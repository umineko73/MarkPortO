# MarkPortO

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)

[English](README.md) | **日本語**

Word・Excel・PowerPoint の文書（docx・xlsx・pptx）を、Markdownと、ページごとの印刷イメージ（PNG）に変換するWindows向けのツールです。画面（WPF）とコマンドライン（CLI）があり、どちらも同じ変換処理を使うので、ビルドやCIの中で人手なしに動かすこともできます。

> 開発中です。実機での手動の確認が済んでいない部分があります。

![MarkPortO のメイン画面](docs/images/main-window.ja.png)

*メイン画面（サンプルの3文書（docx・xlsx・pptx）を、VLMを使って変換したあと）。*

## できること

- 文書のテキストをMarkdownにし、全ページの画像（PNG）を保存します。Markdownの各ページの位置に、そのページの画像への参照を入れます。
- Excelは、既定では、シートの全列が1枚の画像に収まるようにします（列が極端に多いシートは、列の区切りで複数の画像に分けます）。「Excelの範囲」で、全行を1ページに収める、シートの印刷範囲設定に従う、も選べます（`--excel-range`）。
- ページのように配置したシート（複数の表が左右や上下に並ぶもの、ラベルの文字が空白のセルにはみ出すもの）は、1つの巨大な表にせず、空の行・列で区切ったブロックごとに、別々のMarkdownの表にします。
- ページ全体に「×」の図形が付いたページは、削除ページとして扱います。画像は保存し、Markdownにはそのページの位置に削除を示す文言だけを書きます。
- フォルダ単位の変換、サブフォルダの走査、種類（docx / xlsx / pptx）の選択ができます。出力先に同名のフォルダがあれば `名前 (2)` のように連番を付け、既存の出力は上書きしません。
- プレビュー: ページの画像、ファイルから取ったテキスト、VLMの結果を並べて見られます。変換の実行中でも見られます。
- 途中で中断でき、次回に続きから再開できます。完了済みのファイルは、やり直しません。
- Step3（任意）: OpenAI互換のVLMサーバー（画像を読めるモデル）で、ページの画像から文字を読み取ってMarkdownに入れます。接続先は複数登録して切り替えられ、使用トークン数をページごと・ファイルごとに記録します。
- 変換エンジンは Microsoft Office（COM）か LibreOffice（画面を出さずに実行）を選べます。既定は Office です。画面の選択肢には、インストールされているエンジンだけが出ます。
- 画面は、日本語と English に対応しています。初めて起動したときは、OSの言語（対応していない言語は English）で表示します。言語は、メニューの「設定」から、再起動せずに切り替えられます。テーマ（OSに合わせる / ライト / ダーク）も、同じメニューから切り替えられます。出力するMarkdownの文言も、画面の言語に合わせます（変換を始めたときの言語で、その変換の間は変わりません）。コマンドラインのメッセージは English 固定です。

状態: 画面の操作（一覧の更新、中断と再開、設定の復元）と、実際のVLMモデルでの読み取りは、手動の確認がまだです。

## 変換の流れ

変換は3つの工程（Step）からなり、工程ごとにスレッドで動いて、ファイルをまたいで重なって進みます。

| 工程 | 内容 |
| ---- | ---- |
| Step1 | 文書のテキストをMarkdownにし、全ページのPNGを保存します。常に実行します。 |
| Step2 | Markdownに画像を差し込みます。 |
| Step3 | 任意。VLMでページの画像から文字を読み取り、Markdownに入れます。 |

## 必要なもの

