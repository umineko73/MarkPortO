# markporto-rag（MarkPortO の出力を検索する MCP サーバー）

[English](README.md) | 日本語

MarkPortO が書く RAG 用の小分け（`<名前>_chunks.jsonl`）を検索する、小さな MCP サーバーです。Claude Code などの道具に登録すると、そのモデルが設計書などを探し、**出どころ（元のファイル・ページ・見出し・ページ画像）つき**で答えられます。

- データベースは使いません。フォルダの下の `*_chunks.jsonl` をすべてメモリに読みます（数千の塊まで）。ファイルが増えたり変わったりすると、次の検索で読み直します。
- 検索は、キーワード（日本語は2文字ずつ、英語は単語）と、埋め込み（LM Studio・Ollama など OpenAI 互換の API）による意味の検索を混ぜます。埋め込みにつながらないときは、キーワードだけで動きます。
- 文書は PC の外へ送りません（埋め込みの API をクラウドにしたときだけ、塊の文字をその API へ送ります）。

## 道具

| 道具 | すること |
|---|---|
| `search_docs(query, k=5, source=None, path=None)` | 質問に近い塊を k 件。`source` で元のファイル名を、`path` で変換したフォルダからのパス（例: 版のフォルダ `"v1.2"`。フォルダ名が丸ごと合うものを先に探すので、`v1.20` は混ざらない）を絞る |
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
| `embedding.timeoutSeconds` / `retrySeconds` | `120` / `30` | API を待つ秒数 / API につながらなかったとき、次の検索で試し直すまでの秒数（その間はキーワードだけ） |
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

### 頼み方

「どこを（設計書・版）」「何を」「出どころの示し方」を入れて頼みます。モデルは `search_docs` で探し、必要なら `read_chunk` で前後を読み、出どころのページを付けて答えます。答えのページは、`pageImages` のページ画像で確かめられます。

```
設計書（v1.2）から、接続のタイムアウトの仕様を調べて。
答えには、元のファイルとページを必ず付けて。見つからなければ推測しないで「見つからない」と言って。
```

| 頼みたいこと | 頼み方の例 | うまく使うコツ |
|---|---|---|
| 仕様を調べる | 「v1.2 の設計書から、ジョブの再開の条件を調べて。ページも示して」 | 版を言うと、モデルが `path` で絞ります |
| 版の違いを調べる | 「タイムアウトの値が v1.1 と v1.2 でどう違うか、それぞれの設計書から探して比べて」 | 版ごとに検索させます。文書全体の違いは、Markdown を WinMerge などで比べるほうが確実です |
| 根拠まで確かめる | 「見つかった節の前後も読んで、条件に例外がないか確かめて」 | `read_chunk` で前後の塊を読ませます |
| 漏れなく探す | 「『タイムアウト』『待ち時間』『timeout』など言い方を変えて、何回か検索して」 | 言い回しの違いに強くなります |
| 影響を調べる | 「エラーコード E102 を使っている箇所を、設計書とプロトコル仕様書の両方から全部挙げて」 | 「全部」「両方から」と言うと、件数や文書を変えて探します |
| 原文で確かめる | 「その表のページ画像のパスを教えて」 | 表や図は、ページ画像で原文を確かめます |

### 指示ファイルに書いておく決まり

毎回言わなくて済むように、`CLAUDE.md` や `AGENTS.md` などの指示ファイルに書いておきます。版のフォルダ名と最新版は、自分の文書に合わせて直してください。

```markdown
## 設計書の検索（markporto-rag）
- 仕様・設計についての質問は、まず markporto-rag の search_docs で設計書を探してから答える。自分の知識だけで答えない。
- 文書は版ごとのフォルダ（例: v1.1, v1.2）にある。版の指定がなければ最新版（v1.2）を path で絞って探し、どの版の答えかを書く。
- 答えには必ず出どころを書く: path とページ（例: v1.2/基本設計/設計書.docx p.13）。ページ番号は検索の結果の pages だけを使い、作らない。
- 節が途中で切れていそうなときは read_chunk で前後も読む。
- 言い回しを変えて2〜3回検索しても見つからなければ、「設計書には見つからない」と答える。
- 表や図の数値は、必要なら pageImages のページ画像で確かめる。
```

- 「推測しない」「見つからなければそう言う」は必ず入れてください。決まりがないと、モデルは答えのない質問にも自分の知識でそれらしい答えを作ることがあります。
- ページは検索の結果の `pages` だけを使わせてください。モデルが、ないページ番号を書くのを防ぎます。
- 最新版がどれかを書いておきます。フォルダ名だけでは、モデルはどれが最新か分かりません。版が上がったら、この1行を直します。

## 注意

- 小さいモデルに答えさせると、渡されていないページ番号を書くことがあります。出どころは、検索の結果の `source`・`pages` を使ってください。
- 文書が古いと、答えも古くなります。設計書を直したら、MarkPortO で変換し直してください（サーバーは自動で読み直します）。
- 同じ文書を何度も変換した出力が同じフォルダにあると、同じ内容が重なって見つかります。`roots` を絞るか、古い出力を消してください。
- 版ごとにフォルダを作って管理している文書は、`roots` を最新版の出力だけにするか、`path` で版を絞ってください（版が混ざると、古い版で答えることがあります）。
