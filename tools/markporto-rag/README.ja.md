# markporto-rag（MarkPortO の出力を検索する MCP サーバー）

[English](README.md) | 日本語

MarkPortO が書く RAG 用の小分け（`<名前>_chunks.jsonl`）を検索する、小さな MCP サーバーです。Claude Code・Codex・OpenCode・GitHub Copilot などの道具に登録すると、そのモデルが設計書などを探し、**出どころ（元のファイル・ページ・見出し・ページ画像）つき**で答えられます。

- データベースは使いません。フォルダの下の `*_chunks.jsonl` をすべてメモリに読みます（数千の塊まで）。ファイルが増えたり変わったりすると、次の検索で読み直します。
- 検索は、キーワード（日本語は2文字ずつ、英語は単語）と、埋め込み（LM Studio・Ollama など OpenAI 互換の API）による意味の検索を混ぜます。埋め込みにつながらないときは、キーワードだけで動きます。
- 文書は PC の外へ送りません（埋め込みの API をクラウドにしたときだけ、塊の文字をその API へ送ります）。

## 道具

| 道具 | すること |
|---|---|
| `search_docs(query, k=5, source=None)` | 質問に近い塊を k 件。`source` で元のファイル名を絞る |
| `read_chunk(key, neighbors=1)` | 塊を前後の塊（最大3つずつ）と一緒に読む |
| `list_documents()` | 検索の対象の文書の一覧と、使っている埋め込み（設定のモデル、実際のベクトルの次元）。次元が違えば、別のモデルが答えている |

## 準備

1. MarkPortO で、プリセット「RAG用」、または「ステップの詳細」の「RAG 用の小分け（_chunks.jsonl）を書く」をオンにして変換する（CLI は `--chunks on`）。
2. [uv](https://docs.astral.sh/uv/) を入れる（`winget install astral-sh.uv`）。MCP の部品（Python の `mcp`）は、初回の起動で uv が取ってきます（インターネットが要ります）。
3. 設定ファイルを作る: `markporto-rag.sample.json` を `%APPDATA%\MarkPortO\markporto-rag.json` に写して、`roots` に MarkPortO の出力先を書く。
4. 意味の検索も使うなら、LM Studio で埋め込みのモデル（例: `multilingual-e5-small` の GGUF）を入れて、サーバーを起動しておく。

## 設定（JSON）

設定ファイルは、`MARKPORTO_RAG_CONFIG` が指すファイル、なければ `%APPDATA%\MarkPortO\markporto-rag.json`、なければこのフォルダの `markporto-rag.json` を使います。環境変数 `MARKPORTO_RAG_ROOT`（`;` で区切る）は `roots` の代わりになります。

| 項目 | 既定 | 意味 |
|---|---|---|
| `roots` | （なし） | 検索するフォルダ。下の `*_chunks.jsonl` をすべて読む。`%USERPROFILE%` などの環境変数も使える |
| `embedding.enabled` | `true` | `false` でキーワードだけ |
| `embedding.url` | `http://localhost:1234/v1` | OpenAI 互換の埋め込みの API（LM Studio。Ollama は `http://localhost:11434/v1`） |
| `embedding.model` | `text-embedding-multilingual-e5-small` | 埋め込みのモデル |
| `embedding.apiKeyEnv` | `""` | 鍵が要る API のとき、**鍵を入れた環境変数の名前**（鍵そのものは JSON に書かない） |
| `embedding.queryPrefix` / `passagePrefix` | `"query: "` / `"passage: "` | e5 系のモデルの決まり。**ほかのモデルでは `""` にする** |
| `embedding.passageChars` | `1500` | 塊の何文字までを埋め込みにするか |
| `cacheDir` | `%LOCALAPPDATA%\markporto-rag\cache` | 埋め込みを残す場所（jsonl が変わったときだけ計算し直す） |
| `textChars` / `maxResults` | `1500` / `20` | 検索の結果に入れる本文の長さ / 返す件数の上限 |

## 登録

パスはこのフォルダに合わせてください。どれも、登録したあと CLI を開き直すと使えます。

- **Claude Code**（すべてのプロジェクトで使う）:
  ```
  claude mcp add --scope user markporto-rag -- uv run --no-project --with mcp python C:\MarkPortO\tools\markporto-rag\server.py
  ```
- **Codex**（`~/.codex/config.toml`）:
  ```toml
  [mcp_servers.markporto-rag]
  command = "uv"
  args = ["run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"]
  ```
- **OpenCode**（`opencode.json`）:
  ```json
  { "mcp": { "markporto-rag": { "type": "local", "command": ["uv", "run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"], "enabled": true } } }
  ```
- **GitHub Copilot**（VS Code の `.vscode/mcp.json`。最上位は `servers`）:
  ```json
  { "servers": { "markporto-rag": { "command": "uv", "args": ["run", "--no-project", "--with", "mcp", "python", "C:\\MarkPortO\\tools\\markporto-rag\\server.py"] } } }
  ```

書き方は CLI の版で変わることがあります。うまくいかないときは、それぞれの説明書を確かめてください。

## 使い方の例

道具に「設計書から、中断したジョブの再開の仕方を調べて。ページも示して」のように頼みます。モデルは `search_docs` で探し、必要なら `read_chunk` で前後を読み、出どころのページを付けて答えます。答えのページは、`pageImages` のページ画像で確かめられます。

## 注意

- 小さいモデルに答えさせると、渡されていないページ番号を書くことがあります。出どころは、検索の結果の `source`・`pages` を使ってください。
- 文書が古いと、答えも古くなります。設計書を直したら、MarkPortO で変換し直してください（サーバーは自動で読み直します）。
- 同じ文書を何度も変換した出力が同じフォルダにあると、同じ内容が重なって見つかります。`roots` を絞るか、古い出力を消してください。
