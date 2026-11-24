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

"""Checks of the search server, through the MCP client as a tool uses it. Each case has its own folders and configuration (never the
user's), and a small embedding API of its own (never LM Studio). Not part of the ZIP.

Run: uv run --no-project --with mcp python tools/markporto-rag/tests/test_server.py
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from array import array
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

SERVER = Path(__file__).resolve().parent.parent / "server.py"
BASE = Path(tempfile.mkdtemp(prefix="markporto-rag-tests-"))
failures: list[str] = []
count = 0


def check(name: str, ok: bool, detail="") -> None:
    global count
    count += 1
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + ("" if ok else f"   <- {detail}"))
    if not ok:
        failures.append(name)


def chunk(id_: str, text: str, prev=None, next_=None, **extra) -> dict:
    c = {"schemaVersion": 1, "id": id_, "prev": prev, "next": next_, "source": "設計書.docx", "sourceModified": "2026-11-15T07:38:12Z",
         "markdown": "設計書.md", "headings": ["設計書"], "kind": "text", "pages": [1], "pageImages": ["images/page-001.png"], "text": text}
    c.update(extra)
    return c


def write(folder: Path, rows, raw=()) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    lines = [json.dumps(r, ensure_ascii=False) for r in rows] + list(raw)
    (folder / "設計書_chunks.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")


MAGIC = b"MPRAG1\n"


def kept_files(folder) -> list[Path]:
    """The files of kept vectors: binary (since 0.4.4) or JSON (0.4.1-0.4.3)."""
    return sorted(list(Path(folder).glob("*.bin")) + list(Path(folder).glob("*.json")))


def read_kept(f: Path) -> dict:
    if f.suffix == ".json":
        return json.loads(f.read_text(encoding="utf-8"))
    raw = f.read_bytes()
    assert raw.startswith(MAGIC)
    end = raw.index(b"\n", len(MAGIC))
    head = json.loads(raw[len(MAGIC):end])
    flat = array("f")
    flat.frombytes(raw[end + 1:])
    head["vectors"] = [list(flat[k * head["dim"]:(k + 1) * head["dim"]]) for k in range(head["count"])]
    return head


def write_kept_binary(f: Path, head: dict, vectors) -> None:
    flat = array("f", [x for v in vectors for x in v])
    head = {k: v for k, v in head.items() if k != "vectors"}
    f.write_bytes(MAGIC + json.dumps(head).encode() + b"\n" + flat.tobytes())


def to_old_json(folder) -> None:
    """The kept vectors as 0.4.3 kept them (a JSON file of plain lists), to check that they are taken over, or made again when broken."""
    for f in Path(folder).glob("*.bin"):
        head = read_kept(f)
        f.with_suffix(".json").write_text(json.dumps({"digest": head["digest"], "model": head["model"], "vectors": head["vectors"]}), encoding="utf-8")
        f.unlink()


class Embeddings:
    """An OpenAI compatible embedding API: a vector from the characters of the text; it can be down, or answer as another model."""

    def __init__(self):
        self.up = True
        self.model: str | None = None  # the name it answers with; None: the name asked for
        self.inputs = 0  # the texts it was asked for
        self.requests = 0  # the requests it answered or refused
        self.pick = None  # the model it answers with for a text, when it is not always the same
        self.same_index = False  # every vector with index 0, as a broken API might
        self.bool_index = False  # false / true for the indexes 0 and 1
        self.dims = None  # the length of the vector for a text, when it is not 8
        self.hook = None  # called with the texts of each request, before it is answered
        self.zero = None  # texts whose vector is all 0
        self.flat = False  # the same vector for every text (ties in the search by meaning)
        self.seen: list[str] = []  # the first text of each request
        self.fail = None  # texts whose request is refused (503), the API being up
        self.alias = None  # the text whose vector a text gets (the same vector for several chunks)
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                outer.inputs += len(body["input"])
                outer.requests += 1
                outer.seen.append(body["input"][0])
                if outer.hook:
                    outer.hook(body["input"])
                if not outer.up or (outer.fail and any(outer.fail(t) for t in body["input"])):
                    self.send_response(503)
                    self.end_headers()
                    return
                def vector(t):
                    n = outer.dims(t) if outer.dims else 8
                    if outer.zero and outer.zero(t):
                        return [0.0] * n
                    if outer.flat:
                        return [1.0] + [0.0] * (n - 1)
                    if outer.alias:
                        t = outer.alias(t)
                    raw = hashlib.sha256(t.encode()).digest()[:n] if n <= 32 else hashlib.shake_256(t.encode()).digest(n)
                    return [b / 255 for b in raw]

                def index(i):
                    return 0 if outer.same_index else (bool(i) if outer.bool_index else i)

                data = [{"index": index(i), "embedding": vector(t)} for i, t in enumerate(body["input"])]
                model = (outer.pick(body["input"][0]) if outer.pick else None) or outer.model or body["model"]
                answer = json.dumps({"object": "list", "model": model, "data": data}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(answer)))
                self.end_headers()
                self.wfile.write(answer)

            def log_message(self, *args):
                pass

        class Quiet(ThreadingHTTPServer):
            def handle_error(self, request, client_address):  # a server process that ended dropped its connection: not a failure
                if not isinstance(sys.exc_info()[1], ConnectionError):
                    super().handle_error(request, client_address)

        self.http = Quiet(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.http.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.http.server_address[1]}/v1"


async def run(case: str, config: dict, body) -> None:
    folder = BASE / case
    folder.mkdir(parents=True, exist_ok=True)
    config.setdefault("cacheDir", str(folder / "cache"))
    path = folder / "config.json"
    path.write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")
    env = {k: v for k, v in os.environ.items() if k not in ("MARKPORTO_RAG_ROOT",)}
    env["MARKPORTO_RAG_CONFIG"] = str(path)
    params = StdioServerParameters(command=sys.executable, args=[str(SERVER)], env=env)
    async with stdio_client(params, errlog=open(os.devnull, "w")) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()

            async def call(name, args):
                res = await s.call_tool(name, args)
                text = res.content[0].text if res.content else ""
                if getattr(res, "is_error", False) or getattr(res, "isError", False):
                    return {"tool_error": text}
                return json.loads(text)

            print(case)
            await body(call)


async def same_place_in_two_folders(call):
    res = await call("search_docs", {"query": "タイムアウト", "k": 5})
    keys = [x["key"] for x in res.get("results", [])]
    check("the same place in two folders gives two keys", len(keys) == 2 and len(set(keys)) == 2, keys)
    texts = set()
    for key in keys:
        out = await call("read_chunk", {"key": key})
        texts.add(out.get("chunk", {}).get("text", "")[:2])
    check("read_chunk gives the chunk of each key", texts == {"A：", "B："}, texts)
    paths = {x["path"] for x in res.get("results", [])}
    check("the path is that of the original document", paths == {"v1/設計書.docx"}, paths)
    narrowed = await call("search_docs", {"query": "タイムアウト", "path": "v1"})
    check("path narrows by the path of the chunk, as before", len(narrowed.get("results", [])) == 2, narrowed)


async def one_folder_keeps_its_keys(call):
    res = await call("search_docs", {"query": "タイムアウト"})
    check("with one folder the key is as before", [x["key"] for x in res["results"]] == ["設計書/設計書_chunks.jsonl#c0001"], res)


async def old_files_in_two_folders(call):
    res = await call("search_docs", {"query": "タイムアウト", "k": 5})
    paths = [x["path"] for x in res.get("results", [])]
    keys = {x["key"] for x in res.get("results", [])}
    check("old chunks (no path) in two folders: the place in each folder, without its name", paths == ["設計書/設計書_chunks.jsonl"] * 2, paths)
    check("... with two keys", len(keys) == 2, keys)


async def one_character(call):
    res = await call("search_docs", {"query": "図"})
    check("a question of one character is looked for as it is", [x["text"] for x in res.get("results", [])] == ["図に timeout を記載"], res)
    res = await call("search_docs", {"query": "鳥"})
    check("... and not found when it is not there", res.get("results") == [], res)


async def single_characters(call):
    res = await call("search_docs", {"query": "図 表"})
    texts = [x["text"] for x in res.get("results", [])]
    check("'図 表': each word of one character is looked for", sorted(texts) == sorted(["図だけの段落", "表だけの段落", "図の説明と表の一覧"]), res)
    check("... and the chunk with both comes first", texts[:1] == ["図の説明と表の一覧"], texts)


async def no_word_of_the_question(call):
    res = await call("search_docs", {"query": "量子コンピューター"})
    check("keywords only: no chunk without a word of the question", res.get("results") == [], res)
    check("... and the note says it is not found", "not in the documents" in (res.get("note") or ""), res)
    res = await call("search_docs", {"query": "タイムアウト 量子"})
    check("keywords only: the chunks with a word are found, no other", [x["text"][:2] for x in res["results"]] == ["A："], res)


async def broken_lines(call):
    res = await call("search_docs", {"query": "ふくろう"})
    check("a search is not stopped by broken lines", "tool_error" not in res, res)
    check("... the good chunk is found", [x["text"] for x in res.get("results", [])] == ["正しい塊 ふくろう"], res)
    docs = await call("list_documents", {})
    warned = [w for w in docs["warnings"] if "were skipped" in w]
    check("... the broken lines are counted in a warning", len(warned) == 1 and " 9 line(s)" in warned[0], docs["warnings"])
    res = await call("search_docs", {"query": "重複"})
    check("an id written twice in a file is kept once", len(res.get("results", [])) == 1, res)


async def hash_in_a_folder(call):
    out = await call("read_chunk", {"key": "C#設計/設計書/設計書_chunks.jsonl#c0001", "neighbors": 2})
    check("'#' in a folder: the next key is whole", out.get("chunk", {}).get("next") == "C#設計/設計書/設計書_chunks.jsonl#c0002", out)
    check("... and the chunk after is read", [x["text"] for x in out.get("after", [])] == ["後ろの塊"], out)
    res = await call("search_docs", {"query": "後ろ", "path": "C#設計"})
    check("... and path narrows to it", len(res.get("results", [])) == 1, res)


def embedding_cases(api: Embeddings):
    async def comes_back(call):
        api.up = False
        res = await call("search_docs", {"query": "タイムアウト"})
        check("API down: keywords only", res.get("mode") == "keyword", res)
        api.up = True
        res = await call("search_docs", {"query": "タイムアウト"})
        check("API back: not before retrySeconds", res.get("mode") == "keyword", res)
        await asyncio.sleep(1.5)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("API back: tried again after retrySeconds, by meaning too", res.get("mode") == "hybrid", res)
        docs = await call("list_documents", {})
        check("... list_documents says the embeddings are there", docs["embedding"]["available"] is True, docs["embedding"])

    async def other_model(call):
        api.model = "another-embedding-model"  # the same dimension: only the name tells
        res = await call("search_docs", {"query": "タイムアウト"})
        check("another model answered: keywords only", res.get("mode") == "keyword", res)
        docs = await call("list_documents", {})
        check("... and a warning names it", any("another-embedding-model" in w for w in docs["warnings"]), docs["warnings"])
        cache = kept_files(BASE / "other-model" / "cache")
        check("... and its vectors are not kept", cache == [], cache)
        api.model = None

    async def other_model_for_the_question(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("the right model: by meaning", res.get("mode") == "hybrid", res)
        api.model = "publisher/another-embedding-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("another model for the question: keywords only, with a note", res.get("mode") == "keyword" and "another-embedding-model" in (res.get("note") or ""), res)
        api.model = "lab/test-model:latest"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("'publisher/name:latest' is the same model as 'name'", res.get("mode") == "hybrid", res)
        api.model = None

    async def publishers(call):
        api.model = "lab/test-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("the same publisher and name: by meaning", res.get("mode") == "hybrid", res)
        api.model = "other-lab/test-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("another publisher's model of the same name: keywords only", res.get("mode") == "keyword", res)
        api.model = None

    async def documents_and_question_of_two_publishers(call):
        # The configuration names no publisher: each answer agrees with it, but not with the other.
        api.pick = lambda text: "org-b/test-model" if text.startswith("q: ") else "org-a/test-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("documents by org-a/…, question by org-b/…: keywords only, with a note",
              res.get("mode") == "keyword" and "org-b/test-model" in (res.get("note") or ""), res)
        api.pick = lambda text: "org-a/test-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... the question by the same model as the documents: by meaning", res.get("mode") == "hybrid", res)
        api.pick = None

    def batches_of_two_names(first: str, second: str):
        # 17 chunks go in two requests; each request answers with one name of the same model, one of them without its publisher.
        async def body(call):
            api.pick = lambda text: "org-b/test-model" if text.startswith("q: ") else (second if "第17" in text else first)
            res = await call("search_docs", {"query": "タイムアウト"})
            check(f"documents answered as {first} and {second}, question by org-b/…: keywords only",
                  res.get("mode") == "keyword" and "org-b/test-model" in (res.get("note") or ""), res)
            api.pick = None
        return body

    async def two_models_kept_then_one(call):
        # Two documents embedded while the API answered as two models: the search by meaning is off, and comes back by itself.
        api.pick = lambda text: "b/test-model" if "文書B" in text and not text.startswith("q: ") else "a/test-model"
        res = await call("search_docs", {"query": "タイムアウト"})
        check("documents of two models: keywords only", res.get("mode") == "keyword", res)
        api.pick = lambda text: "a/test-model"
        await asyncio.sleep(1.5)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... the API answers as one model again: by meaning again, after retrySeconds", res.get("mode") == "hybrid", res)
        api.pick = None

    async def question_not_reached(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("by meaning while the API is there", res.get("mode") == "hybrid", res)
        api.up = False
        res = await call("search_docs", {"query": "タイムアウト"})
        check("API down for the question: keywords only, with a note", res.get("mode") == "keyword" and "could not be reached" in (res.get("note") or ""), res)
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... and not asked again before retrySeconds", api.inputs == before and res.get("mode") == "keyword", (api.inputs - before, res.get("mode")))
        api.up = True
        await asyncio.sleep(1.5)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... asked again after retrySeconds", res.get("mode") == "hybrid", res)

    async def same_index(call):
        api.same_index = True
        res = await call("search_docs", {"query": "タイムアウト"})
        check("an API that gives one index twice: keywords only", res.get("mode") == "keyword", res)
        cache = kept_files(BASE / "same-index" / "cache")
        check("... and nothing kept", cache == [], cache)
        api.same_index = False

    async def bool_index(call):
        api.bool_index = True
        res = await call("search_docs", {"query": "タイムアウト"})
        check("indexes false / true are not 0 / 1: keywords only", res.get("mode") == "keyword", res)
        cache = kept_files(BASE / "bool-index" / "cache")
        check("... and nothing kept", cache == [], cache)
        api.bool_index = False

    return (comes_back, other_model, other_model_for_the_question, publishers, documents_and_question_of_two_publishers,
            batches_of_two_names, two_models_kept_then_one, question_not_reached, same_index, bool_index)


async def three_folders_two_names(call):
    res = await call("search_docs", {"query": "タイムアウト", "k": 5})
    keys = [x["key"] for x in res.get("results", [])]
    check("folders 'out', 'out', 'out-2' give three keys", len(keys) == 3 and len(set(keys)) == 3, keys)
    texts = set()
    for key in keys:
        texts.add((await call("read_chunk", {"key": key})).get("chunk", {}).get("text", "")[:2])
    check("... and each key reads its own chunk", texts == {"A：", "B：", "C："}, texts)


async def no_pages(call):
    res = await call("search_docs", {"query": "ページのない塊"})
    check("a chunk without pages is found", len(res.get("results", [])) == 1 and res["results"][0]["pages"] is None, res)
    out = await call("read_chunk", {"key": "設計書/設計書_chunks.jsonl#c0001"})
    check("... and read", out.get("chunk", {}).get("pages", "x") is None, out)


async def whole_folders(call):
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.2"})
    check("'v1.2' is not 'v1.20'", [x["path"] for x in res.get("results", [])] == ["v1.2/設計書.docx"], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.2/"})
    check("'v1.2/' is not 'v1.20' either", [x["path"] for x in res.get("results", [])] == ["v1.2/設計書.docx"], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "設計書.docx/"})
    check("a name of a file with '/' after it is no folder: nothing", res.get("results") == [], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.2/設計書.docx"})
    check("... the file is found by its whole path, without '/'", [x["path"] for x in res.get("results", [])] == ["v1.2/設計書.docx"], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.20"})
    check("'v1.20' is its own", [x["path"] for x in res.get("results", [])] == ["v1.20/設計書.docx"], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "設計"})
    check("a part of a name still narrows when no folder is whole", len(res.get("results", [])) == 2, res)
    check("... and the note says it was a part of the names", "a part of their names" in (res.get("note") or ""), res)


async def only_a_longer_version(call):
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.2/"})
    check("'v1.2/' with only v1.20 there: nothing", res.get("results") == [], res)
    res = await call("search_docs", {"query": "タイムアウト", "path": "v1.2"})
    check("'v1.2' with only v1.20 there: found by a part, and the note names it",
          [x["path"] for x in res.get("results", [])] == ["v1.20/設計書.docx"] and "v1.20/設計書.docx" in (res.get("note") or ""), res)


UNIT = '\nimport importlib.util, json, os, sys, threading\nfrom pathlib import Path\nspec = importlib.util.spec_from_file_location("server", sys.argv[1])\nserver = importlib.util.module_from_spec(spec)\nspec.loader.exec_module(server)\nout = {}\ncache = server.CACHE\ncache.mkdir(parents=True, exist_ok=True)\n# R1-04: two writers of one file at once: what is left is whole, and no temporary file stays\nwritten, errors = {1: 0, 100: 0}, []\ndef writer(n):\n    try:\n        for _ in range(30):\n            server.write_cache("doc", "d", "m", server.as_rows([[1.0, 0.0]] * n))\n            written[n] += 1\n    except Exception as ex:\n        errors.append(repr(ex))\nthreads = [threading.Thread(target=writer, args=(n,)) for n in (1, 100)]\n[t.start() for t in threads]\n[t.join() for t in threads]\nout["concurrent"] = [server.read_cache("doc", "d", n) is not None for n in (1, 100)]\nout["written"] = [written[1], written[100]]\nout["errors"] = errors\nout["tmp_left"] = [f.name for f in cache.glob("*.tmp")]\n# R1-05: a broken binary file next to a good JSON of 0.4.3: the JSON is used\nserver.cache_file("old", ".bin").write_bytes(server.CACHE_MAGIC + b"no line end")\nserver.cache_file("old", ".json").write_text(json.dumps({"digest": "d2", "model": "m", "vectors": [[3, 4]]}), encoding="utf-8")\ngot = server.read_cache("old", "d2", 1)\nout["broken_bin_json_used"] = got is not None and [round(float(x), 3) for x in got[0][0]] == [0.6, 0.8]\n# ... and when the binary form cannot be written, the JSON\'s vectors are used all the same, and the JSON stays\nserver.cache_file("old2", ".json").write_text(json.dumps({"digest": "d3", "model": "m", "vectors": [[3, 4]]}), encoding="utf-8")\ndef fail(*a, **k):\n    raise OSError("disk full")\nserver.write_cache = fail\ngot = server.read_cache("old2", "d3", 1)\nout["unwritable_json_used"] = got is not None and server.cache_file("old2", ".json").exists()\nimport time as _t\nold_tmp, new_tmp, others = cache / "markporto-rag.a.old.tmp", cache / "markporto-rag.b.new.tmp", cache / "accounting-recovery.tmp"\nfor f in (old_tmp, new_tmp, others):\n    f.write_bytes(b"x")\nfor f in (old_tmp, others):\n    os.utime(f, (_t.time() - 7200, _t.time() - 7200))\nserver.remove_old_temporary_files()\nout["tmp_cleanup"] = [old_tmp.exists(), new_tmp.exists(), others.exists()]\nimport struct\ndef rows_ok(values):\n    return server.rows_from_bytes(struct.pack("<%df" % len(values), *values), 1, len(values)) is not None\nout["tiny_rows"] = [rows_ok([0.6, 0.8]), rows_ok([0.0, 0.0]), rows_ok([-1e-8, 0.0])]\nprint(json.dumps(out))\n'


def unit_checks():
    """Checks of the functions that keep the vectors, run in a process of their own (the server module reads its configuration on import)."""
    folder = BASE / "unit"
    folder.mkdir(parents=True, exist_ok=True)
    cfg = folder / "config.json"
    cfg.write_text(json.dumps({"roots": [str(folder)], "cacheDir": str(folder / "cache")}), encoding="utf-8")
    env = dict(os.environ, MARKPORTO_RAG_CONFIG=str(cfg))
    run = subprocess.run([sys.executable, "-c", UNIT, str(SERVER)], capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=300)
    print("unit")
    try:
        out = json.loads(run.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        check("the unit checks run", False, run.stderr[-500:])
        return
    check("two writers of one kept file at once: every write is done, without an error", out["written"] == [30, 30] and out["errors"] == [], out)
    check("... and what is left is whole", any(out["concurrent"]), out["concurrent"])
    check("... and no temporary file stays", out["tmp_left"] == [], out["tmp_left"])
    check("a broken binary file next to a good JSON of 0.4.3: the JSON is used", out["broken_bin_json_used"], out)
    check("the binary form cannot be written: the JSON's vectors are used all the same, and it stays", out["unwritable_json_used"], out)
    check("temporary files of kept vectors: an old one goes, a new one (another server writing) stays, another app's stays",
          out["tmp_cleanup"] == [False, True, True], out["tmp_cleanup"])
    check("kept rows: length 1 and all 0 are taken, a tiny broken one is not", out["tiny_rows"] == [True, True, False], out["tiny_rows"])


async def main():
    two = BASE / "two"
    write(two / "rootA" / "out" / "設計書", [chunk("c0001", "A：タイムアウトは30秒。", path="v1/設計書.docx")])
    write(two / "rootB" / "out" / "設計書", [chunk("c0001", "B：タイムアウトは90秒。", path="v1/設計書.docx")])
    off = {"enabled": False}
    await run("same-place-in-two-folders", {"roots": [str(two / "rootA" / "out"), str(two / "rootB" / "out")], "embedding": off},
              same_place_in_two_folders)

    old = BASE / "old-two"
    for r in ["rootA", "rootB"]:
        row = chunk("c0001", f"{r[-1]}：タイムアウト")
        row.pop("path", None)  # no "path": a chunk written before 0.4.2
        write(old / r / "設計書", [row])
    await run("old-files-in-two-folders", {"roots": [str(old / "rootA"), str(old / "rootB")], "embedding": off}, old_files_in_two_folders)

    letters = BASE / "one-character"
    write(letters / "設計書", [chunk("c0001", "図に timeout を記載"), chunk("c0002", "別の段落", prev="c0001")])
    await run("one-character", {"roots": [str(letters)], "embedding": off}, one_character)
    pairs = BASE / "single-characters"
    write(pairs / "設計書", [chunk("c0001", "図だけの段落"), chunk("c0002", "表だけの段落"), chunk("c0003", "図の説明と表の一覧"),
                             chunk("c0004", "別の段落")])
    await run("single-characters", {"roots": [str(pairs)], "embedding": off}, single_characters)

    one = BASE / "one"
    write(one / "設計書", [chunk("c0001", "A：タイムアウトは30秒。"), chunk("c0002", "B：別の話。", prev="c0001")])
    await run("one-folder", {"roots": [str(one)], "embedding": off}, one_folder_keeps_its_keys)
    await run("no-word-of-the-question", {"roots": [str(one)], "embedding": off}, no_word_of_the_question)

    broken = BASE / "broken"
    bad = [chunk("c0002", "x", kind=None), chunk("c0003", "x", headings=[1]), chunk("c0004", "x", pages=["1"]),
           chunk("c0005", "x", pageImages=[3]), chunk("c0006", "x", next_=7), chunk("c0007", "x", schemaVersion="1"),
           chunk("c0008", "x", path=["v1"]), chunk("", "x")]
    del bad[0]["kind"]
    write(broken / "設計書", [chunk("c0001", "正しい塊 ふくろう"), chunk("c0009", "重複 一つ目"), chunk("c0009", "重複 二つ目")] + bad)
    await run("broken-lines", {"roots": [str(broken)], "embedding": off}, broken_lines)

    three = BASE / "three"
    for folder, text in [("x/out", "A：タイムアウト"), ("y/out", "B：タイムアウト"), ("z/out-2", "C：タイムアウト")]:
        write(three / folder / "設計書", [chunk("c0001", text)])
    await run("three-folders-two-names", {"roots": [str(three / f) for f in ["x/out", "y/out", "z/out-2"]], "embedding": off},
              three_folders_two_names)

    nopages = BASE / "no-pages"
    row = chunk("c0001", "ページのない塊")
    del row["pages"]
    write(nopages / "設計書", [row])
    await run("no-pages", {"roots": [str(nopages)], "embedding": off}, no_pages)

    versions = BASE / "versions"
    write(versions / "a", [chunk("c0001", "v1.2 のタイムアウト", path="v1.2/設計書.docx")])
    write(versions / "b", [chunk("c0001", "v1.20 のタイムアウト", path="v1.20/設計書.docx")])
    await run("whole-folders", {"roots": [str(versions)], "embedding": off}, whole_folders)
    await run("only-a-longer-version", {"roots": [str(versions / "b")], "embedding": off}, only_a_longer_version)

    sharp = BASE / "sharp"
    write(sharp / "C#設計" / "設計書", [chunk("c0001", "前の塊", next_="c0002"), chunk("c0002", "後ろの塊", prev="c0001")])
    await run("hash-in-a-folder", {"roots": [str(sharp)], "embedding": off}, hash_in_a_folder)

    api = Embeddings()
    emb = {"url": api.url, "model": "test-model", "queryPrefix": "", "passagePrefix": "", "retrySeconds": 1}
    (comes_back, other_model, other_model_for_the_question, publishers, two_publishers, batches_of_two_names,
     two_models_kept_then_one, question_not_reached, same_index, bool_index) = embedding_cases(api)
    await run("embeddings-come-back", {"roots": [str(one)], "embedding": dict(emb)}, comes_back)
    await run("other-model", {"roots": [str(one)], "embedding": dict(emb)}, other_model)
    await run("other-model-for-the-question", {"roots": [str(one)], "embedding": dict(emb)}, other_model_for_the_question)
    await run("documents-and-question-of-two-publishers", {"roots": [str(one)], "embedding": dict(emb, queryPrefix="q: ", passagePrefix="p: ")},
              two_publishers)
    many = BASE / "many"
    write(many / "設計書", [chunk(f"c{i:04d}", f"第{i}の塊 タイムアウト") for i in range(1, 18)])
    for n, (first, second) in enumerate([("org-a/test-model", "test-model"), ("test-model", "org-a/test-model")]):
        await run(f"batches-of-two-names-{n}", {"roots": [str(many)], "embedding": dict(emb, queryPrefix="q: ", passagePrefix="p: ", batchSize=16)},
                  batches_of_two_names(first, second))
    await run("publishers", {"roots": [str(one)], "embedding": dict(emb, model="lab/test-model")}, publishers)

    # The vectors kept for one API are not used for another of the same model name.
    shared = str(BASE / "shared-cache")
    await run("cache-first-api", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": shared}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    other = Embeddings()

    async def second_api(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("another API: the passages are embedded by it, not taken from the cache of the first",
              res.get("mode") == "hybrid" and other.inputs > 1, (res.get("mode"), other.inputs))

    async def old_cache(call):
        before = api.inputs
        await call("search_docs", {"query": "タイムアウト"})
        check("vectors kept without the answering model are made again", api.inputs - before > 1, api.inputs - before)
        kept = [read_kept(f) for f in kept_files(shared)]
        check("... and now say it", kept and all(k.get("model") == "test-model" for k in kept), [k.get("model") for k in kept])

    to_old_json(shared)
    for f in Path(shared).glob("*.json"):  # as kept before the answering model was kept (0.4.1)
        saved = json.loads(f.read_text(encoding="utf-8"))
        del saved["model"]
        f.write_text(json.dumps(saved), encoding="utf-8")
    await run("old-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": shared}, old_cache)
    await run("cache-second-api", {"roots": [str(one)], "embedding": dict(emb, url=other.url), "cacheDir": shared}, second_api)
    other.http.shutdown()

    twodocs = BASE / "two-docs"
    write(twodocs / "文書A", [chunk("c0001", "文書A のタイムアウト")])
    write(twodocs / "文書B", [chunk("c0001", "文書B のタイムアウト")])
    mixed_cache = str(BASE / "mixed-cache")
    await run("two-models-kept-then-one", {"roots": [str(twodocs)], "embedding": dict(emb, queryPrefix="q: ", batchSize=1), "cacheDir": mixed_cache},
              two_models_kept_then_one)

    async def restarted(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("vectors of two models kept, then a restart with one model: by meaning at once", res.get("mode") == "hybrid", res)

    first = kept_files(mixed_cache)[0]  # as kept while the API answered as another model for one document
    saved = read_kept(first)
    saved["model"] = "b/test-model"
    write_kept_binary(first, saved, saved["vectors"])
    api.pick = lambda text: "a/test-model"
    await run("restarted-with-one-model", {"roots": [str(twodocs)], "embedding": dict(emb, queryPrefix="q: ", batchSize=1), "cacheDir": mixed_cache}, restarted)
    api.pick = None

    await run("question-not-reached", {"roots": [str(one)], "embedding": dict(emb)}, question_not_reached)
    await run("same-index", {"roots": [str(many)], "embedding": dict(emb)}, same_index)

    async def bad_cache(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a kept vector with a text in it is made again: by meaning", res.get("mode") == "hybrid", res)
        saved = [read_kept(f) for f in kept_files(bad)]
        check("... and kept as numbers, in the binary form", saved and all(f.suffix == ".bin" for f in kept_files(bad))
              and all(isinstance(x, float) for k in saved for v in k["vectors"] for x in v), [f.name for f in kept_files(bad)])

    bad = str(BASE / "bad-vectors")  # not the folder of a case, which holds its config.json
    await run("bad-cache-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": bad}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    to_old_json(bad)
    for f in Path(bad).glob("*.json"):
        saved = json.loads(f.read_text(encoding="utf-8"))
        saved["vectors"][0][0] = "bad"
        f.write_text(json.dumps(saved), encoding="utf-8")
    await run("bad-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": bad}, bad_cache)

    # R2-05: an integer too large for a float in the kept vectors
    huge = str(BASE / "huge-vectors")
    await run("huge-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": huge}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    to_old_json(huge)
    for f in Path(huge).glob("*.json"):
        text = f.read_text(encoding="utf-8")
        saved = json.loads(text)
        first = json.dumps(saved["vectors"][0][0])
        f.write_text(text.replace(first, "1" + "0" * 400, 1), encoding="utf-8")

    async def huge_cache(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a kept integer too large for a float: no error, made again, by meaning", "tool_error" not in res and res.get("mode") == "hybrid", res)

    await run("huge-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": huge}, huge_cache)

    # R2-07
    await run("bool-index", {"roots": [str(one)], "embedding": dict(emb)}, bool_index)  # two chunks: false and true, each once

    # R2-06: list_documents says the search by meaning is not used while a question waits
    async def waiting(call):
        await call("search_docs", {"query": "タイムアウト"})
        api.up = False
        await call("search_docs", {"query": "タイムアウト"})
        docs = await call("list_documents", {})
        check("a question waits for retrySeconds: list_documents says not available", docs["embedding"]["available"] is False, docs["embedding"])
        api.up = True

    await run("waiting-question", {"roots": [str(one)], "embedding": dict(emb, retrySeconds=3600)}, waiting)

    # R2-03: a new file while the API waits for retrySeconds does not call it
    growing = BASE / "growing"
    write(growing / "文書A", [chunk("c0001", "文書A のタイムアウト")])

    async def grows(call):
        api.up = False
        await call("search_docs", {"query": "タイムアウト"})
        before = api.inputs
        write(growing / "文書B", [chunk("c0001", "文書B のタイムアウト")])
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a new file while the API waits: its keywords are found at once", len(res.get("results", [])) == 2, res)
        check("... and the API is not called before retrySeconds", api.inputs == before, api.inputs - before)
        api.up = True

    await run("growing", {"roots": [str(growing)], "embedding": dict(emb, retrySeconds=3600)}, grows)

    # 0.4.4: small files go in one request (LM Studio takes about 2 s for each request, however many chunks it has)
    small = BASE / "small-files"
    for k in range(10):
        write(small / f"文書{k}", [chunk("c0001", f"文書{k} のタイムアウト"), chunk("c0002", f"文書{k} の続き", prev="c0001")])

    async def packed(call):
        before = api.requests
        res = await call("search_docs", {"query": "タイムアウト"})
        check("10 files of 2 chunks: embedded in one request (and the question in one)", res.get("mode") == "hybrid" and api.requests - before == 2,
              (res.get("mode"), api.requests - before))

    await run("small-files-packed", {"roots": [str(small)], "embedding": dict(emb)}, packed)

    async def batched(call):
        before = api.requests
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... with batchSize 4: in 5 requests (2 files each), and the question", res.get("mode") == "hybrid" and api.requests - before == 6,
              (res.get("mode"), api.requests - before))

    await run("small-files-batched", {"roots": [str(small)], "embedding": dict(emb, batchSize=4)}, batched)

    unit_checks()

    # R1-02 (0.4.4): retrySeconds 0 with the API down: the embedding thread does not ask without end
    async def no_storm(call):
        api.up = False
        await call("list_documents", {})
        before = api.requests
        await asyncio.sleep(0.5)
        check("retrySeconds 0, the API down: at most one request in half a second", api.requests - before <= 1, api.requests - before)
        api.up = True

    await run("no-storm", {"roots": [str(one)], "embedding": dict(emb, retrySeconds=0)}, no_storm)

    # R1-03: one chunk whose vector is 0 does not stop the search by meaning of the others
    async def zero_vector(call):
        api.zero = lambda t: "別の話" in t
        res = await call("search_docs", {"query": "タイムアウト"})
        docs = await call("list_documents", {})
        check("a chunk of vector 0: the others are searched by meaning", res.get("mode") == "hybrid" and docs["embedding"]["pendingChunks"] == 0, (res.get("mode"), docs["embedding"]))
        api.zero = None

    await run("zero-vector", {"roots": [str(one)], "embedding": dict(emb)}, zero_vector)

    # R1-10: ties by meaning: the same order with and without numpy (the later chunk first, as 0.4.3)
    ties = BASE / "ties"
    write(ties / "設計書", [chunk("c0001", "aaa"), chunk("c0002", "bbb"), chunk("c0003", "ccc")])

    async def tie(call):
        api.flat = True
        res = await call("search_docs", {"query": "zzz", "k": 1})
        check("a tie by meaning: the later chunk first (with or without numpy)", [x["text"] for x in res.get("results", [])] == ["ccc"], res)
        api.flat = False

    await run("ties", {"roots": [str(ties)], "embedding": dict(emb)}, tie)

    # R1-11, R1-12: the configuration
    async def wrong_type(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("passageChars written as a text: an error that names it", "passageChars" in (res.get("error") or ""), res)

    await run("wrong-type", {"roots": [str(one)], "embedding": dict(emb, passageChars="1500")}, wrong_type)

    async def keywords_only_no_url(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("keywords only with no url: the search works", res.get("mode") == "keyword" and len(res.get("results", [])) == 1, res)

    await run("keywords-only-no-url", {"roots": [str(one)], "embedding": {"enabled": False, "url": None}}, keywords_only_no_url)

    # R1-06, R1-07: a large file, batch by batch
    large = BASE / "large"
    write(large / "大きい", [chunk(f"c{i:04d}", f"大きい文書 {i} タイムアウト") for i in range(1, 13)])
    hold_large = threading.Event()

    async def large_file(call):
        api.hook = lambda texts: hold_large.wait(10) if any("大きい文書 1 " in t for t in texts) else None
        await call("list_documents", {})
        await asyncio.sleep(0.5)  # the first batch is held
        (large / "大きい" / "設計書_chunks.jsonl").unlink()
        write(large / "新しい", [chunk("c0001", "新しい文書 タイムアウト")])
        await call("list_documents", {})
        api.hook = None
        before = len(api.seen)
        hold_large.set()
        await asyncio.sleep(1.0)
        later = api.seen[before:]
        check("a large file gone while its first batch is embedded: the rest of it is not sent", not any("大きい文書" in t for t in later), later)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... and the new file is embedded", res.get("mode") == "hybrid", res)

    await run("large-file-gone", {"roots": [str(large)], "embedding": dict(emb, batchSize=1)}, large_file)

    progress = BASE / "progress"
    write(progress / "四つ", [chunk(f"c{i:04d}", f"四つの塊 {i}") for i in range(1, 5)])
    hold_progress = threading.Event()

    async def progress_of_a_large_file(call):
        api.hook = lambda texts: hold_progress.wait(10) if any("四つの塊 3" in t for t in texts) else None
        await call("list_documents", {})
        await asyncio.sleep(0.8)  # the first batch (1, 2) is done, the second (3, 4) is held
        docs = await call("list_documents", {})
        api.hook = None
        hold_progress.set()
        check("a large file, half embedded: pendingChunks says 2", docs["embedding"]["pendingChunks"] == 2, docs["embedding"])

    await run("progress-of-a-large-file", {"roots": [str(progress)], "embedding": dict(emb, batchSize=2)}, progress_of_a_large_file)

    # R1-13: the warning of a file goes with it
    warned = BASE / "warned"
    write(warned / "文書A", [chunk("c0001", "文書A のタイムアウト")])

    async def warning_goes(call):
        await call("search_docs", {"query": "タイムアウト"})
        api.pick = lambda t: "other/thing" if "文書B" in t else None
        write(warned / "文書B", [chunk("c0001", "文書B のタイムアウト")])
        await call("search_docs", {"query": "タイムアウト"})
        docs = await call("list_documents", {})
        had = any("search by meaning is off" in w for w in docs["warnings"])
        (warned / "文書B" / "設計書_chunks.jsonl").unlink()
        api.pick = None
        await asyncio.sleep(1.5)
        res = await call("search_docs", {"query": "タイムアウト"})
        docs = await call("list_documents", {})
        check("a file answered by another model, then gone: its warning goes too", had and res.get("mode") == "hybrid"
              and not any("search by meaning is off" in w for w in docs["warnings"]), (had, res.get("mode"), docs["warnings"]))

    await run("warning-goes", {"roots": [str(warned)], "embedding": dict(emb)}, warning_goes)

    # R1-14: a JSON kept by 0.4.3, made here as 0.4.3 made it (not from this server's files): taken over, normalized
    old43 = BASE / "kept-by-043"
    write(old43 / "設計書", [chunk("c0001", "タイムアウト 旧版")])
    f43 = old43 / "設計書" / "設計書_chunks.jsonl"
    cache43 = BASE / "kept-by-043-cache"
    cache43.mkdir(parents=True, exist_ok=True)
    signature = json.dumps([emb["url"].rstrip("/"), emb["model"], emb["passagePrefix"], 1500]).encode()
    digest43 = hashlib.sha1(hashlib.sha1(f43.read_bytes()).hexdigest().encode() + signature).hexdigest()
    (cache43 / (hashlib.sha1(str(f43).encode()).hexdigest() + ".json")).write_text(
        json.dumps({"digest": digest43, "model": "test-model", "vectors": [[3, 4, 0, 0, 0, 0, 0, 0]]}), encoding="utf-8")

    async def kept_by_043(call):
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        kept = [read_kept(f) for f in kept_files(cache43)]
        check("a JSON kept by 0.4.3: used without the API (only the question is sent)", res.get("mode") == "hybrid" and api.inputs - before == 1,
              (res.get("mode"), api.inputs - before))
        check("... kept as binary, normalized ([3, 4] -> [0.6, 0.8])", len(kept) == 1 and [round(x, 3) for x in kept[0]["vectors"][0][:2]] == [0.6, 0.8]
              and kept_files(cache43)[0].suffix == ".bin", kept)

    await run("kept-by-043", {"roots": [str(old43)], "embedding": dict(emb), "cacheDir": str(cache43)}, kept_by_043)

    # R2-01 (0.4.4): a file that breaks the reading (a line nested too deep) does not keep the other files' changes out
    deep = BASE / "deep"
    write(deep / "A", [chunk("c0001", "before 変更前")])

    async def deep_line(call):
        await call("search_docs", {"query": "before"})
        write(deep / "A", [chunk("c0001", "after-edit 変更後")])
        write(deep / "Z", [chunk("c0001", "zzz")], raw=["[" * 100000 + "]" * 100000])
        res = await call("search_docs", {"query": "after-edit"})
        check("a line nested too deep in another file: the changed file is searched", [x["text"] for x in res.get("results", [])] == ["after-edit 変更後"], res)
        (deep / "Z" / "設計書_chunks.jsonl").unlink()
        out = await call("read_chunk", {"key": "A/設計書_chunks.jsonl#c0001"})
        check("... and read", out.get("chunk", {}).get("text") == "after-edit 変更後", out)

    await run("deep-line", {"roots": [str(deep)], "embedding": off}, deep_line)

    # R2-04: a file replaced keeping its size and time is read again
    same_size = BASE / "same-size"
    write(same_size / "設計書", [chunk("c0001", "apple")])

    async def replaced(call):
        await call("search_docs", {"query": "apple"})
        f = same_size / "設計書" / "設計書_chunks.jsonl"
        st = f.stat()
        tmp = f.with_name("new.tmp")
        tmp.write_text(f.read_text(encoding="utf-8").replace("apple", "peach"), encoding="utf-8")
        os.utime(tmp, ns=(st.st_atime_ns, st.st_mtime_ns))
        os.replace(tmp, f)
        write(same_size / "別", [chunk("c0001", "other")])
        out = await call("read_chunk", {"key": "設計書/設計書_chunks.jsonl#c0001"})
        check("a file replaced keeping its size and time: read again", out.get("chunk", {}).get("text") == "peach", out)

    await run("replaced-same-size", {"roots": [str(same_size)], "embedding": off}, replaced)

    # R2-08: a tie across files: the order of the files is that of 0.4.3 (Path order; on Windows without case)
    cases = BASE / "cases"
    write(cases / "Zoo", [chunk("c0001", "matching")])
    write(cases / "alpha", [chunk("c0001", "matching")])

    async def tie_across_files(call):
        res = await call("search_docs", {"query": "matching", "k": 1})
        expected = "Zoo/設計書_chunks.jsonl#c0001" if sys.platform == "win32" else "alpha/設計書_chunks.jsonl#c0001"
        check("a tie across files: the later file first, files in Path order (as 0.4.3)", [x["key"] for x in res.get("results", [])] == [expected], res)

    await run("tie-across-files", {"roots": [str(cases)], "embedding": off}, tie_across_files)

    # R2-06, R2-07: the progress of a large file
    four = BASE / "four"
    write(four / "四つ", [chunk(f"c{i:04d}", f"四つ目の塊 {i}") for i in range(1, 5)])
    hold_four = threading.Event()

    async def progress_in_the_note(call):
        api.hook = lambda texts: hold_four.wait(10) if any("四つ目の塊 3" in t for t in texts) else None
        res = await call("search_docs", {"query": "四つ目"})
        api.hook = None
        hold_four.set()
        check("a search that waited: its note counts the batches done (2 of 4)", "(2 of 4 chunks)" in (res.get("note") or ""), res.get("note"))

    await run("progress-in-the-note", {"roots": [str(four)], "embedding": dict(emb, batchSize=2, waitSeconds=0.8)}, progress_in_the_note)

    hold_fail = threading.Event()

    async def failed_batch(call):
        api.fail = lambda t: "四つ目の塊 3" in t
        api.hook = lambda texts: hold_fail.wait(10) if any("四つ目の塊 3" in t for t in texts) else None
        await call("list_documents", {})
        await asyncio.sleep(0.8)
        held = await call("list_documents", {})
        api.hook = None
        hold_fail.set()
        await asyncio.sleep(0.8)
        docs = await call("list_documents", {})
        api.fail = None
        check("the second batch of a large file held: 2 pending", held["embedding"]["pendingChunks"] == 2, held["embedding"])
        check("... then failed: all 4 are pending again", docs["embedding"]["pendingChunks"] == 4, docs["embedding"])

    await run("failed-batch", {"roots": [str(four)], "embedding": dict(emb, batchSize=2, retrySeconds=3600)}, failed_batch)

    # R2-02, R2-03: vectors of two dimensions: not made again without end, and made again by the thread at retry_at
    dims2 = BASE / "two-dims-2"
    write(dims2 / "文書A", [chunk("c0001", "文書A のタイムアウト")])
    write(dims2 / "文書B", [chunk("c0001", "文書B のタイムアウト")])

    async def two_dims_storm(call):
        api.dims = lambda t: 2 if "文書B" in t else 3
        await call("list_documents", {})
        await asyncio.sleep(1.0)
        before = api.requests
        await asyncio.sleep(1.0)
        check("vectors of two dimensions, retrySeconds 0: at most one round (2 requests) a second", api.requests - before <= 2, api.requests - before)
        api.dims = None

    await run("two-dims-storm", {"roots": [str(dims2)], "embedding": dict(emb, batchSize=1, retrySeconds=0)}, two_dims_storm)

    async def two_dims_by_itself(call):
        api.dims = lambda t: 2 if "文書B" in t else 3
        await call("list_documents", {})
        await asyncio.sleep(0.5)  # made, made again, still of two dimensions: waits retrySeconds
        api.dims = None
        before = api.requests
        await asyncio.sleep(2.0)
        check("... the API gives one dimension again: the thread makes them again by itself at retry_at", api.requests - before >= 2, api.requests - before)
        docs = await call("list_documents", {})
        check("... and the search by meaning is there", docs["embedding"]["available"] is True, docs["embedding"])

    await run("two-dims-by-itself", {"roots": [str(dims2)], "embedding": dict(emb, batchSize=1, retrySeconds=1)}, two_dims_by_itself)

    # R3-01 (0.4.4): a file written over in place, its size and time kept: read again when the files change
    in_place = BASE / "in-place"
    write(in_place / "設計書", [chunk("c0001", "apple")])

    async def written_in_place(call):
        await call("search_docs", {"query": "apple"})
        f = in_place / "設計書" / "設計書_chunks.jsonl"
        st = f.stat()
        f.write_text(f.read_text(encoding="utf-8").replace("apple", "peach"), encoding="utf-8")
        os.utime(f, ns=(st.st_atime_ns, st.st_mtime_ns))
        write(in_place / "別", [chunk("c0001", "other")])
        out = await call("read_chunk", {"key": "設計書/設計書_chunks.jsonl#c0001"})
        check("a file written over in place, size and time kept: read again", out.get("chunk", {}).get("text") == "peach", out)

    await run("written-in-place", {"roots": [str(in_place)], "embedding": off}, written_in_place)

    # R3-02: the question's vectors of another dimension than the documents', for good: the documents are not sent at each search
    q_dims = BASE / "question-dims"
    write(q_dims / "設計書", [chunk(f"c{i:04d}", f"質問と違う次元 {i}") for i in range(1, 4)])

    async def question_of_another_dimension(call):
        api.dims = lambda t: 3 if t.startswith("q: ") else 2
        before = len(api.seen)
        for _ in range(4):
            await call("search_docs", {"query": "次元"})
        await asyncio.sleep(0.5)
        passages = [t for t in api.seen[before:] if not t.startswith("q: ")]
        api.dims = None
        check("the question of another dimension at every search: the documents sent at most twice", len(passages) <= 2, len(passages))

    await run("question-of-another-dimension", {"roots": [str(q_dims)], "embedding": dict(emb, queryPrefix="q: ", retrySeconds=3600)},
              question_of_another_dimension)

    # R3-03: a file answered by another model, then gone: the others are searched by meaning at once
    other_gone = BASE / "other-gone"
    write(other_gone / "文書A", [chunk("c0001", "文書A のタイムアウト")])

    async def other_model_gone(call):
        await call("search_docs", {"query": "タイムアウト"})
        api.pick = lambda t: "other/thing" if "文書B" in t else None
        write(other_gone / "文書B", [chunk("c0001", "文書B のタイムアウト")])
        await call("search_docs", {"query": "タイムアウト"})
        (other_gone / "文書B" / "設計書_chunks.jsonl").unlink()
        api.pick = None
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a file of another model gone: the others by meaning at once (retrySeconds 3600)", res.get("mode") == "hybrid", res)

    await run("other-model-gone", {"roots": [str(other_gone)], "embedding": dict(emb, retrySeconds=3600)}, other_model_gone)

    # R3-06: the same vector for several chunks (384 dimensions): the same tie order with and without numpy
    ties384 = BASE / "ties-384"
    write(ties384 / "設計書", [chunk(f"c{i:04d}", f"同じ {i}") for i in range(1, 6)])

    async def tie_384(call):
        api.dims = lambda t: 384
        api.alias = lambda t: t if t.startswith("q: ") else "the same passage"
        res = await call("search_docs", {"query": "zzz", "k": 1})
        api.dims = None
        api.alias = None
        check("5 chunks of one vector (384 dimensions): the later chunk first, with or without numpy", [x["text"] for x in res.get("results", [])] == ["同じ 5"], res)

    await run("ties-384", {"roots": [str(ties384)], "embedding": dict(emb, queryPrefix="q: ")}, tie_384)

    # R3-08: a line nested too deep is one bad line: the good chunks of the same file stay
    deep_same = BASE / "deep-same"
    write(deep_same / "設計書", [chunk("c0001", "正しい行 ふくろう")], raw=["[" * 100000 + "]" * 100000])

    async def deep_in_the_same_file(call):
        res = await call("search_docs", {"query": "ふくろう"})
        check("a line nested too deep: the good chunk of the same file is found", [x["text"] for x in res.get("results", [])] == ["正しい行 ふくろう"], res)

    await run("deep-in-the-same-file", {"roots": [str(deep_same)], "embedding": off}, deep_in_the_same_file)

    # R3-09: the configuration
    async def embedding_null(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("embedding written as null: an error of the configuration", "embedding" in (res.get("error") or ""), res)

    await run("embedding-null", {"roots": [str(one)], "embedding": None}, embedding_null)

    async def timeout_text(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("timeoutSeconds written as a text: an error that names it", "timeoutSeconds" in (res.get("error") or ""), res)

    await run("timeout-text", {"roots": [str(one)], "embedding": dict(emb, timeoutSeconds="120")}, timeout_text)

    # R4-01: vectors of two dimensions, and a file of another model waiting for its retry: the tools are not held
    stuck = BASE / "stuck"
    write(stuck / "文書A", [chunk("c0001", "文書A のタイムアウト")])
    write(stuck / "文書B", [chunk("c0001", "文書B のタイムアウト")])

    async def mixed_and_waiting(call):
        api.dims = lambda t: 2 if "文書B" in t else 3
        await call("list_documents", {})
        await asyncio.sleep(0.6)  # made, made again, still mixed: waits retrySeconds
        api.pick = lambda t: "other/thing" if "文書C" in t else None
        write(stuck / "文書C", [chunk("c0001", "文書C のタイムアウト")])
        await call("list_documents", {})
        await asyncio.sleep(4.0)  # the retry of the mixed vectors is due (3 s), C waits for its own (asked again at ~3.6 s, then ~6.6 s)
        started = time.monotonic()
        await asyncio.wait_for(call("read_chunk", {"key": "文書A/設計書_chunks.jsonl#c0001"}), 10)
        took = time.monotonic() - started
        api.dims = None
        api.pick = None
        check("mixed vectors due again while a file waits for its own retry: a tool answers at once", took < 1.0, round(took, 2))

    await run("mixed-and-waiting", {"roots": [str(stuck)], "embedding": dict(emb, batchSize=1, retrySeconds=3, waitSeconds=0)}, mixed_and_waiting)

    # R3-09: kept vectors are used again: after a restart only the question goes to the API
    reuse = str(BASE / "reuse-cache")
    await run("reuse-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": reuse}, lambda call: call("search_docs", {"query": "タイムアウト"}))

    async def reused(call):
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a restart with the same documents and API: the kept vectors are used (only the question is sent)",
              res.get("mode") == "hybrid" and api.inputs - before == 1, (res.get("mode"), api.inputs - before))

    await run("reuse-again", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": reuse}, reused)

    # 0.4.4: vectors kept as JSON by 0.4.3 are turned into the binary form, without asking the API again
    taken = str(BASE / "taken-over-cache")
    await run("taken-over-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": taken}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    to_old_json(taken)

    async def taken_over(call):
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("vectors kept as JSON by 0.4.3: used without the API (only the question is sent)",
              res.get("mode") == "hybrid" and api.inputs - before == 1, (res.get("mode"), api.inputs - before))
        check("... and kept in the binary form instead", [f.suffix for f in kept_files(taken)] == [".bin"], [f.name for f in kept_files(taken)])

    await run("taken-over", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": taken}, taken_over)

    broken_bin = str(BASE / "broken-binary")
    await run("broken-binary-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": broken_bin},
              lambda call: call("search_docs", {"query": "タイムアウト"}))
    for f in kept_files(broken_bin):
        head = read_kept(f)
        head["vectors"][0][0] = float("nan")
        write_kept_binary(f, head, head["vectors"])

    async def broken_binary(call):
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a kept binary file with NaN is made again: by meaning", res.get("mode") == "hybrid" and api.inputs - before > 1,
              (res.get("mode"), api.inputs - before))

    await run("broken-binary", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": broken_bin}, broken_binary)

    # R3-07: a kept value whose square a float cannot hold
    big = str(BASE / "big-vectors")
    await run("big-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": big}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    to_old_json(big)
    for f in Path(big).glob("*.json"):
        text = f.read_text(encoding="utf-8")
        first = json.dumps(json.loads(text)["vectors"][0][0])
        f.write_text(text.replace(first, "1" + "0" * 200, 1), encoding="utf-8")

    async def big_cache(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a kept value of 10**200: made again, by meaning", res.get("mode") == "hybrid", res)

    await run("big-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": big}, big_cache)

    # R3-08: a retrySeconds too large for a float
    async def huge_retry(call):
        api.up = False
        res = await call("search_docs", {"query": "タイムアウト"})
        check("retrySeconds of 10**400 with the API down: the keywords still answer", "tool_error" not in res and len(res.get("results", [])) == 1, res)
        api.up = True

    await run("huge-retry", {"roots": [str(one)], "embedding": dict(emb, retrySeconds=10 ** 400)}, huge_retry)

    # R3-03: the wait for the API is kept when the files change, and a failure of the documents holds the questions back too
    moving = BASE / "moving"
    write(moving / "文書A", [chunk("c0001", "文書A のタイムアウト")])

    async def files_change_during_the_wait(call):
        await call("search_docs", {"query": "タイムアウト"})
        api.up = False
        await call("search_docs", {"query": "タイムアウト"})  # the question fails: wait
        f = moving / "文書A" / "設計書_chunks.jsonl"
        os.utime(f, ns=(f.stat().st_atime_ns, f.stat().st_mtime_ns + 10_000_000))
        docs = await call("list_documents", {})
        check("a file changes during the wait: still not available", docs["embedding"]["available"] is False, docs["embedding"])
        before = api.inputs
        await call("search_docs", {"query": "タイムアウト"})
        check("... and the API is not called", api.inputs == before, api.inputs - before)
        api.up = True

    await run("files-change-during-the-wait", {"roots": [str(moving)], "embedding": dict(emb, retrySeconds=3600)}, files_change_during_the_wait)

    gone = BASE / "gone"
    write(gone / "文書A", [chunk("c0001", "文書A のタイムアウト")])

    async def new_file_fails_then_goes(call):
        await call("search_docs", {"query": "タイムアウト"})  # A is kept
        api.up = False
        write(gone / "文書B", [chunk("c0001", "文書B のタイムアウト")])
        await call("search_docs", {"query": "タイムアウト"})  # B cannot be embedded: wait
        (gone / "文書B" / "設計書_chunks.jsonl").unlink()
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a document that could not be embedded goes away: the question still waits", api.inputs == before and res.get("mode") == "keyword",
              (api.inputs - before, res.get("mode")))
        api.up = True

    await run("new-file-fails-then-goes", {"roots": [str(gone)], "embedding": dict(emb, retrySeconds=3600)}, new_file_fails_then_goes)

    # R3-04: every kept vector of an old model, the API now answers as another model of the configured name
    old_model = str(BASE / "old-model-cache")
    api.model = "org-a/test-model"
    await run("old-model-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": old_model}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    api.model = "org-b/test-model"

    async def new_model(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("kept by org-a, answered by org-b now: keywords only, with a note", res.get("mode") == "keyword" and "again" in (res.get("note") or ""), res)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... and made again at the next search: by meaning", res.get("mode") == "hybrid", res)

    await run("old-model-new-model", {"roots": [str(one)], "embedding": dict(emb, retrySeconds=0), "cacheDir": old_model}, new_model)
    api.model = None

    # R3-01: a search reads one index from start to end, though another call comes while it waits for the API
    versions2 = BASE / "versions-race"
    write(versions2 / "manual", [chunk("c0001", "apple りんご", path="v1/manual.docx")])
    release = threading.Event()

    async def search_and_change(call):
        await call("search_docs", {"query": "apple"})
        api.hook = lambda texts: release.wait(10) if texts[0].startswith("q: ") else None
        searching = asyncio.ensure_future(call("search_docs", {"query": "apple", "path": "v1/"}))
        await asyncio.sleep(0.8)
        write(versions2 / "manual", [chunk("c0001", "pear なし", path="v2/manual.docx")])
        listing = asyncio.ensure_future(call("list_documents", {}))
        await asyncio.sleep(0.8)
        api.hook = None
        release.set()
        res, _ = await asyncio.gather(searching, listing)
        check("another call while a search waits: the search answers from the index it began with",
              [(x["path"], x["text"]) for x in res.get("results", [])] == [("v1/manual.docx", "apple りんご")], res)

    await run("search-and-change", {"roots": [str(versions2)], "embedding": dict(emb, queryPrefix="q: ")}, search_and_change)

    # R1-01 (0.4.4): the same while the search waits for the documents' embeddings (not the question's)
    versions3 = BASE / "versions-wait"
    write(versions3 / "manual", [chunk("c0001", "apple りんご", path="v1/manual.docx")])
    hold_docs = threading.Event()

    async def search_waits_for_documents(call):
        api.hook = lambda texts: hold_docs.wait(10) if any("apple" in t for t in texts) and not texts[0].startswith("q: ") else None
        searching = asyncio.ensure_future(call("search_docs", {"query": "apple", "path": "v1/"}))
        await asyncio.sleep(0.8)  # the search waits for the documents' embeddings
        write(versions3 / "manual", [chunk("c0001", "pear なし", path="v2/manual.docx")])
        listing = asyncio.ensure_future(call("list_documents", {}))
        await asyncio.sleep(0.8)
        api.hook = None
        hold_docs.set()
        res, docs = await asyncio.gather(searching, listing)
        check("another call while a search waits for the documents' embeddings: the search answers from the index it began with",
              [(x["path"], x["text"]) for x in res.get("results", [])] == [("v1/manual.docx", "apple りんご")], res)

    await run("search-waits-for-documents", {"roots": [str(versions3)], "embedding": dict(emb, queryPrefix="q: ")}, search_waits_for_documents)

    # R3-02: two rebuilds of the index do not cross
    crossing = BASE / "crossing"
    write(crossing / "文書", [chunk("c0001", "ひとつめ first")])
    hold = threading.Event()

    async def rebuilds_cross(call):
        api.hook = lambda texts: hold.wait(10) if any("ひとつめ" in t for t in texts) and len(texts) == 1 and not texts[0].startswith("q: ") else None
        first = asyncio.ensure_future(call("list_documents", {}))
        await asyncio.sleep(0.8)
        api.hook = None
        write(crossing / "文書", [chunk("c0001", "ひとつめ first"), chunk("c0002", "ふたつめ second", prev="c0001")])
        second = asyncio.ensure_future(call("list_documents", {}))
        await asyncio.sleep(0.8)
        hold.set()
        await asyncio.gather(first, second)
        res = await call("search_docs", {"query": "ふたつめ"})
        check("a file read again while its old text is being embedded: the new chunks stand, by meaning",
              res.get("mode") == "hybrid" and [(x["key"], x["text"]) for x in res.get("results", [])][:1] == [("文書/設計書_chunks.jsonl#c0002", "ふたつめ second")], res)

    await run("rebuilds-cross", {"roots": [str(crossing)], "embedding": dict(emb, queryPrefix="q: ")}, rebuilds_cross)

    # R3-05: a file locked for a moment comes back when it is free
    locked_root = BASE / "locked"
    write(locked_root / "設計書", [chunk("c0001", "ロックされた文書")])
    if sys.platform == "win32":
        import msvcrt

        async def locked(call):
            f = locked_root / "設計書" / "設計書_chunks.jsonl"
            size = f.stat().st_size
            handle = open(f, "rb")
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, size)
            docs = await call("list_documents", {})
            msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, size)
            handle.close()
            check("a locked file is not read (a warning)", docs["documents"] == [] and any("could not be read" in w for w in docs["warnings"]), docs)
            docs = await call("list_documents", {})
            check("... and read when it is free again", len(docs["documents"]) == 1, docs)

        await run("locked-file", {"roots": [str(locked_root)], "embedding": off}, locked)

    # R2-02: vectors of two dimensions under one model name come back when the API gives one dimension
    dims_root = BASE / "two-dims"
    write(dims_root / "文書A", [chunk("c0001", "文書A のタイムアウト")])
    write(dims_root / "文書B", [chunk("c0001", "文書B のタイムアウト")])
    dims_cache = str(BASE / "two-dims-cache")

    async def two_dims(call):
        api.dims = lambda t: 2 if "文書B" in t else 3
        res = await call("search_docs", {"query": "タイムアウト"})
        check("vectors of two dimensions: keywords only", res.get("mode") == "keyword", res)
        api.dims = None
        await asyncio.sleep(1.5)
        res = await call("search_docs", {"query": "タイムアウト"})
        check("... one dimension again: by meaning, after retrySeconds", res.get("mode") == "hybrid", res)

    await run("two-dims", {"roots": [str(dims_root)], "embedding": dict(emb, batchSize=1), "cacheDir": dims_cache}, two_dims)  # one file a request

    # R2-01: a file changed while its neighbour was being embedded is embedded again from what it says now
    race = BASE / "race"
    write(race / "A", [chunk("c0001", "りんご apple")])
    write(race / "B", [chunk("c0001", "バナナ banana"), chunk("c0002", "バナナの続き", prev="c0001")])

    async def changed_while_embedding(call):
        api.up = False
        await call("search_docs", {"query": "バナナ"})  # the chunks are read; the API is down
        api.up = True

        def change_b(texts):
            if any("apple" in t for t in texts):
                write(race / "B", [chunk("c0001", "ココナツ coconut"), chunk("c0002", "ココナツの続き", prev="c0001")])
                api.hook = None

        api.hook = change_b
        await asyncio.sleep(1.5)
        await call("search_docs", {"query": "バナナ"})  # A is embedded, B changes meanwhile; B is embedded from the chunks read before
        before = api.inputs
        res = await call("search_docs", {"query": "ココナツ"})
        check("... and its new text is found", res.get("results") and "ココナツ" in res["results"][0]["text"], res)

    def vectors_of(texts):  # as the fake API makes them, normalized (passagePrefix "", headings ["設計書"])
        out = []
        for t in texts:
            v = [b / 255 for b in hashlib.sha256(("設計書\n" + t).encode()).digest()[:8]]
            n = sum(x * x for x in v) ** 0.5
            out.append([x / n for x in v])
        return out

    def same(a, b):
        return len(a) == len(b) and all(len(u) == len(w) for u, w in zip(a, b)) and all(abs(x - y) < 1e-5 for u, w in zip(a, b) for x, y in zip(u, w))

    race_cache = str(BASE / "race-cache")
    await run("changed-while-embedding", {"roots": [str(race)], "embedding": dict(emb), "cacheDir": race_cache}, changed_while_embedding)
    kept = [read_kept(f)["vectors"] for f in kept_files(race_cache)]
    check("a file changed while being embedded: its vectors are of the new text", any(same(k, vectors_of(["ココナツ coconut", "ココナツの続き"])) for k in kept), "")
    check("... and none of the old text is kept", not any(same(k, vectors_of(["バナナ banana", "バナナの続き"])) for k in kept), "")
    api.http.shutdown()

    print(f"\n{count - len(failures)}/{count} passed")
    shutil.rmtree(BASE, ignore_errors=True)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    asyncio.run(main())
