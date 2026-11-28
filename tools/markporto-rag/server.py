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

Start: uv run --no-project --with mcp --with numpy python server.py (numpy is optional: without it, the search by meaning is slower)
With --build-cache, it embeds the chunks that have no kept vectors yet, keeps them, and ends (no MCP): see build_cache().
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import contextlib
import functools
import operator
import atexit
import sys
import tempfile
import threading
import time
import urllib.parse
import urllib.request
from array import array
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
        "queryParams": {},  # more of the request, for a question: {"input_type": "query"} for Voyage AI
        "passageParams": {},  # ... for the text of a chunk: {"input_type": "document"}
        "passageChars": 400,  # a chunk longer than this is embedded in parts of it (e5-small reads 512 tokens; a cloud model reads more)
        # "passageOverlap": the characters of the end of a part that the next one begins with; not written: 50, less for a short passageChars
        "timeoutSeconds": 120,
        "retrySeconds": 30,
        "batchSize": 64,
        "waitSeconds": 10,
    },
    "rerank": {  # a model that puts the best results in order again (a cloud API: Voyage AI, Cohere, Jina ...); off by default
        "enabled": False,
        "url": "",
        "model": "",
        "apiKeyEnv": "",
        "candidates": 30,
        "timeoutSeconds": 30,
    },
    "cacheDir": "",
    "textChars": 1500,
    "readChars": 20000,  # read_chunk: the most text of the chunks around the one asked for
    "maxResults": 20,
    "meaningCandidates": 50,  # the chunks nearest by meaning that a search takes (not every chunk: those far from the question are not results)
    "recheckSeconds": 300,  # the files are compared by their bytes this often too (a copy that keeps the size and the time); 0: never
}


KEEP_ERRORS: list[str] = []  # kept files that could not be written (--build-cache does not end as a success then)
LOGGED: set[str] | None = None  # --build-cache: the lines already written (the index writes its warnings again at each change)


def log(*args):
    if LOGGED is not None:
        line = " ".join(str(a) for a in args)
        if line in LOGGED:
            return
        LOGGED.add(line)
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
            if not isinstance(config.get("embedding"), dict):
                raise ValueError("\"embedding\" must be an object { ... }")
            if not isinstance(config.get("rerank"), dict):
                raise ValueError("\"rerank\" must be an object { ... }")
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
RERANK = CONFIG["rerank"]
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

# The tools run in threads, and a search can wait for the embedding API. One tool at a time (COND, below), so that a search reads one
# index from start to end, and two rebuilds of the index cannot cross (the server has one user; a call waits for the one before). The
# embedding thread takes COND only to pick a file and to keep its vectors.


def one_at_a_time(fn):
    @functools.wraps(fn)
    def run(*args, **kwargs):
        with TOOLS, COND:
            return fn(*args, **kwargs)
    return run


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


def valid_vectors(vectors, count: int) -> bool:
    """count vectors of one length, each of finite numbers (not true/false): what the API gave and what was kept are checked alike."""
    if not isinstance(vectors, list) or len(vectors) != count:
        return False
    length = None
    for v in vectors:
        if not isinstance(v, list) or not v or (length is not None and len(v) != length):
            return False
        length = len(v)
        for x in v:
            if isinstance(x, bool) or not isinstance(x, (int, float)):
                return False
            try:
                f = float(x)
            except OverflowError:  # an integer too large for a float (10**400)
                return False
            if not math.isfinite(f) or abs(f) > 1e100:  # no embedding has such values, and their squares would not fit a float
                return False
    return True


def embed(texts: list[str], params: dict | None = None) -> tuple[list[list[float]], str | None]:
    """The vectors of the texts, and the model that answered (as the API says it). params: more of the request (queryParams, passageParams)."""
    headers = {"Content-Type": "application/json"}
    if EMB.get("apiKeyEnv") and os.environ.get(EMB["apiKeyEnv"]):
        headers["Authorization"] = "Bearer " + os.environ[EMB["apiKeyEnv"]]
    req = urllib.request.Request(
        EMB["url"].rstrip("/") + "/embeddings",
        data=json.dumps({**(params or {}), "model": EMB["model"], "input": texts}).encode("utf-8"),
        headers=headers)
    with urllib.request.urlopen(req, timeout=EMB["timeoutSeconds"]) as r:
        answer = json.load(r)
    if not same_model(EMB["model"], answer.get("model")):
        raise OtherModel(f"the embedding API answered with the model {answer.get('model')}, not {EMB['model']} (is {EMB['model']} there?)")
    data = answer["data"]
    if not isinstance(data, list) or len(data) != len(texts):
        raise ValueError(f"the embedding API gave {len(data) if isinstance(data, list) else 'no'} vectors for {len(texts)} texts")
    indexes = [d.get("index") if isinstance(d, dict) else None for d in data]
    if sorted(i for i in indexes if isinstance(i, int) and not isinstance(i, bool)) != list(range(len(texts))):  # each text once: else a vector would go to another text
        raise ValueError(f"the embedding API gave the indexes {indexes} for {len(texts)} texts")
    vectors = [d["embedding"] for d in sorted(data, key=lambda d: d["index"])]
    if not valid_vectors(vectors, len(texts)):
        raise ValueError("the embedding API gave vectors that are not lists of numbers of one length")
    return vectors, answered_name(answer.get("model"))


try:  # numpy makes the search by meaning fast (a few ms for 20,000 chunks); without it, plain Python does the same, more slowly
    import numpy as np
except ImportError:  # pragma: no cover - the server is started with or without "--with numpy"
    np = None

CACHE_MAGIC = b"MPRAG1\n"
TMP_PREFIX = "markporto-rag."  # the temporary files of kept vectors: only these are cleaned (cacheDir may be a folder of other files too)  # the kept vectors of one file: this line, a line of JSON (digest, model, count, dim), then float32 little-endian


def cos(a, b) -> float:
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    return sum(x * y for x, y in zip(a, b)) / (na * nb) if na and nb else 0.0


def normalized(v) -> list[float] | None:
    """The vector of length 1 (the search by meaning is then a dot product). A vector of length 0 stays 0 (its chunk is as far from every
    question as can be, as before); None only when its length is not a number."""
    n = math.sqrt(sum(x * x for x in v))
    if not math.isfinite(n):
        return None
    return [x / n for x in v] if n > 0 else [0.0 for _ in v]


def as_rows(vectors: list[list[float]]):
    """Normalized vectors as kept in memory: one numpy array (count x dim), or a list of arrays of floats."""
    if np is not None:
        return np.asarray(vectors, dtype=np.float32)
    return [array("f", v) for v in vectors]


def rows_dim(rows) -> int:
    return int(rows.shape[1]) if np is not None else len(rows[0])


