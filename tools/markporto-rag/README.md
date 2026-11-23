# markporto-rag (an MCP server that searches the output of MarkPortO)

English | [日本語](README.ja.md)

A small MCP server that searches the chunks for RAG that MarkPortO writes (`<name>_chunks.jsonl`). Registered in Claude Code, Codex, OpenCode, GitHub Copilot or a similar tool, it lets the model search design documents and the like, and answer **with the source** (the original file, the pages, the headings and the page images).

- No database: every `*_chunks.jsonl` under the folders is read into memory (up to some thousands of chunks). A file that is added or changed is read again at the next search.
- The search mixes keywords (pairs of characters for Japanese, words for English) with a search by meaning through an embedding API (OpenAI compatible: LM Studio, Ollama ...). Without the embedding API it uses the keywords only.
- The documents do not leave the computer (only when the embedding API is a cloud service, the text of the chunks is sent to it).

## Tools

| Tool | What it does |
|---|---|
| `search_docs(query, k=5, source=None)` | The k chunks closest to the question; `source` limits it to the original files whose name contains it |
| `read_chunk(key, neighbors=1)` | A chunk with up to three chunks before and after it |
| `list_documents()` | The documents that are searched |

## Setting up

1. Convert with MarkPortO with the preset For RAG, or with "Write the chunks for RAG (_chunks.jsonl)" on in the details of the steps (`--chunks on` on the command line).
2. Install [uv](https://docs.astral.sh/uv/) (`winget install astral-sh.uv`). The MCP package (`mcp` for Python) is fetched by uv the first time (this needs the internet).
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
| `cacheDir` | `%LOCALAPPDATA%\markporto-rag\cache` | Where the embeddings are kept (computed again only when a jsonl changes) |
| `textChars` / `maxResults` | `1500` / `20` | How much text a search result has / the most results |

## Registering

Use the path of this folder. A tool sees the server after it is opened again.

- **Claude Code** (in every project):
  ```
  claude mcp add --scope user markporto-rag -- uv run --no-project --with mcp python C:\MarkPortO\tools\markporto-rag\server.py
  ```
- **Codex** (`~/.codex/config.toml`):
  ```toml
  [mcp_servers.markporto-rag]
  command = "uv"
  args = ["run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"]
  ```
- **OpenCode** (`opencode.json`):
  ```json
  { "mcp": { "markporto-rag": { "type": "local", "command": ["uv", "run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"], "enabled": true } } }
  ```
- **GitHub Copilot** (`.vscode/mcp.json` in VS Code; the top level is `servers`):
  ```json
  { "servers": { "markporto-rag": { "command": "uv", "args": ["run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"] } } }
  ```

The way to write these can change with the version of a tool; see its documentation when it does not work.

## Example

Ask the tool something like "Find in the design documents how an interrupted job is resumed, with the pages." It searches with `search_docs`, reads the chunks around with `read_chunk` when it needs to, and answers with the pages. The page images in `pageImages` show the pages as they are printed.

## Notes

- A small model that writes the answer may write page numbers it was not given; take the source from `source` and `pages` of the results.
- An old document gives an old answer: convert a design document again after it changes (the server reads it again by itself).
- Several conversions of the same document in the searched folders give the same text more than once; narrow `roots` or remove the old output.
