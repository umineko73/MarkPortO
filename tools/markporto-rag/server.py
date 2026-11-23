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

mcp = FastMCP("markporto-rag")


# ---- reading ----------------------------------------------------------------

def tokens(s: str) -> list[str]:
    """Pairs of characters (for Japanese, which has no spaces) and words (for English)."""
    low = s.lower()
    compact = re.sub(r"\s+", "", low)
    pairs = [compact[i:i + 2] for i in range(len(compact) - 1)]
    words = ["w:" + w for w in re.findall(r"[a-z0-9_]{2,}", low)]
    return pairs + words


def embed(texts: list[str]) -> list[list[float]]:
    headers = {"Content-Type": "application/json"}
    if EMB.get("apiKeyEnv") and os.environ.get(EMB["apiKeyEnv"]):
        headers["Authorization"] = "Bearer " + os.environ[EMB["apiKeyEnv"]]
    req = urllib.request.Request(
        EMB["url"].rstrip("/") + "/embeddings",
        data=json.dumps({"model": EMB["model"], "input": texts}).encode("utf-8"),
        headers=headers)
    with urllib.request.urlopen(req, timeout=EMB["timeoutSeconds"]) as r:
        data = json.load(r)["data"]
    return [d["embedding"] for d in sorted(data, key=lambda d: d["index"])]


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
        self.warnings: list[str] = []

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
            return
        chunks = []
        for path in [Path(s[0]) for s in stamp]:
            root = max((r for r in ROOTS if path.is_relative_to(r)), key=lambda r: len(r.parts), default=path.parent)
            rel = path.relative_to(root).as_posix()
            try:
                lines = path.read_text(encoding="utf-8-sig").splitlines()
            except (OSError, UnicodeDecodeError) as ex:
                warnings.append(f"{path}: could not be read ({ex}); skipped")
                continue
            bad = 0
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
                if c.get("schemaVersion", 0) > SCHEMA_VERSION:
                    warnings.append(f"{path}: schemaVersion {c.get('schemaVersion')} is newer than this server ({SCHEMA_VERSION}); skipped")
                    break
                if not usable(c):
                    bad += 1
                    continue
                c["key"] = f"{rel}#{c['id']}"
                c["file"] = str(path)
                c["folder"] = str(path.parent)
                chunks.append(c)
            if bad:
                warnings.append(f"{path}: {bad} line(s) that are not chunks of MarkPortO were skipped")
        self.chunks = chunks
        self.warnings = warnings
        self.by_key = {c["key"]: c for c in chunks}
        self.terms = [Counter(tokens(" ".join(c["headings"]) + "\n" + c["text"])) for c in chunks]
        self.df = Counter(t for d in self.terms for t in d)
        self.avg = (sum(sum(d.values()) for d in self.terms) / len(self.terms)) if self.terms else 1.0
        self.vectors = self.load_vectors(files) if EMB.get("enabled", True) else [None] * len(chunks)
        self.embedding_ok = bool(chunks) and all(v is not None for v in self.vectors)
        lengths = {len(v) for v in self.vectors if v is not None}
        if len(lengths) > 1:  # vectors of two models: the search by meaning would compare things that do not go together
            warnings.append(f"the embeddings have different dimensions {sorted(lengths)}; the search by meaning is off")
            self.embedding_ok = False
        self.dimension = next(iter(lengths)) if len(lengths) == 1 else None
        self.stamp = stamp
        for w in warnings:
            log("markporto-rag: " + w)
        log(f"markporto-rag: {len(files)} files, {len(chunks)} chunks, embeddings: {self.embedding_ok}")

    def load_vectors(self, files: list[Path]) -> list:
        vectors: list = []
        reachable = True
        signature = json.dumps([EMB["model"], EMB["passagePrefix"], EMB["passageChars"]]).encode()
        for path in files:
            mine = [c for c in self.chunks if c["file"] == str(path)]
            try:
                digest = hashlib.sha1(path.read_bytes() + signature).hexdigest()
            except OSError:
                vectors += [None] * len(mine)
                continue
            cache = CACHE / (hashlib.sha1(str(path).encode()).hexdigest() + ".json")
            got = None
            if cache.exists():
                try:
                    saved = json.loads(cache.read_text(encoding="utf-8"))
                    if saved.get("digest") == digest and len(saved["vectors"]) == len(mine):
                        got = saved["vectors"]
                except (OSError, ValueError, KeyError, TypeError, AttributeError):
                    got = None  # a broken cache is made again
            if got is None and reachable and mine:
                try:
                    texts = [EMB["passagePrefix"] + " > ".join(c["headings"]) + "\n" + c["text"][:EMB["passageChars"]] for c in mine]
                    got = []
                    for i in range(0, len(texts), 16):
                        got += embed(texts[i:i + 16])
                    CACHE.mkdir(parents=True, exist_ok=True)
                    cache.write_text(json.dumps({"digest": digest, "vectors": got}), encoding="utf-8")
                except Exception as ex:  # no embeddings: the keyword search alone
                    log(f"markporto-rag: no embeddings ({ex})")
                    reachable = False
                    got = None
            vectors += got if got is not None else [None] * len(mine)
        return vectors