def rows_from_bytes(body: bytes, count: int, dim: int):
    """The rows of a kept file, or None when they are not vectors of length 1 or 0 (broken, or written by something else)."""
    if np is not None:
        m = np.frombuffer(body, dtype="<f4").reshape(count, dim).astype(np.float32)
        if not np.isfinite(m).all():
            return None
        norms = np.linalg.norm(m, axis=1)
        if not np.all((np.abs(norms - 1.0) <= 1e-2) | ~m.any(axis=1)):  # length 1, or all 0 (not a tiny broken row)
            return None
        return m
    flat = array("f")
    flat.frombytes(body)
    if sys.byteorder == "big":
        flat.byteswap()
    rows = [flat[k * dim:(k + 1) * dim] for k in range(count)]
    for r in rows:
        if not all(math.isfinite(x) for x in r):
            return None
        n = math.sqrt(sum(x * x for x in r))
        if abs(n - 1.0) > 1e-2 and any(r):  # length 1, or all 0 (not a tiny broken row)
            return None
    return rows


def rows_to_bytes(rows) -> bytes:
    if np is not None:
        return np.asarray(rows, dtype="<f4").tobytes()
    flat = array("f")
    for r in rows:
        flat.extend(r)
    if sys.byteorder == "big":
        flat.byteswap()
    return flat.tobytes()


def cache_file(key: str, suffix: str) -> Path:
    """The kept file of a key: the Entry.cache_key (0.4.8 and later), or the path of the chunks file (0.4.7 and before)."""
    return CACHE / (hashlib.sha1(key.encode()).hexdigest() + suffix)


def embedding_signature() -> bytes:
    """What the vectors of a file depend on besides its text: the API (its URL as written, without a "/" at the end), the model, and how
    the chunks are cut and prefixed. The same as 0.4.7 and before, so the vectors kept then are taken over."""
    return json.dumps([EMB["url"].rstrip("/"), EMB["model"], EMB["passagePrefix"], EMB["passageChars"], passage_overlap(), EMB["passageParams"], "parts 2"],
                      sort_keys=True).encode()


def cache_slot(path: str) -> str:
    """A chunks file as the kept vectors name it: its whole path as Windows compares it (case does not count, a mapped drive is its
    network path). PCs that share a cache find the same vectors when they reach the output folder by the same path and write the same
    URL; otherwise each keeps its own (made once), without writing over another's."""
    try:
        path = os.path.realpath(path)
    except (OSError, ValueError):
        path = os.path.abspath(path)
    return os.path.normcase(path)


def write_cache(key: str, digest: str, model: str | None, rows) -> None:
    """Writes the kept vectors of a file: through a temporary file of its own (two servers may keep the same file at once), then the
    JSON of 0.4.1-0.4.3 goes."""
    head = json.dumps({"digest": digest, "model": model, "count": len(rows), "dim": rows_dim(rows)}).encode()
    target = cache_file(key, ".bin")
    CACHE.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=TMP_PREFIX + target.stem + ".", suffix=".tmp", dir=CACHE)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(CACHE_MAGIC + head + b"\n" + rows_to_bytes(rows))
        for attempt in range(40):  # on Windows a file being replaced (or read) by another is locked a moment
            try:
                os.replace(tmp, target)
                break
            except PermissionError:
                if attempt == 39:
                    raise
                time.sleep(0.025)
    except BaseException:
        with contextlib.suppress(OSError):
            os.unlink(tmp)
        raise
    with contextlib.suppress(OSError):
        cache_file(key, ".json").unlink(missing_ok=True)  # the form of 0.4.1-0.4.3, now kept as binary


def read_binary_cache(path: str, digest: str, count: int):
    try:
        raw = cache_file(path, ".bin").read_bytes()
        if not raw.startswith(CACHE_MAGIC):
            return None
        end = raw.index(b"\n", len(CACHE_MAGIC))
        head = json.loads(raw[len(CACHE_MAGIC):end])
        body = raw[end + 1:]
        dim = head.get("dim")
        if (head.get("digest") != digest or head.get("count") != count or "model" not in head
                or not isinstance(dim, int) or isinstance(dim, bool) or dim <= 0 or len(body) != count * dim * 4):
            return None
        rows = rows_from_bytes(body, count, dim)
        return (rows, answered_name(head.get("model"))) if rows is not None else None
    except Exception:  # missing, broken, or not ours: made again (or taken from the JSON below)
        return None


def read_cache(path: str, digest: str, count: int, keep_as: tuple[str, str] | None = None):
    """(rows, model) kept for a file read from these bytes, or None. A file kept as JSON by 0.4.1-0.4.3 is turned into the binary form
    once, without asking the API again; if the binary file cannot be written, its vectors are used all the same. keep_as (key, digest):
    vectors found under the name and digest of an earlier version are kept under the name and the digest of now (the old file stays,
    for a server of the earlier version)."""
    kept = read_binary_cache(path, digest, count)
    if kept is not None:
        if keep_as:
            try:
                write_cache(keep_as[0], keep_as[1], kept[1], kept[0])
            except OSError as ex:
                KEEP_ERRORS.append(f"{keep_as[0].splitlines()[0]}: {ex}")
                log(f"markporto-rag: the embeddings could not be kept ({ex})")
        return kept
    try:
        saved = json.loads(cache_file(path, ".json").read_text(encoding="utf-8"))
        vectors = saved.get("vectors")
        # Kept before the model that answered was kept ("model"), or not numbers: made again by the API.
        if saved.get("digest") != digest or "model" not in saved or not valid_vectors(vectors, count):
            return None
        norm = [normalized(v) for v in vectors]
        if any(v is None for v in norm):
            return None
        rows, model = as_rows(norm), answered_name(saved.get("model"))
    except Exception:
        return None
    try:
        write_cache(*(keep_as or (path, digest)), model, rows)
        if keep_as:
            cache_file(path, ".json").unlink(missing_ok=True)
    except OSError as ex:
        KEEP_ERRORS.append(f"{(keep_as[0] if keep_as else path).splitlines()[0]}: {ex}")
        log(f"markporto-rag: the embeddings could not be kept as binary ({ex}); the JSON stays")
    return rows, model


def check_rerank_config() -> str | None:
    """What is wrong with the rerank part of the configuration (checked only when it is on), or None."""
    if not RERANK.get("enabled", False):
        return None
    for key in ("url", "model"):
        if not isinstance(RERANK.get(key), str) or not RERANK[key].strip():
            return f"rerank.{key} must be a text that is not empty"
    return None


