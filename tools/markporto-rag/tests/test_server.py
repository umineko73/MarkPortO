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
import sys
import tempfile
import threading
import time
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


class Embeddings:
    """An OpenAI compatible embedding API: a vector from the characters of the text; it can be down, or answer as another model."""

    def __init__(self):
        self.up = True
        self.model: str | None = None  # the name it answers with; None: the name asked for
        self.inputs = 0  # the texts it was asked for
        self.pick = None  # the model it answers with for a text, when it is not always the same
        self.same_index = False  # every vector with index 0, as a broken API might
        self.bool_index = False  # false / true for the indexes 0 and 1
        self.dims = None  # the length of the vector for a text, when it is not 8
        self.hook = None  # called with the texts of each request, before it is answered
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                outer.inputs += len(body["input"])
                if outer.hook:
                    outer.hook(body["input"])
                if not outer.up:
                    self.send_response(503)
                    self.end_headers()
                    return
                def vector(t):
                    n = outer.dims(t) if outer.dims else 8
                    return [b / 255 for b in hashlib.sha256(t.encode()).digest()[:n]]

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

        self.http = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
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
        cache = list((BASE / "other-model" / "cache").glob("*.json"))
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
        cache = list((BASE / "same-index" / "cache").glob("*.json"))
        check("... and nothing kept", cache == [], cache)
        api.same_index = False

    async def bool_index(call):
        api.bool_index = True
        res = await call("search_docs", {"query": "タイムアウト"})
        check("indexes false / true are not 0 / 1: keywords only", res.get("mode") == "keyword", res)
        cache = list((BASE / "bool-index" / "cache").glob("*.json"))
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
        await run(f"batches-of-two-names-{n}", {"roots": [str(many)], "embedding": dict(emb, queryPrefix="q: ", passagePrefix="p: ")},
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
        for f in Path(shared).glob("*.json"):  # as kept before the answering model was kept
            saved = json.loads(f.read_text(encoding="utf-8"))
            del saved["model"]
            f.write_text(json.dumps(saved), encoding="utf-8")
        before = api.inputs
        await call("search_docs", {"query": "タイムアウト"})
        check("vectors kept without the answering model are made again", api.inputs - before > 1, api.inputs - before)
        kept = [json.loads(f.read_text(encoding="utf-8")) for f in Path(shared).glob("*.json")]
        check("... and now say it", kept and all(k.get("model") == "test-model" for k in kept), [k.get("model") for k in kept])

    await run("old-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": shared}, old_cache)
    await run("cache-second-api", {"roots": [str(one)], "embedding": dict(emb, url=other.url), "cacheDir": shared}, second_api)
    other.http.shutdown()

    twodocs = BASE / "two-docs"
    write(twodocs / "文書A", [chunk("c0001", "文書A のタイムアウト")])
    write(twodocs / "文書B", [chunk("c0001", "文書B のタイムアウト")])
    mixed_cache = str(BASE / "mixed-cache")
    await run("two-models-kept-then-one", {"roots": [str(twodocs)], "embedding": dict(emb, queryPrefix="q: "), "cacheDir": mixed_cache},
              two_models_kept_then_one)

    async def restarted(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("vectors of two models kept, then a restart with one model: by meaning at once", res.get("mode") == "hybrid", res)

    first = sorted(Path(mixed_cache).glob("*.json"))[0]  # as kept while the API answered as another model for one document
    saved = json.loads(first.read_text(encoding="utf-8"))
    saved["model"] = "b/test-model"
    first.write_text(json.dumps(saved), encoding="utf-8")
    api.pick = lambda text: "a/test-model"
    await run("restarted-with-one-model", {"roots": [str(twodocs)], "embedding": dict(emb, queryPrefix="q: "), "cacheDir": mixed_cache}, restarted)
    api.pick = None

    await run("question-not-reached", {"roots": [str(one)], "embedding": dict(emb)}, question_not_reached)
    await run("same-index", {"roots": [str(many)], "embedding": dict(emb)}, same_index)

    async def bad_cache(call):
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a kept vector with a text in it is made again: by meaning", res.get("mode") == "hybrid", res)
        saved = [json.loads(f.read_text(encoding="utf-8")) for f in Path(bad).glob("*.json")]
        check("... and kept as numbers", saved and all(isinstance(x, float) for k in saved for v in k["vectors"] for x in v), "")

    bad = str(BASE / "bad-vectors")  # not the folder of a case, which holds its config.json
    await run("bad-cache-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": bad}, lambda call: call("search_docs", {"query": "タイムアウト"}))
    for f in Path(bad).glob("*.json"):
        saved = json.loads(f.read_text(encoding="utf-8"))
        saved["vectors"][0][0] = "bad"
        f.write_text(json.dumps(saved), encoding="utf-8")
    await run("bad-cache", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": bad}, bad_cache)

    # R2-05: an integer too large for a float in the kept vectors
    huge = str(BASE / "huge-vectors")
    await run("huge-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": huge}, lambda call: call("search_docs", {"query": "タイムアウト"}))
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

    # R3-09: kept vectors are used again: after a restart only the question goes to the API
    reuse = str(BASE / "reuse-cache")
    await run("reuse-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": reuse}, lambda call: call("search_docs", {"query": "タイムアウト"}))

    async def reused(call):
        before = api.inputs
        res = await call("search_docs", {"query": "タイムアウト"})
        check("a restart with the same documents and API: the kept vectors are used (only the question is sent)",
              res.get("mode") == "hybrid" and api.inputs - before == 1, (res.get("mode"), api.inputs - before))

    await run("reuse-again", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": reuse}, reused)

    # R3-07: a kept value whose square a float cannot hold
    big = str(BASE / "big-vectors")
    await run("big-first", {"roots": [str(one)], "embedding": dict(emb), "cacheDir": big}, lambda call: call("search_docs", {"query": "タイムアウト"}))
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
        check("two rebuilds of the index at once: the later one stands, by meaning", res.get("mode") == "hybrid" and len(res.get("results", [])) >= 1, res)

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

    await run("two-dims", {"roots": [str(dims_root)], "embedding": dict(emb), "cacheDir": dims_cache}, two_dims)

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
        check("a file changed while being embedded is embedded again", api.inputs - before >= 3, api.inputs - before)
        check("... and its new text is found", res.get("results") and "ココナツ" in res["results"][0]["text"], res)

    await run("changed-while-embedding", {"roots": [str(race)], "embedding": dict(emb)}, changed_while_embedding)
    api.http.shutdown()

    print(f"\n{count - len(failures)}/{count} passed")
    shutil.rmtree(BASE, ignore_errors=True)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    asyncio.run(main())
