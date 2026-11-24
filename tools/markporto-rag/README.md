# markporto-rag (an MCP server that searches the output of MarkPortO)

English | [日本語](README.ja.md)

A small MCP server that searches the chunks for RAG that MarkPortO writes (`<name>_chunks.jsonl`). Registered in a tool such as Claude Code, it lets the model search design documents and the like, and answer **with the source** (the original file, the pages, the headings and the page images).

- No database: every `*_chunks.jsonl` under the folders is read into memory (with 20,000 chunks, a search by meaning takes a few milliseconds). A file that is added or changed is read again, alone, at the next search.
- The embeddings are made in the background. Meanwhile a search answers by keywords, and its `note` and the `pendingChunks` of `list_documents` say how far it is; with a few, a search waits up to `waitSeconds` and uses the search by meaning too. The embeddings are kept, so only changed documents are embedded again.
- The search mixes keywords (pairs of characters for Japanese, words for English) with a search by meaning through an embedding API (OpenAI compatible: LM Studio, Ollama ...). Without the embedding API it uses the keywords only.
- The documents do not leave the computer (only when the embedding API is a cloud service, the text of the chunks is sent to it).

## Tools

| Tool | What it does |
|---|---|
| `search_docs(query, k=5, source=None, path=None)` | The k chunks closest to the question; `source` limits it to the original files whose name contains it, `path` to the documents whose path in the converted folder contains it (for example the folder of a version, `"v1.2"`; whole folder names are matched first, so `v1.20` is not mixed in; `"v1.2/"` is that folder only) |
| `read_chunk(key, neighbors=1)` | A chunk with up to three chunks before and after it |
| `list_documents()` | The documents that are searched, and the embedding in use (the model of the configuration, the dimension of the vectors); another dimension means another model answered |

## Setting up