def passage_overlap() -> int:
    """embedding.passageOverlap, or when it is not written 50 (a quarter of a shorter passageChars)."""
    return EMB["passageOverlap"] if "passageOverlap" in EMB else min(50, EMB["passageChars"] // 4)


def pieces_of(text: str, size: int, overlap: int) -> list[str]:
    """The parts of a text that are embedded, each of size characters at most. A part ends at the end of a line or a sentence in its second
    half (else where it reaches the size); the next part begins overlap characters before that end, so that what is said across the cut
    is whole in one of them. A text of size characters or fewer is one part."""
    if len(text) <= size:
        return [text]
    overlap = max(0, min(overlap, size - 1))
    parts: list[str] = []
    start = 0
    while start < len(text):
        end = min(start + size, len(text))
        if end < len(text):
            window = text[start:end]
            cut = max([window.rfind("\n")] + [window.rfind(mark) for mark in "。．！？.!?"])
            if cut + 1 > max(overlap, size // 2):
                end = start + cut + 1
        parts.append(text[start:end])
        if end >= len(text):
            break
        start = max(end - overlap, start + 1)
    return [p for p in parts if p.strip()] or [text[:size]]


def check_embedding_config() -> str | None:
    """What is wrong with the embedding part of the configuration (checked only when the embeddings are on), or None."""
    if not EMB.get("enabled", True):
        return None
    for key in ("url", "model", "queryPrefix", "passagePrefix"):
        if not isinstance(EMB.get(key), str):
            return f"embedding.{key} must be a text (\"...\")"
    if not EMB["url"].strip() or not EMB["model"].strip():
        return "embedding.url and embedding.model must not be empty"
    chars = EMB.get("passageChars")
    if not isinstance(chars, int) or isinstance(chars, bool) or chars < 1:
        return "embedding.passageChars must be a whole number of 1 or more"
    overlap = EMB.get("passageOverlap")
    if "passageOverlap" in EMB and (not isinstance(overlap, int) or isinstance(overlap, bool) or not (0 <= overlap < chars)):
        return "embedding.passageOverlap must be a whole number from 0 to less than passageChars"
    for key in ("queryParams", "passageParams"):
        if not isinstance(EMB.get(key), dict):
            return f"embedding.{key} must be an object {{ ... }}"
    timeout = EMB.get("timeoutSeconds")
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not (0 < timeout < 1e6):
        return "embedding.timeoutSeconds must be a number of seconds above 0"
    return None


class Entry:
    """One *_chunks.jsonl as it was read: its chunks, their keywords (an inverted index of the file) and their vectors when there are."""

    def __init__(self, path: str, stamp: tuple, read_digest: str, chunks: list[dict], warnings: list[str]):
        self.path = path
        self.stamp = stamp
        self.chunks = chunks
        self.warnings = warnings
        if EMB.get("enabled", True):
            signature = embedding_signature()
            # The digest of 0.4.7 and before: of the bytes the chunks came from (not of the file now). Vectors kept with it are taken over.
            self.byte_digest = hashlib.sha1(read_digest.encode() + signature).hexdigest()
            # The kept file of this file for these settings: two configurations that share cacheDir (another URL, model or prefix) keep
            # their own files instead of writing over each other's (until 0.4.7 it was named by the path alone: PCs that wrote LM Studio
            # as "localhost" and as its address took turns, and each start embedded everything again).
            self.cache_key = cache_slot(path) + "\n" + signature.decode()
        else:  # keywords only: the embedding part of the configuration is not looked at
            self.digest = self.byte_digest = self.cache_key = ""
        terms = [Counter(tokens(" ".join(c["headings"]) + "\n" + c["text"])) for c in chunks]
        self.lengths = [sum(t.values()) for t in terms]
        self.postings: dict[str, list[tuple[int, int]]] = {}
        for j, t in enumerate(terms):
            for term, tf in t.items():
                self.postings.setdefault(term, []).append((j, tf))
        self.df = Counter({term: len(p) for term, p in self.postings.items()})
        self.offset = 0
        self.rows = None  # the normalized vectors of the chunks, when they are all there
        self.model: str | None = None  # the model that made them, as the API said it
        self.done = 0  # chunks of a large file embedded so far (its batches), for the progress
        self.read_hash = ""  # the hash of the bytes it was read from
        self.retry_at = 0.0  # answered by another model: asked for again (this file alone) after this
        self.embed_warning: str | None = None  # why its embeddings could not be made (goes with the file)
        # The parts of the chunks that are embedded (a long chunk in several, each with its headings): which chunk each is of.
        self.parts: list[str] = []
        self.owner: list[int] = []
        if EMB.get("enabled", True):
            for j, c in enumerate(chunks):
                for part in pieces_of(c["text"], EMB["passageChars"], passage_overlap()):
                    self.parts.append(EMB["passagePrefix"] + " > ".join(c["headings"]) + "\n" + part)
                    self.owner.append(j)
            # The digest of the vectors: of the texts sent to the API (and the settings), not of the bytes of the file. A file converted
            # again with the same text (only the time of the document or another field changed) keeps its vectors.
            self.digest = hashlib.sha1(b"texts\n" + json.dumps(self.parts, ensure_ascii=False).encode() + signature).hexdigest()
        self.count = len(self.parts)  # the vectors of the file

    def texts(self) -> list[str]:
        return self.parts


class Index:
    """The index of the documents. COND guards it: the tools hold it while they run (and give it up only while a search waits for the
    embeddings), and the embedding thread takes it to pick files and to keep their vectors, not while it waits for the API. TOOLS keeps
    the tools one at a time, so a search that waits for the embeddings still answers from the index it began with."""

    def __init__(self):
        self.scanned: dict[str, tuple] | None = None  # path -> (mtime, size) of the last scan; None: not scanned yet
        self.entries: dict[str, Entry] = {}
        self.ordered: list[Entry] = []
        self.unread: dict[str, str] = {}  # path -> warning: could not be read (locked a moment); read again at each refresh
        self.scan_warnings: list[str] = []
        self.warnings: list[str] = []
        self.chunks: list[dict] = []
        self.by_key: dict[str, dict] = {}
        self.df: Counter = Counter()
        self.total_len = 0
        self.avg = 1.0
        self.matrix = None
        self.matrix_dirty = True
        self.embedding_ok = False
        self.pending = 0  # chunks without vectors yet
        self.dimension: int | None = None
        self.answered: str | None = None  # the model that made the vectors kept, as the API said it (None: it did not say)
        self.mixed = False  # vectors of two models (names or dimensions): made again without the cache, now and after retrySeconds
        self.retry_at = 0.0  # when vectors of two models are made again (0: not waiting)
        self.remade = False  # the vectors of two models were made again once already; still mixed: wait retry_at
        self.question_remade_at: float | None = None  # the documents were made again for a question of another model, then
        self.writing = 0  # kept files being written (waited for at the end)
        self.api_down_until = 0.0  # the API could not be reached: no call to it (documents or questions) before this; files changing keep it
        self.worker: threading.Thread | None = None
        self.owner = None  # the chunk (in self.chunks) of each row of self.matrix
        self.parts_total = 0  # the vectors of all the files
        self.once = False  # --build-cache: a file that could not be embedded is not tried again (the others get their turn)
        self.recheck_at = 0.0  # the files are compared by their bytes then (recheckSeconds), though their size and time are the same

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
        files, self.scan_warnings = self.files()
        scanned: dict[str, tuple] = {}
        for p in files:
            try:
                st = p.stat()
                scanned[str(p)] = (st.st_mtime_ns, st.st_size, st.st_dev, st.st_ino)  # a file replaced keeping size and time has another id
            except OSError:  # gone since it was found
                pass
        now = time.monotonic()
        recheck = recheck_seconds()
        bytes_too = recheck > 0 and now >= self.recheck_at
        if bytes_too:
            self.recheck_at = now + recheck
        if scanned != self.scanned or self.unread or bytes_too:
            changed = False
            try:
                for path in [p for p in self.entries if p not in scanned]:
                    self.remove(path)
                    changed = True
                for path in [p for p in self.unread if p not in scanned]:
                    del self.unread[path]
                for path, stamp in scanned.items():
                    old = self.entries.get(path)
                    if old is not None and old.stamp == stamp and path not in self.unread:
                        # Written over with its size and time kept (a copy that keeps the time): the bytes tell.
                        try:
                            if hashlib.sha1(Path(path).read_bytes()).hexdigest() == old.read_hash:
                                continue
                        except OSError:
                            continue
                    try:
                        entry = self.read(Path(path), stamp)
                    except Exception as ex:  # whatever is wrong with one file, the others are read and the index stays whole
                        self.unread[path] = f"{path}: could not be read ({ex!r}); skipped"
                        entry = None
                    if old is not None:
                        self.remove(path)
                        changed = True
                    if entry is not None:
                        self.add(entry)
                        changed = True
                self.scanned = scanned
            finally:
                if changed:  # the files changed so far are in the index, though a later one failed
                    self.rebuild()
        self.evaluate()

    def read(self, path: Path, stamp: tuple) -> Entry | None:
        root_index = max((i for i, r in enumerate(ROOTS) if path.is_relative_to(r)), key=lambda i: len(ROOTS[i].parts), default=None)
        rel = path.relative_to(ROOTS[root_index]).as_posix() if root_index is not None else path.name
        label = LABELS[root_index] if root_index is not None else ""
        base = f"{label}/{rel}" if label else rel  # the document part of the keys: one for each file, though two folders have the same place
        try:
            raw = path.read_bytes()
            lines = raw.decode("utf-8-sig").splitlines()
        except (OSError, UnicodeDecodeError) as ex:
            self.unread[str(path)] = f"{path}: could not be read ({ex}); skipped"
            return None
        self.unread.pop(str(path), None)
        warnings: list[str] = []
        chunks: list[dict] = []
        bad = 0
        ids = set()
        for line in lines:
            if not line.strip():
                continue
            try:
                c = json.loads(line)
            except (ValueError, RecursionError):  # not JSON, or nested too deep: that line alone
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
        unsafe = 0
        for c in chunks:
            c["_where"] = where(c).lower()
            c["_source"] = c["source"].lower()
            # The Markdown and the page images are in the folder of the document: a reference out of it is not given as a source.
            c["_markdown_ok"] = inside_folder(c["markdown"], plain_name=True)
            images = [p for p in c.get("pageImages", []) if inside_folder(p)]
            unsafe += (not c["_markdown_ok"]) + len(c.get("pageImages", [])) - len(images)
            c["pageImages"] = images
        if unsafe:
            warnings.append(f"{path}: {unsafe} reference(s) to a Markdown or a page image out of the folder of the document were left out")
        entry = Entry(str(path), stamp, hashlib.sha1(raw).hexdigest(), chunks, warnings)
        entry.read_hash = hashlib.sha1(raw).hexdigest()
        if chunks and EMB.get("enabled", True):
            kept = read_cache(entry.cache_key, entry.digest, entry.count)
            if kept is None:
                # Kept by 0.4.7 and before: the file named by the path alone, with the digest of its bytes: kept under the name and the
                # digest of now.
                kept = read_cache(entry.path, entry.byte_digest, entry.count, keep_as=(entry.cache_key, entry.digest))
            if kept is None and entry.count == len(chunks) and not EMB["passageParams"]:
                # Kept before a chunk was embedded in parts (0.4.5 and before: the first passageChars characters of each, 1500 by default):
                # the same texts when every chunk is one part, as long as it was not cut then.
                longest = max(len(c["text"]) for c in chunks)
                for chars in dict.fromkeys([EMB["passageChars"], 1500]):
                    if longest <= chars:
                        before = json.dumps([EMB["url"].rstrip("/"), EMB["model"], EMB["passagePrefix"], chars]).encode()
                        kept = read_cache(entry.path, hashlib.sha1(entry.read_hash.encode() + before).hexdigest(), entry.count,
                                          keep_as=(entry.cache_key, entry.digest))
                        if kept is not None:
                            break
            if kept is not None:
                entry.rows, entry.model = kept
        return entry


    def add(self, e: Entry):
        self.entries[e.path] = e
        self.df.update(e.df)
        self.total_len += sum(e.lengths)

    def remove(self, path: str):
        e = self.entries.pop(path)
        self.df.subtract(e.df)
        self.total_len -= sum(e.lengths)

    def rebuild(self):
        self.ordered = [self.entries[p] for p in sorted(self.entries, key=Path)]  # as files() orders them (on Windows, without case)
        chunks: list[dict] = []
        for e in self.ordered:
            e.offset = len(chunks)
            chunks += e.chunks
        self.chunks = chunks
        self.by_key = {c["key"]: c for c in chunks}
        self.df = Counter({t: n for t, n in self.df.items() if n > 0})  # the terms that are no longer anywhere go
        self.avg = self.total_len / len(chunks) if chunks else 1.0
        self.matrix = None  # made again from the rows of the files that are there now
        self.matrix_dirty = True

    def remake_all(self):
        """The vectors are made again by the model that answers now, not taken from what was kept."""
        for e in self.ordered:
            e.rows = None
            e.model = None
            e.done = 0
        self.matrix = None
        self.matrix_dirty = True

    def evaluate(self):
        """Whether the search by meaning can be used now; starts the embedding thread when some chunks have no vectors."""
        enabled = EMB.get("enabled", True)
        with_rows = [e for e in self.ordered if e.chunks and e.rows is not None]
        names = {e.model for e in with_rows if e.model}
        dims = {rows_dim(e.rows) for e in with_rows}
        mixed = enabled and (any(not same_model(x, y) for x in names for y in names) or len(dims) > 1)
        all_there = enabled and not [e for e in self.ordered if e.chunks and e.rows is None]
        if mixed and all_there:
            # Two models (the API answered as another one for a while): all made again by the model that answers now; if they are
            # still of two models, again after retrySeconds counted from now (never sooner than a second), by the thread itself.
            now = time.monotonic()
            if not self.remade or (self.retry_at and now >= self.retry_at):
                self.remade = True
                self.retry_at = 0.0
                self.remake_all()
                with_rows, names, dims, mixed = [], set(), set(), False
            elif not self.retry_at:
                self.retry_at = now + background_retry_seconds()
        elif not mixed and all_there:
            self.remade = False
            self.retry_at = 0.0
        self.mixed = mixed
        pending = [e for e in self.ordered if e.chunks and e.rows is None] if enabled else []
        self.pending = sum(e.count - e.done for e in pending)
        self.parts_total = sum(e.count for e in self.ordered) if enabled else 0
        self.embedding_ok = enabled and bool(self.chunks) and not pending and not mixed
        self.dimension = next(iter(dims)) if len(dims) == 1 else None
        self.answered = fullest(names) if self.embedding_ok else None
        if not self.embedding_ok:
            self.matrix = None
            self.owner = None
        elif self.matrix_dirty or self.matrix is None:
            parts = [e.rows for e in self.ordered if e.chunks]
            self.matrix = np.vstack(parts) if np is not None else [r for p in parts for r in p]
            owner = [e.offset + j for e in self.ordered if e.chunks for j in e.owner]
            self.owner = np.asarray(owner, dtype=np.int64) if np is not None else owner
            self.matrix_dirty = False
        warnings = (list(self.scan_warnings) + [w for e in self.ordered for w in e.warnings] + list(self.unread.values())
                    + [e.embed_warning for e in pending if e.embed_warning])
        if len(dims) > 1:
            warnings.append(f"the embeddings have different dimensions {sorted(dims)}; the search by meaning is off")
        elif mixed:  # two models of the same dimension ("a/e5", "b/e5") do not go together either
            warnings.append(f"the embeddings were made by different models {sorted(names)}; the search by meaning is off")
        if warnings != self.warnings:
            for w in warnings:
                log("markporto-rag: " + w)
        self.warnings = warnings
        if pending and (self.worker is None or not self.worker.is_alive()):
            self.worker = threading.Thread(target=self.work, name="markporto-rag-embed", daemon=True)
            self.worker.start()
        COND.notify_all()

    def work(self):
        """The embedding thread: a pack of small files, or one large file batch by batch, without holding COND while the API works."""
        with COND:
            while True:
                now = time.monotonic()
                pack = self.next_pack() if EMB.get("enabled", True) else []
                if not pack and self.mixed and self.retry_at:  # vectors of two models: made again at retry_at, without a tool call
                    if now < self.retry_at:
                        COND.wait(timeout=self.retry_at - now)
                        continue
                    self.evaluate()  # made again now, if every file has its vectors
                    if self.next_pack():
                        continue
                    # Not yet: some files wait for their own retry (another model answered them). Wait for the first of them, or a tool.
                    waiting = [e.retry_at for e in self.ordered if e.chunks and e.rows is None and e.retry_at > now]
                    COND.wait(timeout=(min(waiting) - now) if waiting else None)
                    continue
                if not pack:
                    waiting = [e.retry_at for e in self.ordered if e.chunks and e.rows is None and e.retry_at > now]
                    COND.wait(timeout=(min(waiting) - now) if waiting else None)  # a file of another model is asked again then
                    continue
                if now < self.api_down_until:
                    COND.wait(timeout=self.api_down_until - now)
                    continue
                try:
                    self.embed_pack(pack)
                except Exception as ex:  # a fault of this thread: not to stop the embeddings for good, nor a search waiting for them
                    log(f"markporto-rag: the embedding thread failed ({ex!r})")
                    self.api_down_until = time.monotonic() + background_retry_seconds()
                self.evaluate()

    def embed_pack(self, pack: list[Entry]) -> None:
        """Embeds the files of a pack (called with COND held; gives it up while the API works). A file read again or gone meanwhile is
        dropped between batches, so the old text of a large file is not sent on."""
        size = batch_size()
        texts = [(e, t) for e in pack for t in e.texts()]
        params = EMB["passageParams"]
        got: list[list[float]] = []
        models: set[str] = set()
        for start in range(0, len(texts), size):
            if any(self.entries.get(e.path) is not e for e in pack):
                return  # read again or gone: the next pack takes what is there now
            batch = [t for _, t in texts[start:start + size]]
            COND.release()
            try:
                part, by = embed(batch, params)
            except OtherModel as ex:  # vectors of another model would be kept as if they were of this one
                COND.acquire()
                for e in pack:  # these files wait (the API answers: the others and the questions go on)
                    e.embed_warning = str(ex) + "; the search by meaning is off"
                    e.done = 0  # what was made is dropped: all of it is asked for again
                    e.retry_at = time.monotonic() + background_retry_seconds()
                return
            except Exception as ex:  # no embeddings: the keyword search alone, and the API is tried again later
                COND.acquire()
                log(f"markporto-rag: no embeddings ({ex})")
                for e in pack:
                    e.done = 0
                self.api_down_until = time.monotonic() + background_retry_seconds()
                return
            COND.acquire()
            got += part
            if by:
                models.add(by)
            if len(pack) == 1 and self.entries.get(pack[0].path) is pack[0]:
                pack[0].done = len(got)  # the progress of a large file, seen by a search waiting for it
                self.count_pending()
                COND.notify_all()
        # Checked, normalized and made into rows without COND (a large file takes a moment): a search waiting for it answers meanwhile.
        sizes = [e.count for e in pack]
        COND.release()
        try:
            whole = not any(not same_model(x, y) for x in models for y in models) and valid_vectors(got, len(texts))
            prepared = []
            if whole:
                start = 0
                for n in sizes:
                    norm = [normalized(v) for v in got[start:start + n]]
                    start += n
                    prepared.append(as_rows(norm) if all(v is not None for v in norm) else None)
            del got, texts
        finally:
            COND.acquire()
        if not whole:
            for e in pack:
                e.embed_warning = f"the embedding API answered with the models {sorted(models)} or vectors of different dimensions; the search by meaning is off"
                e.done = 0
                e.retry_at = time.monotonic() + background_retry_seconds()
            return
        model = fullest(models)
        to_keep = []
        for e, rows in zip(pack, prepared):
            e.done = 0
            if self.entries.get(e.path) is not e:  # read again (changed) while the API worked: embedded again
                continue
            if rows is None:
                e.embed_warning = "the embedding API gave a vector that is not of numbers; the search by meaning is off"
                e.retry_at = time.monotonic() + background_retry_seconds()
                continue
            e.rows = rows
            e.model = model
            e.embed_warning = None
            self.matrix_dirty = True
            to_keep.append((e.cache_key, e.digest, model, e.rows))
        if to_keep:  # written without COND: a search waiting for the embeddings answers meanwhile
            self.evaluate()
            self.writing += 1
            COND.release()
            try:
                for key, digest, m, rows in to_keep:
                    try:
                        write_cache(key, digest, m, rows)
                    except OSError as ex:
                        KEEP_ERRORS.append(f"{key.splitlines()[0]}: {ex}")
                        log(f"markporto-rag: the embeddings could not be kept ({ex})")
            finally:
                COND.acquire()
                self.writing -= 1
                COND.notify_all()

    def count_pending(self) -> None:
        enabled = EMB.get("enabled", True)
        self.pending = sum(e.count - e.done for e in self.ordered if e.chunks and e.rows is None) if enabled else 0

    def next_pack(self) -> list[Entry]:
        """The next files to embed: small files are packed into one request of up to batchSize chunks (the API takes about as long for
        one chunk as for many: LM Studio about 2 s a request); a large file goes alone, in batches."""
        size = batch_size()
        pack: list[Entry] = []
        total = 0
        now = time.monotonic()
        for e in self.ordered:
            if not e.chunks or e.rows is not None or now < e.retry_at or (self.once and e.embed_warning):
                continue
            if pack and total + e.count > size:
                break
            pack.append(e)
            total += e.count
            if total >= size:
                break
        return pack

    def wait_for_embeddings(self) -> None:
        """A search waits a little (waitSeconds) for the chunks being embedded, so that a few new ones are searched by meaning too; with
        many, it answers by keywords and says how far the embedding is. The other tools wait for the search (TOOLS)."""
        deadline = time.monotonic() + wait_seconds()
        while True:
            now = time.monotonic()
            if not (self.pending and now >= self.api_down_until and EMB.get("enabled", True)) or now >= deadline:
                return
            COND.wait(timeout=deadline - now)


COND = threading.Condition()  # the index: the tools and the embedding thread
TOOLS = threading.Lock()  # the tools, one at a time (a search that waits for the embeddings gives up COND, not TOOLS)


def batch_size() -> int:
    try:
        return min(max(int(EMB.get("batchSize", 64)), 1), 256)
    except (TypeError, ValueError, OverflowError):
        return 64


def wait_seconds() -> float:
    try:
        value = float(EMB.get("waitSeconds", 10))
    except (TypeError, ValueError, OverflowError):
        return 10.0
    return min(max(value, 0.0), 600.0) if math.isfinite(value) else 10.0


def background_retry_seconds() -> float:
    """The embedding thread tries the API again after retrySeconds, but never sooner than a second (retrySeconds 0 would ask it without
    end while it is down)."""
    return max(retry_seconds(), 1.0)


def retry_seconds() -> float:
    try:
        value = float(EMB.get("retrySeconds", 30))
    except (TypeError, ValueError, OverflowError):
        return 30.0
    return min(max(value, 0.0), 86400.0) if math.isfinite(value) else 30.0  # at most a day


def whole_setting(name: str, default: int, low: int, high: int) -> int:
    """A whole number of the configuration, kept between low and high; the default when it is not one."""
    value = CONFIG.get(name, default)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return default
    return int(min(max(value, low), high))


def whole_setting_of(section: dict, name: str, default: int, low: int, high: int) -> int:
    value = section.get(name, default)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return default
    return int(min(max(value, low), high))


def recheck_seconds() -> float:
    return float(whole_setting("recheckSeconds", 300, 0, 86400))


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


def inside_folder(reference: str, plain_name: bool = False) -> bool:
    """A file of the folder of a document, as a chunk names it: a relative path without ".." (a plain name, for the Markdown)."""
    if not reference or reference.strip() != reference:
        return False
    p = reference.replace("\\", "/")
    if p.startswith("/") or re.match(r"^[A-Za-z]:", p) or any(part in ("", ".", "..") for part in p.split("/")):
        return False
    return "/" not in p if plain_name else True


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
        "markdown": str(Path(c["folder"]) / c["markdown"]) if c.get("_markdown_ok", True) else None,
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


def rerank(query: str, texts: list[str]) -> list[float]:
    """The scores that the rerank API gives the texts for the question (the higher, the better), in the order of the texts. The answers of
    Voyage AI ("data") and of Cohere and Jina ("results") are read; each has the index of a text and its relevance_score."""
    headers = {"Content-Type": "application/json"}
    if RERANK.get("apiKeyEnv") and os.environ.get(RERANK["apiKeyEnv"]):
        headers["Authorization"] = "Bearer " + os.environ[RERANK["apiKeyEnv"]]
    req = urllib.request.Request(
        RERANK["url"].rstrip("/") + "/rerank",
        data=json.dumps({"model": RERANK["model"], "query": query, "documents": texts}).encode("utf-8"),
        headers=headers)
    timeout = RERANK.get("timeoutSeconds", 30)
    if isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not (0 < timeout < 1e6):
        timeout = 30
    with urllib.request.urlopen(req, timeout=timeout) as r:
        answer = json.load(r)
    items = answer.get("data") if isinstance(answer.get("data"), list) else answer.get("results")
    if not isinstance(items, list):
        raise ValueError("the rerank API gave no list of results")
    scores: list[float | None] = [None] * len(texts)
    for item in items:
        i = item.get("index") if isinstance(item, dict) else None
        s = item.get("relevance_score") if isinstance(item, dict) else None
        if (isinstance(i, int) and not isinstance(i, bool) and 0 <= i < len(texts) and scores[i] is None
                and isinstance(s, (int, float)) and not isinstance(s, bool) and math.isfinite(s)):
            scores[i] = float(s)
    if any(s is None for s in scores):
        raise ValueError("the rerank API did not give a score to every text")
    return [float(s) for s in scores]


def no_roots() -> dict | None:
    if CONFIG_ERROR:
        return {"error": CONFIG_ERROR}
    if (wrong := check_embedding_config() or check_rerank_config()) is not None:
        return {"error": f"The configuration is not right: {wrong}."}
    if ROOTS:
        return None
    return {"error": "No folder to search. Write \"roots\" in the configuration (markporto-rag.json) or set MARKPORTO_RAG_ROOT."}


# ---- tools ------------------------------------------------------------------

def semantic_ranks(qn: list[float], idx: list[int]) -> dict[int, int]:
    """The rank of the chunks of idx nearest to the question by meaning (the best cosine of their parts with the question's), the first
    meaningCandidates of them: a chunk far from the question is not given a place by meaning."""
    keep = whole_setting("meaningCandidates", 50, 1, 100000)
    if np is not None:
        rows = np.round((INDEX.matrix @ np.asarray(qn, dtype=np.float32)).astype(np.float64), 6)  # ties alike with and without numpy
        best = np.full(len(INDEX.chunks), -np.inf)
        np.maximum.at(best, INDEX.owner, rows)
        chosen = np.asarray(idx, dtype=np.int64)
        order = chosen[np.lexsort((-chosen, -best[chosen]))][:keep]  # by score, then the later chunk first (as without numpy)
        return {int(i): r for r, i in enumerate(order)}
    q = array("f", qn)
    best: dict[int, float] = {}
    for row, i in zip(INDEX.matrix, INDEX.owner):
        s = round(sum(map(operator.mul, row, q)), 6)
        if s > best.get(i, -math.inf):
            best[i] = s
    sem = sorted(((best.get(i, -math.inf), i) for i in idx), reverse=True)[:keep]
    return {i: r for r, (_, i) in enumerate(sem)}


@mcp.tool()
@one_at_a_time
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
    INDEX.wait_for_embeddings()
    folder_only = bool(path) and path.strip().replace("\\", "/").endswith("/")  # "v1.2/": that folder, never a part of a name
    needle = path.replace("\\", "/").strip().strip("/").lower() if path and path.strip() else None
    partial = None
    wanted = source.lower() if source else None
    idx = [i for i, c in enumerate(INDEX.chunks) if not wanted or wanted in c["_source"]]
    if needle:
        # Whole folders and names first, so that "v1.2" is not also "v1.20"; a part of a name ("設計") when nothing is whole.
        # A folder is followed by "/"; without "/" at the end, a whole name of a file is matched too.
        whole = re.compile(r"(?:^|/)" + re.escape(needle) + (r"/" if folder_only else r"(?:/|$)"))
        exact = [i for i in idx if whole.search(INDEX.chunks[i]["_where"])]
        if exact or folder_only:
            idx = exact
        else:
            idx = [i for i in idx if needle in INDEX.chunks[i]["_where"]]
            if idx:
                names = sorted({where(INDEX.chunks[i]) for i in idx})
                partial = (f"No folder or name is \"{path.strip()}\" as a whole; these were found by a part of their names: "
                           + ", ".join(names[:5]) + (" ..." if len(names) > 5 else "") + f". Write \"{needle}/\" for that folder only.")
    if not idx:
        return {"mode": "none", "results": [], "note": "No chunks (check the folders, or the values of source and path)."}

    words = query.lower().split()
    q = {t for w in words for t in tokens(w)}  # no pairs across the space between two words
    singles = [w for w in words if not tokens(w)]  # "図" of "図 表": too short for a pair
    n = len(INDEX.chunks)
    allowed = set(idx) if len(idx) < n else None
    scores: dict[int, float] = {}
    for t in q:  # only the chunks that have a word of the question (the inverted index of each file)
        df = INDEX.df.get(t, 0)
        if not df:
            continue
        idf = math.log(1 + (n - df + 0.5) / (df + 0.5))
        for e in INDEX.ordered:
            for j, tf in e.postings.get(t, ()):
                i = e.offset + j
                if allowed is None or i in allowed:
                    scores[i] = scores.get(i, 0.0) + idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * e.lengths[j] / INDEX.avg))
    if singles:  # looked for as they are, each on its own
        for i in idx:
            text = (" ".join(INDEX.chunks[i]["headings"]) + "\n" + INDEX.chunks[i]["text"]).lower()
            hits = sum(1 for w in singles if w in text)
            if hits:
                scores[i] = scores.get(i, 0.0) + float(hits)
    # a chunk with none of the words of the question is not found by the keyword search
    bm25 = sorted(((v, i) for i, v in scores.items() if v > 0), reverse=True)
    ranks = [{i: r for r, (_, i) in enumerate(bm25)}]

    mode = "keyword"
    note = None
    if INDEX.pending and EMB.get("enabled", True):
        done = INDEX.parts_total - INDEX.pending
        note = (f"The documents are being embedded ({done} of {INDEX.parts_total} parts of chunks); keywords only until it is done."
                if time.monotonic() >= INDEX.api_down_until else "The embedding API could not be reached a moment ago; keywords only until it is tried again.")
    elif INDEX.embedding_ok and time.monotonic() < INDEX.api_down_until:
        note = "The embedding API could not be reached a moment ago; keywords only until it is tried again."
    elif INDEX.embedding_ok:
        try:
            vectors, by = embed([EMB["queryPrefix"] + query], EMB["queryParams"])
            qv = vectors[0]
            qn = normalized(qv)
            answered, dimension = INDEX.answered, INDEX.dimension
            if (answered and not same_model(answered, by)) or len(qv) != dimension:
                # The API answers now as the model of the configuration (else OtherModel), the kept vectors are of another one (the model was
                # changed under the same name): they are made again, by the embedding thread - once; if the question still differs from
                # the documents made again, not before retrySeconds (else every search would send all of them again).
                now = time.monotonic()
                again = INDEX.question_remade_at is None or now >= INDEX.question_remade_at + background_retry_seconds()
                if again:
                    INDEX.question_remade_at = now
                    INDEX.remake_all()
                    INDEX.evaluate()
                note = (f"The question was embedded by {by or EMB['model']} ({len(qv)} dimensions), the documents by "
                        f"{answered or 'another model'} ({dimension}): keywords only; "
                        + ("the documents are embedded again." if again else "the documents were embedded again a moment ago and still differ."))
                log("markporto-rag: " + note)
            elif qn is not None and any(qn):  # a question of length 0 tells nothing by meaning
                ranks.append(semantic_ranks(qn, idx))
                mode = "hybrid"
                INDEX.question_remade_at = None
        except OtherModel as ex:
            note = f"{ex}. Keywords only."
            log("markporto-rag: " + note)
        except Exception as ex:  # not waited for again at each question: the documents' vectors are kept, the API is tried after retrySeconds
            INDEX.api_down_until = time.monotonic() + retry_seconds()
            note = "The embedding API could not be reached; keywords only."
            log(f"markporto-rag: query embedding failed ({ex})")

    top = max(1, min(k, CONFIG["maxResults"]))
    found = [i for i in idx if any(i in rk for rk in ranks)]
    if not found:
        none = "No chunk has the words of the question. Try other words; if it is still not found, it is not in the documents."
        return {"mode": mode, "results": [], "note": f"{note} {none}" if note else none}
    fused = sorted(((sum(1 / (60 + rk[i]) for rk in ranks if i in rk), i) for i in found), reverse=True)
    reranked: dict[int, float] = {}  # the score the rerank API gave each chunk it put in order
    if RERANK.get("enabled", False) and len(fused) > 1:
        head = fused[:whole_setting_of(RERANK, "candidates", 30, 2, 200)]
        try:
            given = rerank(query, [" > ".join(INDEX.chunks[i]["headings"]) + "\n" + INDEX.chunks[i]["text"] for _, i in head])
            reranked = {i: s for s, (_, i) in zip(given, head)}
            fused = sorted(head, key=lambda x: -reranked[x[1]]) + fused[len(head):]
        except Exception as ex:  # the order of the search is kept
            note = ((note + " ") if note else "") + "The rerank API could not be used; the results are in the order of the search."
            log(f"markporto-rag: rerank failed ({ex})")
    fused = fused[:top]

    def matched(i: int) -> list[str]:
        return (["keywords"] if i in ranks[0] else []) + (["meaning"] if len(ranks) > 1 and i in ranks[1] else [])

    results = []
    for s, i in fused:
        v = view(INDEX.chunks[i], s)
        v["matchedBy"] = matched(i)
        if i in reranked:
            v["rerankScore"] = round(reranked[i], 4)
        results.append(v)
    result = {"mode": mode, "results": results}
    if reranked:
        result["reranked"] = True
    if mode == "hybrid" and not ranks[0]:  # R1-05: no chunk searched has a word of the question (not only those shown)
        note = ((note + " ") if note else "") + ("No chunk has the words of the question: these were found by meaning only, and may not "
                                                 "answer it. Say so if they do not; do not answer from them as if they did.")
    notes = [n for n in (partial, note) if n]
    if notes:
        result["note"] = " ".join(notes)
    return result


