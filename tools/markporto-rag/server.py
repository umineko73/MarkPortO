# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 umineko73
#
# This file is part of MarkPortO.
#
# MarkPortO is free software: you can redistribute it and/or modify it under the terms of
# the GNU General Public License, version 3 only, as published by the Free Software Foundation.
#
# MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
# without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License along with MarkPortO.
# If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

"""An MCP server that searches the chunks for RAG that MarkPortO writes (<name>_chunks.jsonl, FR-152).

The chunks of every *_chunks.jsonl under the folders of the configuration are kept in memory (no database). A search mixes a keyword
search (BM25 over pairs of characters and over words) with a search by meaning, when an embedding API (OpenAI compatible, such as LM
Studio or Ollama) can be reached. The source of each chunk (file, pages, headings, page images) is given by the server, not by the model.

Configuration (JSON): the file named by MARKPORTO_RAG_CONFIG, else %APPDATA%\\MarkPortO\\markporto-rag.json, else markporto-rag.json next
to this file. MARKPORTO_RAG_ROOT (folders separated by ';') replaces "roots". See markporto-rag.sample.json and README.md.

Start: uv run --no-project --with mcp python server.py
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import sys
import time
import urllib.request
from collections import Counter
from pathlib import Path

try:  # mcp 2.x renamed FastMCP to MCPServer; the use is the same
    from mcp.server.mcpserver import MCPServer as FastMCP
except ImportError:
    from mcp.server.fastmcp import FastMCP

SCHEMA_VERSION = 1  # the version of the lines of _chunks.jsonl that this server reads

HERE = Path(__file__).resolve().parent
DEFAULTS = {
    "roots": [],
    "embedding": {
        "enabled": True,
        "url": "http://localhost:1234/v1",
        "model": "text-embedding-multilingual-e5-small",
        "apiKeyEnv": "",
        "queryPrefix": "query: ",
        "passagePrefix": "passage: ",
        "passageChars": 1500,
        "timeoutSeconds": 120,
        "retrySeconds": 30,
    },
    "cacheDir": "",
    "textChars": 1500,
    "maxResults": 20,
}


def log(*args):
    print(*args, file=sys.stderr, flush=True)


def config_path() -> Path | None:
    candidates = []
    if os.environ.get("MARKPORTO_RAG_CONFIG"):
        candidates.append(Path(os.environ["MARKPORTO_RAG_CONFIG"]))
    if os.environ.get("APPDATA"):
        candidates.append(Path(os.environ["APPDATA"]) / "MarkPortO" / "markporto-rag.json")
    candidates.append(HERE / "markporto-rag.json")
    return next((p for p in candidates if p.is_file()), None)


CONFIG_ERROR: str | None = None  # a configuration that could not be read: every tool says so


def load_config() -> dict:
    global CONFIG_ERROR
    config = json.loads(json.dumps(DEFAULTS))
    path = config_path()
    if path:
        try:
            given = json.loads(path.read_text(encoding="utf-8-sig"))
            if not isinstance(given, dict):
                raise ValueError("the top level is not an object { ... }")
            for key, value in given.items():
                if isinstance(value, dict) and isinstance(config.get(key), dict):
                    config[key].update(value)
                else:
                    config[key] = value
            if isinstance(config["roots"], str):  # one folder written without [ ]
                config["roots"] = [config["roots"]]
            log(f"markporto-rag: configuration {path}")
        except (OSError, ValueError) as ex:
            CONFIG_ERROR = f"The configuration {path} could not be read: {ex}"
            log("markporto-rag: " + CONFIG_ERROR)
            config = json.loads(json.dumps(DEFAULTS))
    if os.environ.get("MARKPORTO_RAG_ROOT"):
        config["roots"] = [p for p in os.environ["MARKPORTO_RAG_ROOT"].split(";") if p.strip()]
    return config


CONFIG = load_config()
ROOTS = [Path(os.path.expandvars(p)) for p in CONFIG["roots"]]
EMB = CONFIG["embedding"]
CACHE = Path(os.path.expandvars(CONFIG["cacheDir"])) if CONFIG["cacheDir"] else Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "markporto-rag" / "cache"

def root_labels(roots: list[Path]) -> list[str]:
    """What goes before the key of a chunk for each folder searched, so that the same place in two folders gives two keys: nothing with
    one folder (the keys stay as they were), else the name of the folder ("out-2" and so on when two folders have the same name)."""
    if len(roots) < 2:
        return [""] * len(roots)
    labels: list[str] = []
    taken: set[str] = set()
    for r in roots:
        name = r.name or str(r).rstrip("\\/:") or "root"
        label, n = name, 1
        while label.lower() in taken:  # "out", "out", "out-2": the second is "out-3", not a second "out-2"
            n += 1
            label = f"{name}-{n}"
        taken.add(label.lower())
        labels.append(label)
    return labels


LABELS = root_labels(ROOTS)

mcp = FastMCP("markporto-rag")


# ---- reading ----------------------------------------------------------------

def tokens(s: str) -> list[str]:
    """Pairs of characters (for Japanese, which has no spaces) and words (for English)."""
    low = s.lower()
    compact = re.sub(r"\s+", "", low)
    pairs = [compact[i:i + 2] for i in range(len(compact) - 1)]
    words = ["w:" + w for w in re.findall(r"[a-z0-9_]{2,}", low)]
    return pairs + words


class OtherModel(Exception):
    """The embedding API answered with a model other than the one asked for (LM Studio answers with the embedding model that is loaded)."""


def same_model(asked: str, answered) -> bool:
    """The names name the same model: "name:latest" and "name" are the same, and so are "publisher/name" and "name" (one of them does not
    say the publisher); "a/name" and "b/name" are not. An API that does not say which model answered is believed."""
    if not isinstance(answered, str) or not answered.strip():
        return True

    def parts(s: str) -> tuple[str | None, str]:
        s = s.strip().lower()
        if s.endswith(":latest"):
            s = s[:-len(":latest")]
        publisher, _, name = s.rpartition("/")
        return (publisher or None), name

    (p1, n1), (p2, n2) = parts(asked), parts(answered)
    return n1 == n2 and (p1 is None or p2 is None or p1 == p2)


def answered_name(model) -> str | None:
    """The name of the model that answered, to be compared with another answer as it is (the publisher too): None when the API does not say."""
    if not isinstance(model, str) or not model.strip():
        return None
    s = model.strip().lower()
    return s[:-len(":latest")] if s.endswith(":latest") else s


def fullest(names) -> str | None:
    """Of names of one model ("e5", "org-a/e5"), the one that says the most: with its publisher, so that "org-b/e5" is not taken for it."""
    names = [n for n in names if n]
    return max(names, key=lambda n: ("/" in n, len(n), n)) if names else None


def embed(texts: list[str]) -> tuple[list[list[float]], str | None]:
    """The vectors of the texts, and the model that answered (as the API says it)."""
    headers = {"Content-Type": "application/json"}
    if EMB.get("apiKeyEnv") and os.environ.get(EMB["apiKeyEnv"]):
        headers["Authorization"] = "Bearer " + os.environ[EMB["apiKeyEnv"]]
    req = urllib.request.Request(
        EMB["url"].rstrip("/") + "/embeddings",
        data=json.dumps({"model": EMB["model"], "input": texts}).encode("utf-8"),
        headers=headers)
    with urllib.request.urlopen(req, timeout=EMB["timeoutSeconds"]) as r:
        answer = json.load(r)
    if not same_model(EMB["model"], answer.get("model")):
        raise OtherModel(f"the embedding API answered with the model {answer.get('model')}, not {EMB['model']} (is {EMB['model']} there?)")
    data = answer["data"]
    if len(data) != len(texts):
        raise ValueError(f"the embedding API gave {len(data)} vectors for {len(texts)} texts")
    return [d["embedding"] for d in sorted(data, key=lambda d: d["index"])], answered_name(answer.get("model"))


def cos(a, b) -> float:
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    return sum(x * y for x, y in zip(a, b)) / (na * nb) if na and nb else 0.0


class Index:
    def __init__(self):
        self.stamp: tuple | None = None  # not read yet (an empty tuple would be "no files", which needs its warnings too)
        self.chunks: list[dict] = []
        self.by_key: dict[str, dict] = {}
        self.terms: list[Counter] = []
        self.df: Counter = Counter()
        self.avg = 1.0
        self.vectors: list = []
        self.embedding_ok = False
        self.dimension: int | None = None
        self.answered: str | None = None  # the model that made the vectors kept, as the API said it (None: it did not say)
        self.warnings: list[str] = []
        self.file_warnings: list[str] = []
        self.embedding_warnings: list[str] = []
        self.retry_at = 0.0  # when the embeddings are tried again, after the API could not be reached

    def files(self) -> tuple[list[Path], list[str]]:
        found: dict[str, Path] = {}
        warnings = []
        for root in ROOTS:
            if not root.is_dir():
                warnings.append(f"{root}: not a folder that is there; not searched")
                continue
            for p in root.rglob("*_chunks.jsonl"):
                if ".markporto" not in p.parts:
                    found.setdefault(os.path.normcase(str(p.resolve())), p)  # roots inside one another: each file once
        return sorted(found.values()), warnings

    def refresh(self):
        files, warnings = self.files()
        stamp = []
        for p in files:
            try:
                st = p.stat()
                stamp.append((str(p), st.st_mtime_ns, st.st_size))
            except OSError:  # gone since it was found
                pass
        stamp = tuple(stamp)
        if stamp == self.stamp:
            if self.vectors_missing() and time.monotonic() >= self.retry_at:
                self.embed_chunks([Path(s[0]) for s in stamp])  # the API could not be reached before: it may be there now
            return
        chunks = []
        for path in [Path(s[0]) for s in stamp]:
            root_index = max((i for i, r in enumerate(ROOTS) if path.is_relative_to(r)), key=lambda i: len(ROOTS[i].parts), default=None)
            rel = path.relative_to(ROOTS[root_index]).as_posix() if root_index is not None else path.name
            label = LABELS[root_index] if root_index is not None else ""
            base = f"{label}/{rel}" if label else rel  # the document part of the keys: one for each file, though two folders have the same place
            try:
                lines = path.read_text(encoding="utf-8-sig").splitlines()
            except (OSError, UnicodeDecodeError) as ex:
                warnings.append(f"{path}: could not be read ({ex}); skipped")
                continue
            bad = 0
            ids = set()
            for line in lines:
                if not line.strip():
                    continue
                try:
                    c = json.loads(line)
                except ValueError:
                    bad += 1
                    continue
                if not isinstance(c, dict):
                    bad += 1
                    continue
                version = c.get("schemaVersion", 0)
                if not isinstance(version, int) or isinstance(version, bool):
                    bad += 1
                    continue
                if version > SCHEMA_VERSION:
                    warnings.append(f"{path}: schemaVersion {version} is newer than this server ({SCHEMA_VERSION}); skipped")
                    break
                if not usable(c) or c["id"] in ids:
                    bad += 1
                    continue
                ids.add(c["id"])
                c["base"] = base
                c["rel"] = rel
                c["key"] = f"{base}#{c['id']}"
                c["file"] = str(path)
                c["folder"] = str(path.parent)
                chunks.append(c)
            if bad:
                warnings.append(f"{path}: {bad} line(s) that are not chunks of MarkPortO were skipped")
        self.chunks = chunks
        self.file_warnings = warnings
        self.by_key = {c["key"]: c for c in chunks}
        self.terms = [Counter(tokens(" ".join(c["headings"]) + "\n" + c["text"])) for c in chunks]
        self.df = Counter(t for d in self.terms for t in d)
        self.avg = (sum(sum(d.values()) for d in self.terms) / len(self.terms)) if self.terms else 1.0
        self.stamp = stamp
        self.embed_chunks(files)

    def vectors_missing(self) -> bool:
        """Some chunks have no embedding because the API could not be reached (not because the embeddings are off)."""
        return EMB.get("enabled", True) and bool(self.chunks) and any(v is None for v in self.vectors)

    def embed_chunks(self, files: list[Path]):
        self.embedding_warnings = []
        reached = True
        answered: set[str] = set()
        if EMB.get("enabled", True):
            self.vectors, reached, answered = self.load_vectors(files)
        else:
            self.vectors = [None] * len(self.chunks)
        self.embedding_ok = bool(self.chunks) and all(v is not None for v in self.vectors)
        lengths = {len(v) for v in self.vectors if v is not None}
        if len(lengths) > 1:  # vectors of two models: the search by meaning would compare things that do not go together
            self.embedding_warnings.append(f"the embeddings have different dimensions {sorted(lengths)}; the search by meaning is off")
            self.embedding_ok = False
        if any(not same_model(x, y) for x in answered for y in answered):  # two models of the same dimension ("a/e5", "b/e5") do not go together either
            self.embedding_warnings.append(f"the embeddings were made by different models {sorted(answered)}; the search by meaning is off")
            self.embedding_ok = False
        self.dimension = next(iter(lengths)) if len(lengths) == 1 else None
        self.answered = fullest(answered) if self.embedding_ok else None
        self.retry_at = time.monotonic() + float(EMB.get("retrySeconds", 30)) if not reached else 0.0  # tried again at a later search
        self.warnings = self.file_warnings + self.embedding_warnings
        for w in self.warnings:
            log("markporto-rag: " + w)
        log(f"markporto-rag: {len(files)} files, {len(self.chunks)} chunks, embeddings: {self.embedding_ok}")

    def load_vectors(self, files: list[Path]) -> tuple[list, bool, set[str]]:
        """The vectors of the chunks, in their order (None where there is none), whether the API could be reached when it was needed, and
        the models that made them (as the API said it)."""
        vectors: list = []
        answered: set[str] = set()
        reachable = True
        signature = json.dumps([EMB["url"].rstrip("/"), EMB["model"], EMB["passagePrefix"], EMB["passageChars"]]).encode()
        for path in files:
            mine = [c for c in self.chunks if c["file"] == str(path)]
            if not mine:
                continue
            try:
                digest = hashlib.sha1(path.read_bytes() + signature).hexdigest()
            except OSError:
                vectors += [None] * len(mine)
                continue
            cache = CACHE / (hashlib.sha1(str(path).encode()).hexdigest() + ".json")
            got = None
            model = None
            if cache.exists():
                try:
                    saved = json.loads(cache.read_text(encoding="utf-8"))
                    kept = saved["vectors"]
                    if saved.get("digest") == digest and isinstance(kept, list) and len(kept) == len(mine) and all(isinstance(v, list) and v for v in kept)                             and "model" in saved:  # kept before the model that answered was kept: made again once
                        got = kept
                        model = answered_name(saved.get("model"))
                except (OSError, ValueError, KeyError, TypeError, AttributeError):
                    got = None  # a broken cache is made again
            if got is None and reachable:
                try:
                    texts = [EMB["passagePrefix"] + " > ".join(c["headings"]) + "\n" + c["text"][:EMB["passageChars"]] for c in mine]
                    got = []
                    models = set()
                    for i in range(0, len(texts), 16):
                        part, by = embed(texts[i:i + 16])
                        got += part
                        if by:
                            models.add(by)
                    if any(not same_model(x, y) for x in models for y in models):
                        raise OtherModel(f"the embedding API answered with the models {sorted(models)} for one document")
                    model = fullest(models)
                    CACHE.mkdir(parents=True, exist_ok=True)
                    cache.write_text(json.dumps({"digest": digest, "model": model, "vectors": got}), encoding="utf-8")
                except OtherModel as ex:  # vectors of another model would be kept as if they were of this one
                    self.embedding_warnings.append(str(ex) + "; the search by meaning is off")
                    reachable = False
                    got = None
                except Exception as ex:  # no embeddings: the keyword search alone, and the API is tried again later
                    log(f"markporto-rag: no embeddings ({ex})")
                    reachable = False
                    got = None
            if got is not None and model:
                answered.add(model)
            vectors += got if got is not None else [None] * len(mine)
        return vectors, reachable, answered


def is_str_list(v) -> bool:
    return isinstance(v, list) and all(isinstance(x, str) for x in v)


def usable(c: dict) -> bool:
    """A line has what the server reads, of the right types: an id, a source, a Markdown, headings, a kind and a text; pages, page images,
    prev / next, path and sheet when they are there. A line that does not is skipped (with a warning), so that it cannot stop a search."""
    optional_str = ("prev", "next", "path", "sheet")
    return (isinstance(c.get("id"), str) and c["id"] != "" and isinstance(c.get("source"), str) and isinstance(c.get("markdown"), str)
            and is_str_list(c.get("headings")) and isinstance(c.get("kind"), str) and isinstance(c.get("text"), str)
            and (c.get("pages") is None or (isinstance(c["pages"], list) and all(isinstance(p, int) and not isinstance(p, bool) for p in c["pages"])))
            and is_str_list(c.get("pageImages", []))
            and all(c.get(k) is None or isinstance(c[k], str) for k in optional_str))


def where(c: dict) -> str:
    """The path of the document in the folder that was converted ("v1.2/設計書.docx"). A chunk written before MarkPortO kept it has none:
    then the place of its file in the searched folder, which follows the folders that were converted (the default layout)."""
    if isinstance(c.get("path"), str) and c["path"]:
        return c["path"].replace("\\", "/")
    return c["rel"]


INDEX = Index()


def view(c: dict, score: float | None = None, full: bool = False) -> dict:
    """A chunk with its source. The paths are absolute, so that the page image and the Markdown can be opened."""
    limit = CONFIG["textChars"]
    text = c["text"] if full or len(c["text"]) <= limit else c["text"][:limit] + "…"
    base = c["base"]
    out = {
        "key": c["key"],
        "source": c["source"],
        "path": where(c),
        "sourceModified": c.get("sourceModified"),
        "markdown": str(Path(c["folder"]) / c["markdown"]),
        "pages": c.get("pages"),
        "headings": c["headings"],
        "kind": c["kind"],
        "text": text,
        "pageImages": [str(Path(c["folder"]) / p) for p in c.get("pageImages", [])],
        "prev": f"{base}#{c['prev']}" if c.get("prev") else None,
        "next": f"{base}#{c['next']}" if c.get("next") else None,
    }
    if c.get("sheet"):
        out["sheet"] = c["sheet"]
    if score is not None:
        out["score"] = round(score, 4)
    return out


def no_roots() -> dict | None:
    if CONFIG_ERROR:
        return {"error": CONFIG_ERROR}
    if ROOTS:
        return None
    return {"error": "No folder to search. Write \"roots\" in the configuration (markporto-rag.json) or set MARKPORTO_RAG_ROOT."}


# ---- tools ------------------------------------------------------------------

@mcp.tool()
def search_docs(query: str, k: int = 5, source: str | None = None, path: str | None = None) -> dict:
    """Search the documents converted by MarkPortO (design documents, manuals ...) for the chunks that answer a question.

    The keyword search and, when it can be used, the search by meaning are mixed; the best k are returned. Each result has its
    source: "source" (the original file), "path" (where it is in the folder that was converted, e.g. "v1.2/設計書.docx"), "pages",
    "headings" (where it is in the document) and "pageImages" (the printed pages). Give these sources in the answer; do not make up
    page numbers. Read the chunks before and after with read_chunk (prev / next) when the context is needed.
    "source" limits the search to the documents whose file name contains it; "path" to those whose path has it as whole folders (a part of a name when none has), e.g. a
    folder of a version ("v1.2") when the documents are kept in a folder for each version - search one version, not all of them
    mixed, and say which version an answer comes from.
    """
    if (error := no_roots()) is not None:
        return error
    if not query or not query.strip():
        return {"error": "The question (query) is empty."}
    INDEX.refresh()
    folder_only = bool(path) and path.strip().replace("\\", "/").endswith("/")  # "v1.2/": that folder, never a part of a name
    needle = path.replace("\\", "/").strip().strip("/").lower() if path and path.strip() else None
    partial = None
    idx = [i for i, c in enumerate(INDEX.chunks) if not source or source.lower() in c["source"].lower()]
    if needle:
        # Whole folders and names first, so that "v1.2" is not also "v1.20"; a part of a name ("設計") when nothing is whole.
        # A folder is followed by "/"; without "/" at the end, a whole name of a file is matched too.
        whole = re.compile(r"(?:^|/)" + re.escape(needle) + (r"/" if folder_only else r"(?:/|$)"))
        exact = [i for i in idx if whole.search(where(INDEX.chunks[i]).lower())]
        if exact or folder_only:
            idx = exact
        else:
            idx = [i for i in idx if needle in where(INDEX.chunks[i]).lower()]
            if idx:
                names = sorted({where(INDEX.chunks[i]) for i in idx})
                partial = (f"No folder or name is \"{path.strip()}\" as a whole; these were found by a part of their names: "
                           + ", ".join(names[:5]) + (" ..." if len(names) > 5 else "") + f". Write \"{needle}/\" for that folder only.")
    if not idx:
        return {"mode": "none", "results": [], "note": "No chunks (check the folders, or the values of source and path)."}

    q = set(tokens(query))
    n = len(INDEX.chunks)
    bm25 = []
    for i in idx:
        d = INDEX.terms[i]
        length = sum(d.values())
        s = 0.0
        for t in q:
            if t in d:
                idf = math.log(1 + (n - INDEX.df[t] + 0.5) / (INDEX.df[t] + 0.5))
                tf = d[t]
                s += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * length / INDEX.avg))
        if s > 0:  # a chunk with none of the words of the question is not found by the keyword search
            bm25.append((s, i))
    bm25.sort(reverse=True)
    ranks = [{i: r for r, (_, i) in enumerate(bm25)}]

    mode = "keyword"
    note = None
    if INDEX.embedding_ok:
        try:
            vectors, by = embed([EMB["queryPrefix"] + query])
            qv = vectors[0]
            if INDEX.answered and not same_model(INDEX.answered, by):
                note = f"The question was embedded by {by}, the documents by {INDEX.answered}: their vectors do not go together. Keywords only."
                log("markporto-rag: " + note)
            elif len(qv) != INDEX.dimension:
                # Another model answered (LM Studio answers with the embedding model that is loaded): its vectors do not go with those kept.
                note = f"The embedding API gave {len(qv)} dimensions, the kept embeddings have {INDEX.dimension}: another model answered. Keywords only."
                log("markporto-rag: " + note)
            else:
                sem = sorted(((cos(qv, INDEX.vectors[i]), i) for i in idx), reverse=True)
                ranks.append({i: r for r, (_, i) in enumerate(sem)})
                mode = "hybrid"
        except OtherModel as ex:
            note = f"{ex}. Keywords only."
            log("markporto-rag: " + note)
        except Exception as ex:
            log(f"markporto-rag: query embedding failed ({ex})")

    top = max(1, min(k, CONFIG["maxResults"]))
    found = [i for i in idx if any(i in rk for rk in ranks)]
    if not found:
        none = "No chunk has the words of the question. Try other words; if it is still not found, it is not in the documents."
        return {"mode": mode, "results": [], "note": f"{note} {none}" if note else none}
    fused = sorted(((sum(1 / (60 + rk[i]) for rk in ranks if i in rk), i) for i in found), reverse=True)[:top]
    result = {"mode": mode, "results": [view(INDEX.chunks[i], s) for s, i in fused]}
    notes = [n for n in (partial, note) if n]
    if notes:
        result["note"] = " ".join(notes)
    return result


@mcp.tool()
def read_chunk(key: str, neighbors: int = 1) -> dict:
    """Read a chunk by the key that search_docs gave (or its prev / next), with up to 3 chunks before and after it. The text is whole."""
    if (error := no_roots()) is not None:
        return error
    INDEX.refresh()
    c = INDEX.by_key.get(key)
    if c is None:
        return {"error": f"No chunk with the key {key}"}
    steps = max(0, min(neighbors, 3))
    before, after = [], []
    cur = c
    for _ in range(steps):
        cur = INDEX.by_key.get(f"{cur['base']}#{cur['prev']}") if cur.get("prev") else None
        if not cur:
            break
        before.insert(0, view(cur, full=True))
    cur = c
    for _ in range(steps):
        cur = INDEX.by_key.get(f"{cur['base']}#{cur['next']}") if cur.get("next") else None
        if not cur:
            break
        after.append(view(cur, full=True))
    return {"chunk": view(c, full=True), "before": before, "after": after}


@mcp.tool()
def list_documents() -> dict:
    """The documents that are searched (the original file, its path in the folder that was converted, the number of chunks, when the file
    was changed, its Markdown), and the embedding
    in use: the model of the configuration and the dimension of the vectors kept, which tells which model really made them."""
    if (error := no_roots()) is not None:
        return error
    INDEX.refresh()
    docs: dict[str, dict] = {}
    for c in INDEX.chunks:
        d = docs.setdefault(c["file"], {"source": c["source"], "path": where(c), "markdown": str(Path(c["folder"]) / c["markdown"]),
                                        "sourceModified": c.get("sourceModified"), "chunks": 0})
        d["chunks"] += 1
    embedding = {
        "available": INDEX.embedding_ok,  # the search by meaning is used
        "model": EMB["model"] if EMB.get("enabled", True) else None,
        "url": EMB["url"] if EMB.get("enabled", True) else None,
        "dimension": INDEX.dimension,  # of the kept vectors: it tells which model made them (e5-small 384, e5-base 768 ...)
    }
    return {"roots": [str(r) for r in ROOTS], "embedding": embedding, "warnings": INDEX.warnings, "documents": list(docs.values())}


if __name__ == "__main__":
    mcp.run()