1. Convert with MarkPortO with the preset For RAG, or with "Write the chunks for RAG (_chunks.jsonl)" on in the details of the steps (`--chunks on` on the command line).
2. Install [uv](https://docs.astral.sh/uv/) (`winget install astral-sh.uv`). The MCP package (`mcp` for Python) and `numpy` are fetched by uv the first time (this needs the internet; behind a proxy, see "Behind a proxy, or without the internet" below). Without `numpy` the server still works, but the search by meaning is slower (about 0.3 seconds a search with 20,000 chunks).
3. Make the configuration: copy `markporto-rag.sample.json` to `%APPDATA%\MarkPortO\markporto-rag.json` and write the output folder of MarkPortO in `roots`.
4. For the search by meaning, load an embedding model in LM Studio (for example a GGUF of `multilingual-e5-small`) and start its server.

## Configuration (JSON)

The file named by `MARKPORTO_RAG_CONFIG`, else `%APPDATA%\MarkPortO\markporto-rag.json`, else `markporto-rag.json` in this folder. The environment variable `MARKPORTO_RAG_ROOT` (folders separated by `;`) replaces `roots`.

| Item | Default | Meaning |
|---|---|---|
| `roots` | (none) | The folders to search; every `*_chunks.jsonl` under them is read. Environment variables such as `%USERPROFILE%` can be used |
| `embedding.enabled` | `true` | `false`: keywords only |
| `embedding.url` | `http://localhost:1234/v1` | The OpenAI compatible embedding API (LM Studio; Ollama is `http://localhost:11434/v1`) |
| `embedding.model` | `text-embedding-multilingual-e5-small` | The embedding model |
| `embedding.apiKeyEnv` | `""` | For an API that needs a key: **the name of the environment variable that has the key** (the key is not written in the JSON) |
| `embedding.queryPrefix` / `passagePrefix` | `"query: "` / `"passage: "` | What e5 models expect; **`""` for other models** |
| `embedding.passageChars` | `1500` | How much of a chunk is embedded |
| `embedding.timeoutSeconds` / `retrySeconds` | `120` / `30` | Seconds to wait for the API / when the API could not be reached, seconds before a later search tries it again (keywords only until then) |
| `embedding.batchSize` | `64` | Chunks sent to the embedding API in one request (small documents are sent together). LM Studio takes long for each request: 64 was about 3 times as fast as 16. Lower it if a computer without a GPU runs out of time |
| `embedding.waitSeconds` | `10` | Seconds a search waits for the embedding under way to finish (after that it answers by keywords) |
| `cacheDir` | `%LOCALAPPDATA%\markporto-rag\cache` | Where the embeddings are kept (computed again only when a jsonl changes) |
| `textChars` / `maxResults` | `1500` / `20` | How much text a search result has / the most results |

## Registering

Use the path of this folder. A tool sees the server after it is opened again.

- **Claude Code** (in every project):
  ```
  claude mcp add --scope user markporto-rag -- uv run --no-project --with mcp --with numpy python C:\MarkPortO\tools\markporto-rag\server.py
  ```
- **Codex** (`~/.codex/config.toml`):
  ```toml
  [mcp_servers.markporto-rag]
  command = "uv"
  args = ["run", "--no-project", "--with", "mcp", "--with", "numpy", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"]
  ```
- **OpenCode** (`opencode.json`):
  ```json
  { "mcp": { "markporto-rag": { "type": "local", "command": ["uv", "run", "--no-project", "--with", "mcp", "--with", "numpy", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"], "enabled": true } } }
  ```
- **GitHub Copilot** (`.vscode/mcp.json` in VS Code; the top level is `servers`):
  ```json
  { "servers": { "markporto-rag": { "command": "uv", "args": ["run", "--no-project", "--with", "mcp", "--with", "numpy", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"] } } }
  ```

The way to write these can change with the version of a tool; see its documentation when it does not work.

### Behind a proxy, or without the internet

The first time, uv fetches the packages (`mcp`, `numpy`; numpy is about 15 MB) from PyPI into its cache. Later starts use the cache, but uv still asks PyPI for the versions at every start.

1. **Fetch the packages once through the proxy** (PowerShell; use the address of your proxy):

   ```powershell
   $env:HTTPS_PROXY = "http://proxy.example.com:8080"
   $env:HTTP_PROXY  = $env:HTTPS_PROXY
   uv run --no-project --with mcp --with numpy python -c "import mcp, numpy; print('ok')"
   ```

   An error about a `certificate` (`invalid peer certificate`) means that the proxy inspects the connections: add `$env:UV_SYSTEM_CERTS = "true"` so that uv uses the certificates installed in Windows, and run it again. If the proxy needs a user name and a password, do not write the password in a configuration or in the command that registers the server; follow the instructions of your organization.

2. **Add `--offline` to the command that registers the server**, after `--no-project` (for example `"run", "--no-project", "--offline", "--with", "mcp", "--with", "numpy", ...`): it starts from the cache without asking PyPI. Keep `--no-project` (without it, uv may use the Python project of the folder the tool was opened in).

   Instead of `--offline`, you can make `HTTPS_PROXY` (and `UV_SYSTEM_CERTS` if needed) environment variables of your Windows user (Settings > System > About > Advanced system settings > Environment Variables); the tools use them after they are opened again.

- If the packages cannot be fetched, the server works without `--with numpy` (the search by meaning is slower: about 0.3 seconds a search with 20,000 chunks). `mcp` is needed.
- Older versions of uv name the certificate option differently (`UV_NATIVE_TLS`): if `uv --version` is older than 0.11, update it with `uv self update`, or use that name.

## Examples

### Asking

Say where to look (the documents, the version), what to find, and how to give the source. The model searches with `search_docs`, reads the chunks around with `read_chunk` when it needs to, and answers with the pages; the page images in `pageImages` show the pages as they are printed.

```
In the design documents of v1.2, find the specification of the connection timeout.
Give the file and the page with every answer. If it is not found, say so; do not guess.
```

| To | Ask, for example | Why it works |
|---|---|---|
| Look up a specification | "In the design documents of v1.2, find when a job is resumed, with the pages." | Naming the version makes the model narrow the search with `path` |
| Compare versions | "How does the timeout differ between v1.1 and v1.2? Search the design documents of each and compare." | One search for each version. To see every change of a document, compare its Markdown with WinMerge or a similar tool |
| Check the grounds | "Read the sections before and after it too, and check that the condition has no exception." | `read_chunk` reads the chunks around |
| Find all | "Search a few times with other words too: timeout, wait, time limit." | Other wordings find what one wording misses |
| Find what is affected | "List every place that uses the error code E102, in the design documents and in the protocol specification." | "Every" and "in both" make it search more and in each document |
| See the original | "Give me the path of the page image of that table." | A table or a figure can be checked on its page image |

### Rules in the instructions of the tool

So that you do not have to say it each time, write it in the instructions of the tool, such as `CLAUDE.md` or `AGENTS.md`. Change the folders of the versions and the latest version to those of your documents.

```markdown
## Searching the design documents (markporto-rag)
- For a question about a specification or the design, search the design documents with search_docs of markporto-rag before answering. Do not answer from your own knowledge only.
- The documents are in a folder for each version (for example v1.1, v1.2). Unless a version is asked for, search the latest one (v1.2) with path, and say which version the answer comes from.
- Give the source of every answer: the path and the page (for example v1.2/basic design/design.docx p.13). Take page numbers only from pages of the search results; never make one up.
- When a section seems cut, read the chunks before and after it with read_chunk.
- If two or three searches with other words find nothing, answer that the design documents do not say it.
- Check the numbers of a table or a figure on its page image (pageImages) when it matters.
```

- Always keep "do not guess" and "say that it is not found": without them, a model can make up a likely answer from its own knowledge.
- Let it take page numbers only from `pages` of the results, so that it does not write a page that does not exist.
- Say which version is the latest: a model cannot tell it from the names of the folders. Change that line when a new version comes.

## Notes

- A small model that writes the answer may write page numbers it was not given; take the source from `source` and `pages` of the results.
- An old document gives an old answer: convert a design document again after it changes (the server reads it again by itself).
- Several conversions of the same document in the searched folders give the same text more than once; narrow `roots` or remove the old output.
- For documents kept in a folder for each version, search only the output of the latest version (`roots`), or narrow the search to a version with `path` (mixed versions can give an answer from an old one).