@mcp.tool()
@one_at_a_time
def read_chunk(key: str, neighbors: int = 1) -> dict:
    """Read a chunk by the key that search_docs gave (or its prev / next), with up to 3 chunks before and after it. The text of the chunk
    is whole; the chunks around it, nearest first, up to readChars of text in all (one cut short says "truncated")."""
    if (error := no_roots()) is not None:
        return error
    INDEX.refresh()
    c = INDEX.by_key.get(key)
    if c is None:
        return {"error": f"No chunk with the key {key}"}
    steps = max(0, min(neighbors, 3))

    def walk(link: str) -> list[dict]:
        out, cur = [], c
        for _ in range(steps):
            cur = INDEX.by_key.get(f"{cur['base']}#{cur[link]}") if cur.get(link) else None
            if not cur:
                break
            out.append(cur)
        return out

    left = whole_setting("readChars", 20000, 1000, 1000000)  # for the chunks around it (the chunk itself is whole)
    sides = ((walk("prev"), []), (walk("next"), []))
    for near in range(steps):  # one before and one after, nearest first, while there is room
        for side, out in sides:
            if near >= len(side) or (out and out[-1].get("truncated")) or left <= 0:
                continue
            v = view(side[near], full=True)
            if len(v["text"]) > left:
                v["text"] = v["text"][:left - 1] + "…"
                v["truncated"] = True
            left -= len(v["text"])
            out.append(v)
    return {"chunk": view(c, full=True), "before": list(reversed(sides[0][1])), "after": sides[1][1]}