def usable(c: dict) -> bool:
    """A line has what the server reads: an id, a source, a Markdown, headings and a text."""
    return (isinstance(c.get("id"), str) and isinstance(c.get("source"), str) and isinstance(c.get("markdown"), str)
            and isinstance(c.get("headings"), list) and isinstance(c.get("text"), str)
            and (c.get("pages") is None or isinstance(c.get("pages"), list)) and isinstance(c.get("pageImages", []), list))


def where(c: dict) -> str:
    """The path of the document in the folder that was converted ("v1.2/設計書.docx"). A chunk written before MarkPortO kept it has none:
    then the place of its file in the searched folder, which follows the folders that were converted (the default layout)."""
    if isinstance(c.get("path"), str) and c["path"]:
        return c["path"].replace("\\", "/")
    return c["key"].split("#")[0]


INDEX = Index()


def view(c: dict, score: float | None = None, full: bool = False) -> dict:
    """A chunk with its source. The paths are absolute, so that the page image and the Markdown can be opened."""
    limit = CONFIG["textChars"]
    text = c["text"] if full or len(c["text"]) <= limit else c["text"][:limit] + "…"
    base = c["key"].split("#")[0]
    out = {
        "key": c["key"],
        "source": c["source"],
        "path": where(c),
        "sourceModified": c.get("sourceModified"),
        "markdown": str(Path(c["folder"]) / c["markdown"]),
        "pages": c["pages"],
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
    "source" limits the search to the documents whose file name contains it; "path" to those whose path contains it, e.g. a
    folder of a version ("v1.2") when the documents are kept in a folder for each version - search one version, not all of them
    mixed, and say which version an answer comes from.
    """
    if (error := no_roots()) is not None:
        return error
    if not query or not query.strip():
        return {"error": "The question (query) is empty."}
    INDEX.refresh()
    needle = path.replace("\\", "/").strip("/").lower() if path and path.strip() else None
    idx = [i for i, c in enumerate(INDEX.chunks)
           if (not source or source.lower() in c["source"].lower()) and (not needle or needle in where(c).lower())]
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
        bm25.append((s, i))
    bm25.sort(reverse=True)
    ranks = [{i: r for r, (_, i) in enumerate(bm25)}]

    mode = "keyword"
    note = None
    if INDEX.embedding_ok:
        try:
            qv = embed([EMB["queryPrefix"] + query])[0]
            if len(qv) != INDEX.dimension:
                # Another model answered (LM Studio answers with the embedding model that is loaded): its vectors do not go with those kept.
                note = f"The embedding API gave {len(qv)} dimensions, the kept embeddings have {INDEX.dimension}: another model answered. Keywords only."
                log("markporto-rag: " + note)
            else:
                sem = sorted(((cos(qv, INDEX.vectors[i]), i) for i in idx), reverse=True)
                ranks.append({i: r for r, (_, i) in enumerate(sem)})
                mode = "hybrid"
        except Exception as ex:
            log(f"markporto-rag: query embedding failed ({ex})")

    top = max(1, min(k, CONFIG["maxResults"]))
    fused = sorted(((sum(1 / (60 + rk[i]) for rk in ranks), i) for i in idx), reverse=True)[:top]
    result = {"mode": mode, "results": [view(INDEX.chunks[i], s) for s, i in fused]}
    if note:
        result["note"] = note
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
        cur = INDEX.by_key.get(f"{cur['key'].split('#')[0]}#{cur['prev']}") if cur.get("prev") else None
        if not cur:
            break
        before.insert(0, view(cur, full=True))
    cur = c
    for _ in range(steps):
        cur = INDEX.by_key.get(f"{cur['key'].split('#')[0]}#{cur['next']}") if cur.get("next") else None
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