- Windows 11
- .NET 10 SDK（ビルドするとき）
- Microsoft Office（Word・Excel・PowerPoint）または [LibreOffice](https://www.libreoffice.org/) のどちらか（文書をPDFにするために使います。画面は出しません）。LibreOfficeだけの環境では、`engine.kind` を `libreoffice` にします
- Step3を使うとき: 画像を読めるモデルを載せた、OpenAI互換のサーバー（例: LM Studio）

## クイックスタート

```powershell
git clone https://github.com/umineko73/MarkPortO.git
cd MarkPortO
dotnet build MarkPortO.slnx

# コマンドライン
dotnet run --project src/MarkPortO.Cli -- convert D:\docs\報告書.docx --out D:\out

# 画面
dotnet run --project src/MarkPortO.App
```

画面では、**プレビュー**: 一覧のファイルをダブルクリック（右クリックのメニューや「プレビュー」ボタンでも。ボタンは、選んだファイルにページがあるときだけ押せます）すると、そのファイルのページを見られます。左のページの一覧には、VLMの読み取りの状態（✓ 完了、! 失敗、▶ 実行中、… 待機）が出ます。右には、「ページ画像」「抽出テキスト」（Step1がファイルから取ったテキスト）「VLMの結果」のうち見たいものを選んで、「横並び」か「縦並び」で並べます。Markdownは「プレビュー」（描画）か「ソース」（文字のまま）で見られます。変換の実行中は、「追従」で、最後に読み終えたページを表示します。スクロールバーが出ている領域は、マウスの右ボタンのドラッグでもスクロールできます。

![プレビュー画面](docs/images/preview.ja.png)

ファイルまたはフォルダを追加し（一覧へのドロップでも追加できます）、出力先を選んで（出力先の欄へフォルダをドロップしても設定できます）「開始」を押します。

## 使い方

### コマンドライン

```powershell
dotnet run --project src/MarkPortO.Cli -- convert D:\docs\報告書.docx --out D:\out
dotnet run --project src/MarkPortO.Cli -- convert D:\docs --out D:\out --types docx,xlsx --recurse
dotnet run --project src/MarkPortO.Cli -- convert --resume --out D:\out      # 中断した変換を再開する
dotnet run --project src/MarkPortO.Cli -- list-jobs --out D:\out             # 出力先にある変換の一覧
dotnet run --project src/MarkPortO.Cli -- convert D:\docs --out D:\out --step3 on --settings settings.json   # VLMで読み取る
dotnet run --project src/MarkPortO.Cli -- check-connection --settings settings.json    # VLMサーバーへの接続を確かめる
dotnet run --project src/MarkPortO.Cli -- list-connections --settings settings.json   # 登録した接続の一覧（キーは表示しない）
```

主なオプション: `--settings <path>`（`settings.json`）、`--out`、`--types`、`--recurse`、`--include-hidden`、`--engine`、`--dpi`、`--excel-range columns|rows|print-area`、`--excel-range columns|rows|print-area`、`--step2 on|off`、`--step3 on|off`、`--vlm-separate on|off`、`--result <file>`（結果をJSONで保存）、`--resume`、`--job`、`--retry-failed`、`--quiet`、`--max-pages`、`--max-cost`、`--parallel`。コマンドラインの指定は `settings.json` より優先されます。

アクセスキーは、`settings.json` に環境変数の**名前**（`apiKeyEnv`）として書くのが基本です（値そのものは保存しません）。画面で入力したキーは、現在のWindowsユーザーだけが復号できる形（DPAPI）で保存します。ログ・Markdown・状態ファイルにキーは出ません。

終了コード: `0` すべて成功、`1` 一部が失敗、`2` 引数または設定が不正、`3` 開始前のチェックに失敗（同意がない場合を含む）、`4` 使用量の上限・ログイン・ページ数や費用の上限で停止（`--resume` で再開できます）、`130` 中断（`Ctrl+C`。`--resume` で再開できます）。

### 画面

```powershell
dotnet run --project src/MarkPortO.App
```

メニューバーの「設定」に、言語（OSに合わせる / 日本語 / English）、VLM接続の設定、プロンプトの設定があります。「ヘルプ」に、バージョン情報とライセンス表記があります。

**VLM接続の設定**（設定 → VLM接続の設定）: OpenAI互換のサーバー、または外部コマンド（`claude`、`codex`、`opencode`）の接続を追加し、モデル、上限、外部への送信の同意を設定して、接続と画像解析を確かめます。

![VLM接続の設定](docs/images/connections.ja.png)

**プロンプトの設定**（設定 → プロンプトの設定）: VLMに送る指示を、文書の種類（docx、xlsx、pptx）ごとに設定します。

![プロンプトの設定](docs/images/prompts.ja.png)

ファイルまたはフォルダを追加し（一覧へのドロップでも追加できます。対応していない種類のファイルは無視します）、出力先を選んで（出力先の欄へフォルダをドロップしても設定できます）「開始」を押します。「条件」には、対象の種類、範囲、変換（エンジンとDPI）、Step1〜Step3の行があります（Step1は常に実行します）。「Excelの範囲」（xlsx）は、「すべての列を1ページに含める」（既定）、「すべての行を1ページに収める」、「印刷範囲設定に従う」（シートごとの印刷範囲とページ設定）から選びます。一覧に、ファイルごとの状態（待機中、Step1〜3の実行中、完了、完了（一部失敗）、失敗、中断）が出ます。フォルダを指定したファイルは、指定したフォルダからの相対パスで表示します。VLMが読み取れなかったページがあるファイルは、「失敗のみ再試行」で、そのページだけを読み直せます。画面で設定した値は、すぐに `settings.json`（`%APPDATA%\MarkPortO\settings.json`）に保存され、次回の起動で元に戻ります。

変換の実行中は、条件と入力を変えられません。出力先に再開できる前回の変換があるときは、再開で使わない項目（種類・範囲・エンジン・DPI）も変えられません。「前回の変換をクリア」で前回を破棄すると、変えられるようになります。

### `settings.json` の例

```json
{
  "schemaVersion": 1,
  "input": { "paths": ["D:\\docs"], "recurse": true, "types": ["docx", "xlsx", "pptx"], "includeHidden": false },
  "output": { "folder": "D:\\out" },
  "ui": { "language": "system", "theme": "system" },
  "engine": { "kind": "office" },
  "images": { "dpi": 150 },
  "steps": { "step2": true, "step3": true },
  "vlm": {
    "selected": "local",
    "connections": [
      { "name": "local", "baseUrl": "http://localhost:1234/v1", "model": "vision-model", "apiKeyEnv": "MARKPORTO_API_KEY", "timeoutSeconds": 120 }
    ]
  }
}
```

設定ファイルの場所は、環境変数 `MARKPORTO_SETTINGS` で変えられます（画面もコマンドラインも、`--settings` を指定しないときに使います）。言語とテーマは `ui.language`（`system` / `ja` / `en`）と `ui.theme`（`system` / `light` / `dark`）です。

### Markdownについて

- Markdownの表はセルを結合できないため、結合セルは notePM 方式の目印で表します。`<` は左のセルと、`^` は上のセルと結合しています。見出し行は結合できないため、値を繰り返します。値のない通常のセルは空のままです。見出し行のない表には空の見出し行を付け、1行目をデータ行のままにします。Excelの「テーブル」を使っていないシートは、空の行・列で区切ったブロックごとに、別々の表にします。
- 「結果を別ファイルに書く」（`--vlm-separate on`）にすると、VLMの結果は文書のMarkdownに入れず、同じフォルダの `名前_vlm.md` に書きます（ページの区切り、使用量、失敗・削除ページの印つき）。文書のMarkdownには、VLMの文字は入りません。
- 指示（プロンプト）に `{pagetext}` を書くと、Step1が文書から取り出したそのページのテキスト（20,000文字まで）に置き換わり、画像と一緒に送られます。`{pagetext}` がなければ、テキストは送りません。ページ画像がネットワークの外に出るときは、このテキストも出ます。
- 文書の構造は、ファイルの中のXMLから読みます（Officeはページ画像を作るときだけ使います）。Excelの数値はセルに見える形（`25.6%`、`¥1,234,567`）で書き、非表示の行・列は出さず、シートの表は1つずつ分けて書き、リンク・コメント・グラフ（ファイルに保存された値）も書きます。Wordの脚注・リンク・画像の代替テキスト・入れ子の表、PowerPointの発表者ノート・リンク・代替テキスト・グラフ・SmartArtの文字も書きます。
- VLMが答えを ```` ```markdown ```` などのコードブロックで囲んで返したときは、その囲みを取り除きます（`mermaid` の囲みは残します）。

### Claude Code、Codex、OpenCode で読み取る（外部コマンド）

Step3は、ログイン済みのコマンド（`claude`、`codex`、`opencode`）でも読み取れます。ページ画像ごとに、コマンドを1回、シェルを介さず、空の一時フォルダで実行します。**お使いの設定・フック・指示ファイル・メモリは読み込ませず、道具も使わせません**（ファイルの書き込みやコマンドの実行はできません）。画像を渡し、文字を受け取ります。

```json
{ "name": "cloud", "kind": "command", "command": "claude", "model": "haiku", "effort": "low", "parallel": 2,
  "maxPages": 200, "maxCostUsd": 3.0 }
```

- `model` はコマンドに渡します。新しい接続の初期値は、`claude` が `haiku`、`codex` が `gpt-6-luna`、`opencode` が空（お使いのOpenCodeの既定のモデル）です。画像を読めるモデルにしてください。
- `effort` は `low`（既定）、`medium`、`high`、または空（コマンドの既定）です。`parallel` は、同時に読むページ数です（既定は1）。
- `maxPages` と `maxCostUsd` は、上限に達すると変換を止めます（空は無制限）。費用の上限は、費用を返すコマンド（`claude`）だけで働きます。止まった変換は、「再開」または `--resume` で続けられます。
- コマンドが使用量の上限やログインが必要なことを返したとき、同じ失敗が3ページ続いたときも、変換を止め、再開できます。

**ページ画像は、コマンドの提供元のサービス（ネットワークの外）に送られ、お使いのアカウントに利用料が発生することがあります。** コマンドは、これに加えて、環境の情報（例: アカウントのメールアドレス）を提供元に送ることがあります。機密の文書には使わないでください。このため、この種類の接続（と、`api.openai.com` のようにローカルネットワークの外にあるOpenAI互換の接続）は、同意したあとにだけ使えます。

```powershell
dotnet run --project src/MarkPortO.Cli -- consent cloud --settings settings.json            # 注意を表示し、同意を記録する
dotnet run --project src/MarkPortO.Cli -- consent cloud --revoke --settings settings.json   # 取り消す
```

画面では、接続の設定で「同意する…」を押します。変換を始める前にも、確認が出ます。同意がなければ、何も起動せず、何も送りません。`--max-pages`、`--max-cost`、`--parallel` で、その実行だけ、接続の設定を上書きできます。

## 出力

文書ごとに、拡張子を除いた名前のフォルダができます。

```text
出力先/報告書/
├── 報告書.md          # 先頭に、元のファイル名とMarkdownの構造の説明があります
└── images/
    ├── page-001.png
    └── page-002.png
```

## 開発

```powershell
dotnet build MarkPortO.slnx
dotnet test MarkPortO.slnx                            # 使えるエンジンが無い環境では、そのエンジンを使うテストはスキップされます
dotnet test MarkPortO.slnx --filter "Engine!=Office"   # Officeが無い環境（LibreOfficeのみ）
```

`scripts/build.ps1` は、同じことを1回で行い、配布用のプログラムも作れます。

```powershell
./scripts/build.ps1                          # 復元、ビルド、テスト（Release）
./scripts/build.ps1 -Task Build              # ビルドだけ
./scripts/build.ps1 -SkipOffice              # Microsoft Office が無いコンピューター
./scripts/build.ps1 -Task Publish            # ビルド、テスト、artifacts/MarkPortO-<バージョン>-win-x64.zip の作成（画面、`cli/` のコマンドライン、ライセンスの文書）
```

.NET SDK は `global.json` で固定しています（10.0.100、または新しい機能バンド）。GitHub Actions 用のワークフローは `.github/workflows/ci.yml` です（Microsoft Office が無い Windows で、ビルドとテストを行います）。

```text
src/MarkPortO.Core/   変換処理のすべて（画面に依存しない）
src/MarkPortO.Cli/    コマンドライン
src/MarkPortO.App/    画面（WPF）
tests/               テスト（検証用の文書は、テストコードが生成します）
scripts/             ビルドのスクリプト（復元、ビルド、テスト、配布用の作成）
tools/               補助のスクリプト（アイコン、ライセンスヘッダー）
.github/workflows/   CIのワークフロー
```

- `src/MarkPortO.App/Assets/MarkPortO.ico` は `python tools/make-icon.py`（Pillow が必要）で作り直せます。
- すべてのソースファイルの先頭に、同じライセンスヘッダーを付けています。新しいファイルを足したら、`python tools/add-license-header.py` でヘッダーを付けてください（付け忘れは試験で見つかります）。

## ライセンス

MarkPortO は [GNU General Public License バージョン3（GPL-3.0-only）](LICENSE) のもとで公開します。「それ以降のバージョン」は選べません（`-only`）。Copyright (C) 2026 umineko73。

依存パッケージのライセンスは [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) にあります。