@mcp.tool()
@one_at_a_time
def list_documents(path: str | None = None, offset: int = 0, limit: int = 200) -> dict:
    """The documents that are searched (the original file, its path in the folder that was converted, the number of chunks, when the file
    was changed, its Markdown), and the embedding
    in use: the model of the configuration and the dimension of the vectors kept, which tells which model really made them.
    "path" lists only the documents whose path contains it; "offset" and "limit" list a part of them ("total" is all, "next" the offset
    of the next part)."""
    if (error := no_roots()) is not None:
        return error
    INDEX.refresh()
    docs: dict[str, dict] = {}
    for c in INDEX.chunks:
        d = docs.setdefault(c["file"], {"source": c["source"], "path": where(c),
                                        "markdown": str(Path(c["folder"]) / c["markdown"]) if c.get("_markdown_ok", True) else None,
                                        "sourceModified": c.get("sourceModified"), "chunks": 0})
        d["chunks"] += 1
    listed = list(docs.values())
    if path and path.strip():
        needle = path.replace("\\", "/").strip().lower()
        listed = [d for d in listed if needle in d["path"].lower()]
    total = len(listed)
    start = max(0, offset if isinstance(offset, int) else 0)
    size = max(1, min(limit if isinstance(limit, int) else 200, 1000))
    page = listed[start:start + size]
    embedding = {
        "available": INDEX.embedding_ok and time.monotonic() >= INDEX.api_down_until,  # the search by meaning is used now
        "pendingChunks": INDEX.pending if EMB.get("enabled", True) else 0,  # chunks still being embedded (in the background)
        "model": EMB["model"] if EMB.get("enabled", True) else None,
        "url": EMB["url"] if EMB.get("enabled", True) else None,
        "dimension": INDEX.dimension,  # of the kept vectors: it tells which model made them (e5-small 384, e5-base 768 ...)
    }
    result = {"roots": [str(r) for r in ROOTS], "embedding": embedding, "warnings": INDEX.warnings, "total": total, "documents": page}
    if start + size < total:
        result["next"] = start + size
    return result


