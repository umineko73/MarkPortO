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
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if not outer.up:
                    self.send_response(503)
                    self.end_headers()
                    return
                data = [{"index": i, "embedding": [b / 255 for b in hashlib.sha256(t.encode()).digest()[:8]]} for i, t in enumerate(body["input"])]
                answer = json.dumps({"object": "list", "model": outer.model or body["model"], "data": data}).encode()
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
    check("the path stays the place in the converted folder (no name of the searched folder)", paths == {"設計書/設計書_chunks.jsonl"} or paths == {"v1/設計書.docx"}, paths)
    narrowed = await call("search_docs", {"query": "タイムアウト", "path": "v1"})
    check("path narrows by the path of the chunk, as before", len(narrowed.get("results", [])) == 2, narrowed)


async def one_folder_keeps_its_keys(call):
    res = await call("search_docs", {"query": "タイムアウト"})
    check("with one folder the key is as before", [x["key"] for x in res["results"]] == ["設計書/設計書_chunks.jsonl#c0001"], res)


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

    return comes_back, other_model, other_model_for_the_question


async def main():
    two = BASE / "two"
    write(two / "rootA" / "out" / "設計書", [chunk("c0001", "A：タイムアウトは30秒。", path="v1/設計書.docx")])
    write(two / "rootB" / "out" / "設計書", [chunk("c0001", "B：タイムアウトは90秒。", path="v1/設計書.docx")])
    off = {"enabled": False}
    await run("same-place-in-two-folders", {"roots": [str(two / "rootA" / "out"), str(two / "rootB" / "out")], "embedding": off},
              same_place_in_two_folders)

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

    sharp = BASE / "sharp"
    write(sharp / "C#設計" / "設計書", [chunk("c0001", "前の塊", next_="c0002"), chunk("c0002", "後ろの塊", prev="c0001")])
    await run("hash-in-a-folder", {"roots": [str(sharp)], "embedding": off}, hash_in_a_folder)

    api = Embeddings()
    emb = {"url": api.url, "model": "test-model", "queryPrefix": "", "passagePrefix": "", "retrySeconds": 1}
    comes_back, other_model, other_model_for_the_question = embedding_cases(api)
    await run("embeddings-come-back", {"roots": [str(one)], "embedding": dict(emb)}, comes_back)
    await run("other-model", {"roots": [str(one)], "embedding": dict(emb)}, other_model)
    await run("other-model-for-the-question", {"roots": [str(one)], "embedding": dict(emb)}, other_model_for_the_question)
    api.http.shutdown()

    print(f"\n{count - len(failures)}/{count} passed")
    shutil.rmtree(BASE, ignore_errors=True)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    asyncio.run(main())