def finish_writing(limit: float = 10.0) -> bool:
    """At the end of the server, the kept files being written are finished (else a vector made by the API would be made again).
    False: some were still being written when the time was up."""
    deadline = time.monotonic() + limit
    with COND:
        while INDEX.writing and time.monotonic() < deadline:
            COND.wait(timeout=deadline - time.monotonic())
        return not INDEX.writing


def remove_old_temporary_files(age: float = 3600.0) -> None:
    """Temporary files of kept vectors left by a server that ended while writing (not those of a server writing now)."""
    with contextlib.suppress(OSError):
        for f in CACHE.glob(TMP_PREFIX + "*.tmp"):
            with contextlib.suppress(OSError):
                if time.time() - f.stat().st_mtime > age:
                    f.unlink()


def build_cache(out=sys.stdout) -> int:
    """--build-cache: embeds every file that has no kept vectors yet and keeps them, as the server would, then ends (no MCP). The
    server started after it searches by meaning at once. The work is done by the embedding thread of the index, so the vectors and the
    kept files are the same as the server's; this only waits for it and tells how far it is. 0: every file has its vectors; 1: the
    embedding is off, the API could not be reached, or it answered as another model."""
    def say(text: str) -> None:
        print(text, file=out, flush=True)

    global LOGGED
    LOGGED = set()
    if CONFIG_ERROR:  # the defaults would be used instead: not what was meant to be built
        say("markporto-rag: " + CONFIG_ERROR)
        return 1
    if not EMB.get("enabled", True):
        say("markporto-rag: the embedding is off in the configuration (embedding.enabled is false); nothing to build.")
        return 1
    began = time.monotonic()
    code = 0
    with COND:
        INDEX.once = True
        INDEX.refresh()  # reads the files and the kept vectors; starts the embedding thread for the others
        files = [e for e in INDEX.ordered if e.chunks]
        to_make = {e.path for e in files if e.rows is None}
        say(f"markporto-rag: {len(files)} file(s) of chunks in {len(ROOTS)} folder(s): {len(to_make)} file(s) to embed, "
            f"{len(files) - len(to_make)} from the cache. API: {EMB['url']} ({EMB['model']}); kept in {CACHE}")
        for w in INDEX.warnings:
            say("  warning: " + w)
        # Folders that are not there and files that could not be read: the others are embedded, but it is not a success.
        missing = (list(INDEX.scan_warnings) + list(INDEX.unread.values())
                   + [w for e in INDEX.ordered if not e.chunks for w in e.warnings])  # no chunk read from it (review R2-04)
        shown = None
        while True:
            now = time.monotonic()
            waiting = [e for e in INDEX.ordered if e.chunks and e.rows is None]
            made = len(to_make) - len([e for e in waiting if e.path in to_make])
            if (made, INDEX.pending) != shown:
                shown = (made, INDEX.pending)
                if to_make:
                    say(f"  {made} of {len(to_make)} file(s) embedded ({INDEX.pending} part(s) of chunks left)")
            if not waiting and not INDEX.mixed:
                break
            if now < INDEX.api_down_until:
                say(f"markporto-rag: the embedding API could not be reached ({EMB['url']}, model {EMB['model']}); "
                    "is it started, and is the model loaded? The files embedded so far are kept.")
                code = 1
                break
            # A file that failed in this run (another model, vectors that are not numbers) counts as failed, though the embedding
            # thread would try it again: waiting for its retry time could go on for ever with a slow API (review R1-02).
            stopped = [e for e in waiting if e.embed_warning]
            if waiting and len(stopped) == len(waiting):
                by_reason: dict[str, list[str]] = {}
                for e in stopped:
                    by_reason.setdefault(e.embed_warning or "not embedded", []).append(e.path)
                for reason, paths in by_reason.items():
                    say(f"  {len(paths)} file(s): {reason}")
                say("markporto-rag: some files could not be embedded (see above); the files embedded so far are kept.")
                code = 1
                break
            if INDEX.mixed and INDEX.retry_at:
                say("markporto-rag: the embedding API answered with vectors of two models; see the warning above. Nothing more is built.")
                code = 1
                break
            COND.wait(timeout=1.0)
    if not finish_writing(limit=600.0):
        say("markporto-rag: the kept files were still being written after 10 minutes (a slow shared folder?); not all are kept.")
        code = 1
    if KEEP_ERRORS:
        for e in KEEP_ERRORS[:10]:
            say("  " + e)
        say(f"markporto-rag: the embeddings of {len(KEEP_ERRORS)} file(s) could not be kept in {CACHE}; they are embedded again next time.")
        code = 1
    if missing:
        for w in missing:
            say("  " + w)
        say("markporto-rag: some folders or files could not be read (see above); the others are embedded.")
        code = 1
    say(f"markporto-rag: {'done' if code == 0 else 'stopped'} in {time.monotonic() - began:.1f} s.")
    return code


USAGE = """usage: server.py [--build-cache]
  (no argument)   run the MCP server on stdin and stdout (as Claude Code and other tools start it)
  --build-cache   embed the chunks that have no kept vectors yet, keep them, and end (exit 0: done, 1: not all embedded)"""


if __name__ == "__main__":
    arguments = sys.argv[1:]
    if arguments == ["--build-cache"]:
        atexit.register(finish_writing)
        remove_old_temporary_files()
        sys.exit(build_cache())
    if arguments:
        print(USAGE, file=sys.stderr)
        sys.exit(2)
    atexit.register(finish_writing)
    remove_old_temporary_files()
    mcp.run()
