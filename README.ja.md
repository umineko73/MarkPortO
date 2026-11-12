# MarkPortO

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Version](https://img.shields.io/badge/version-0.3.0-2ea44f.svg)

[English](README.md) | **日本語**

Word・Excel・PowerPoint・PDF の文書（docx・xlsx・pptx・pdf）を、Markdownと、ページごとの画像（PNG）に変換するWindows向けのツールです。画面（WPF）とコマンドライン（CLI）があり、どちらも同じ変換処理を使うので、ビルドやCIの中で人手なしに動かすこともできます。

> 開発中です。Microsoft Office とサンプルの文書で、実機での確認をしています。LibreOffice と大きな文書は、手動の確認が済んでいません。

![MarkPortO のメイン画面](docs/images/main-window.ja.png)

## できること

- **Markdownとページ画像**: 文書のテキストをMarkdownにし、全ページをPNGで保存して、Markdownから参照します。構造はファイルの中から読みます（結合セル、脚注、コメント、リンク、グラフ、SmartArt、発表者ノート、見出しの番号）。
- **ページをまたぐ表と段落**: 複数のページにまたがる表や段落は、Markdownでは1つの表・段落にし、後のページに注記を入れます。ページのように配置したシートは、ブロックごとの表にします（空の行・列で区切ります。罫線のあるセルは、値がなくても表の一部として数えるので、更新中で値のない列があっても、表は割れません）。方眼紙のシート（狭い列を結合して広いセルにしたもの）は、セルが始まる列と行だけで書き、結合セルの続きにすぎない列と行は省きます。数値は、Excelが持つ15桁で書きます（`1.1068500000000001` ではなく `1.10685`）。
- **太字と斜体**: 太字を `**`、斜体を `*` で囲んで書きます（docx、pptx、xlsx）。段落やセルの全体が太字のとき、見出しでは付けません。オフにもできます（「太字・斜体」、`--no-emphasis`）。
- **文書の画像**: Word・PowerPoint・Excel に貼られた画像を `pictures/` に保存し、Markdown の画像の位置から参照できます（`![代替テキスト](pictures/page-003-1.png)`）。名前は画像のあるページと、そのページの中の順番です（`page-003-1.png` は3ページ目の1つ目）。同じ画像は1つのファイルにします。PNG・JPEG・GIF はそのまま、EMF・WMF・BMP・TIFF は PNG にして保存します（読めない画像は省き、文書のログに出ます）。Excel では、画像を固定したセルより上の表や文の後ろに置きます。既定はオフです（「文書の画像を取り出す」、`--pictures on`）。
- **図形**: PowerPoint・Excel の図形（グループは全体で1枚、縦横とも0.5インチ以上の図形。テキストボックスは除く）を、ページ画像から切り出して `pictures/` に保存できます。名前は画像と同じ形です。図形の中の文字は、これまでどおり Markdown に入ります。Excel では、列の幅と行の高さから位置を計算するので、少しずれることがあります。シートの印刷設定に従うとき（`--excel-range print-area`）と、Word は対象外です。既定はオフで、画像とは別の設定です（「図形を画像にする」、`--shapes on`）。
- **図表番号（Word）**: エンジンがWordのとき、Wordでフィールドを更新したコピーから読むので、図表の自動番号がWordで見える番号になります。
- **Excel**: シートの全列を1枚の画像にします（列が極端に多くても、DPIを上げて）。シート全体を1ページに収める、シートの印刷範囲に従う、も選べます（「シートの範囲」、`--excel-range`）。
- **PDF**: 段組のページは、段ごとに読みます。ページごとに画像にして、VLM（Step3）で読み取ります。PDFに文字の層があれば、補助として使います（ページのテキストにし、画像と一緒に送ります）。文字化けするPDFでは、「抽出テキストを補助に使う」（`--pdf-text off`）を外します。OfficeもLibreOfficeも要りません。**PDFには、外部ツールをおすすめします**（例: Docling。[外部コマンドにMarkdownを書かせる](#外部コマンドにmarkdownを書かせる)を参照）。内蔵の抽出は文字の層だけを読み、PDFの表と見出しは作りません。外部ツールは表と見出しを書き、VLM（Step3）はページ画像も読みます。
- **VLMの表と、抽出テキストの表**: ページの文字（Step1）に表があるページでは、VLMの結果に入れる表を選べます。VLMが読んだ表（「表: VLMの結果を採用」）、抽出テキストの表（「抽出テキストを採用」。VLMが表を崩すときに、正確です）、VLMの結果には残さない（`--step1-tables off|replace|omit`）。
- **繰り返しの表**: 小さいVLMは、最後の表のあとも止まらず、同じ表をもう一度書くことがあります。「繰り返しの表を除く」（`--drop-repeated-tables on|off`。既定は書く）をオンにすると、同じページで前に書いた表と同じ内容の表を、Markdownに書きません。VLMの結果そのものは変えません。
- **PowerPoint**: スライドの外に置いたシェイプ（メモ）の文字を、`> **Off-slide**: …` の引用で書きます。書かないこともできます。
- **削除ページと空白ページ**: ページ全体に「×」の図形が付いたページは削除ページ、何も描かれていないページは空白ページとして示します。どちらもVLMには渡しません。
- **過去の出力**: 前に変換した出力を、あとから開いてプレビューできます（ファイル → 過去の出力を開く…）。
- **プレビュー**: ページの画像、抽出テキスト、VLMの結果を、変換の実行中から並べて見られます。`mermaid` のブロックは、図として描きます。
- **VLMでの読み取り（Step3、任意）**: OpenAI互換のサーバー、またはログイン済みのコマンド（`claude`、`codex`、`opencode`）の、画像を読めるモデルで、ページの画像から文字を読み取って加えます。トークンと費用を記録します。
- **フォルダ、中断と再開**: ファイルもフォルダも変換できます。中断して、後で続きから再開できます。既存の出力は上書きしません。
- **確実な終了**: 変換中は停止を待ってから閉じます。終了処理が応答しなくなった場合も、8秒後にアプリのプロセスを終了します。
- **2つのエンジン**: Microsoft Office（COM）または LibreOffice（画面を出さずに実行）でPDFを作ります。画面は日本語と English です。

## 変換の流れ

| Step | 内容 |
| ---- | ---- |
| Step1 | テキストをMarkdownにし、全ページのPNGを保存します。常に実行します。 |
| Step2 | Markdownに画像を入れます。 |
| Step3 | 任意。各ページの画像をVLMで読み、文字を入れます。 |

## インストール

[Releases](https://github.com/umineko73/MarkPortO/releases) から `MarkPortO-<version>-win-x64.zip` をダウンロードして展開し、`MarkPortO.exe`（画面）または `cli\markporto.exe`（コマンドライン）を実行します。

必要なもの: Windows 11、**.NET 10 デスクトップ ランタイム**、Microsoft Office または [LibreOffice](https://www.libreoffice.org/)（LibreOfficeだけのときは、エンジンで選びます）。Step3には、画像を読めるモデルを載せたOpenAI互換のサーバー（例: LM Studio）、または上のコマンドが要ります。

ソースからビルドするとき: `dotnet build MarkPortO.slnx`（.NET 10 SDK）。

## 使い方

### 画面

ファイルまたはフォルダを追加し（一覧へのドロップでも）、出力先を選んで、「開始」を押します。「条件」には、フォルダ内の対象の種類（とサブフォルダ）の行、種類ごとにだれがテキストを抽出するか（内蔵か外部コマンドか）の行、種類ごとのオプション（外部コマンドを選ぶと灰色になります）、Stepの行があります。印刷エンジン、DPI、太字・斜体、文書の画像、図形は、Step1の行にあります。上の部分は折りたためます（折りたたむと、選んだ内容を1行で示します。Stepは出たままです）。ファイル一覧とページ一覧は高さを分け合い、間の境目をドラッグして変えられます。1ファイルのときは「ページ範囲」（`3-5,8` のように）で、Step3で読むページを絞れます。設定は `%APPDATA%\MarkPortO\settings.json` に保存され、次回に元に戻ります。

**プレビュー**（ファイルをダブルクリック）: ページ画像・抽出テキスト・VLMの結果のうち見たいものを、横並びか縦並びで、描画かソースで見られます。「追従」は、最後に読み終えたページを表示します。ページ画像は、Ctrl+ホイールで拡大・縮小できます（Ctrl+0で100%）。ページを移したときや新しい結果が来たときも、拡大率とスクロール位置は保たれます。左右の矢印キーとマウスホイールでページを移り（ホイールは、まずページの端で止まります）、右ボタンのドラッグでスクロールできます。

![プレビュー画面](docs/images/preview.ja.png)

**過去の出力を開く**（ファイル → 過去の出力を開く…）: 出力先のフォルダ（または1つの文書のフォルダ）を選びます。出力先の `.markporto` に残した状態を読み、文書を新しい変換の順に一覧にします。何度も変換したファイルの結果は、下の箱に新しい順に並びます。ボタンは、箱で選んだ結果に対して働きます（**プレビュー**、**エクスプローラで開く**、**開く**）。読むだけで、何も書き換えません。出力先を別の場所へ移したりコピーしたりしても、`.markporto` が一緒にあれば開けます。「開く」は、選んだファイルをメイン画面の「変換するファイル・フォルダ」とファイルの一覧に入れ、出力先をこの出力先にします。変換は始めません。そのあと「すべてやり直し」で変換するか、ファイルの一覧で選んで「再変換」で、前と同じフォルダに変換し直します。**以前の変換を再開**することもできます。箱で結果を選び（中断したもの、あとで新しい変換を始めたために破棄されたもの、失敗を含むもの）、「開く」を押すと、メイン画面がその変換を前回の変換として表示し、「中断したところから再開」で続きを実行できます。ファイルは複数選べます（Ctrl＋クリック、Shift＋クリック）。複数選んだときは、それぞれ最新の結果を使います（未完了のファイルが再開できる変換に入っていれば、その変換を再開できます）。「プレビュー」は1つだけ選んだときに使えます。

**VLM接続** と **プロンプト** は、「設定」メニューにあります。プロンプトは、ファイルの種類ごとに名前を付けて複数持てます（小さいローカルモデル用とクラウドLLM用の例入り）。使うものはメイン画面か `--prompt <名前>` で選びます。プロンプトに `{pagetext}` を書くと、Step1がそのページから取ったテキストを、画像と一緒に送ります。

![VLM接続の設定](docs/images/connections.ja.png)

ファイル一覧の「出力先」の列には、各文書の出力フォルダが出ます。右クリックのメニューで、そのフォルダ、Markdown、VLMの結果のMarkdownを開けます。

変換の実行中は、条件を変えられません。

**変換のボタン**: ファイルの一覧の状態で、押せるものが決まります。一覧には、出力先にある、今の「変換するファイル・フォルダ」と同じ入力の前回の変換が出ます。入力を変えると、その入力の前回の変換を探し直し、なければ一覧は空になります（入力の違う変換の続きは「過去の出力を開く」から再開できます）。

| 一覧の状態 | 押せるボタン |
|---|---|
| 空（出力先に前回の変換がない） | **開始** |
| 完了以外が残っている | **中断したところから再開**、**すべてやり直し** |
| すべて完了 | **すべてやり直し**、**変更されたファイルのみ変換** |

- **開始**: 入力のファイルをすべて変換します。
- **中断したところから再開**: 前回の変換の続きです。未完了のファイルがあればその続き、失敗だけが残っていれば、失敗したファイルと、VLMが読めなかったページをやり直します。
- **すべてやり直し**: 入力のファイルを、画面の条件ですべて変換し直します。前に変換したファイルの書き先は、ボタンの右の「前回のフォルダがあるとき」で選びます（「別名で新しく作る」か「上書きする」。「開始」にも働きます）。
- **変更されたファイルのみ変換**: 出力先での前回の変換から変わったファイルと、新しいファイルだけを変換します。前回と同じフォルダに書きます。

一部のファイルを変換し直すときは、ファイルの一覧で選び（Ctrl＋クリック、Shift＋クリック）、一覧の右上の「再変換」を押すか、右クリックの「選択したファイルを再変換」を選びます。前回と同じフォルダに書きます。Markdown とページ画像が置き換わるので、ファイルの場所は変わりません。

### コマンドライン

```powershell
markporto convert D:\docs\report.docx --out D:\out
markporto convert D:\docs --out D:\out --types docx,xlsx --recurse
markporto convert D:\docs --out D:\out --step3 on --settings settings.json   # VLMで読む
markporto convert --resume --out D:\out                                      # 中断した変換を続ける
markporto list-jobs --out D:\out
markporto check-connection --settings settings.json
```

主なオプション: `--settings`、`--out`、`--types`、`--recurse`、`--include-hidden`（または `--include-hidden-sheets`、`--include-hidden-slides`）、`--emphasis` / `--no-emphasis`（太字を `**`、斜体を `*` で囲む）、`--no-include-off-slide`、`--pdf-text on|off`、`--pictures on|off`（Word・PowerPoint・Excel の画像を `pictures/` に保存する）、`--shapes on|off`（PowerPoint・Excel の図形をページ画像から切り出して `pictures/` に保存する）、`--engine`、`--dpi`、`--pages <範囲>`（1ファイルのとき、Step3で読むページ。`3-5,8` のように）、`--excel-range columns|sheet|print-area`、`--step2 on|off`、`--step3 on|off`、`--vlm-separate on|off`、`--step1-tables off|replace|omit`（ページの文字に表があるページで、VLMの結果の表を、文字の表に置き換える、または省く）、`--drop-repeated-tables on|off`（VLMが同じページで繰り返し書いた表を除く）、`--result <file>`、`--resume`、`--retry-failed`、`--changed-only`（前回の変換から変わったファイルだけを、前と同じフォルダに変換）、`--same-folder on|off`（on は、すべてのファイルを変換し、前回の変換があるファイルは前回と同じフォルダに書き直す。off は新しいフォルダを作る）、`--quiet`、`--max-pages`、`--max-cost`、`--parallel`。指定は `settings.json` より優先されます。

終了コード: `0` 成功、`1` 一部のファイルが失敗、`2` 引数や設定が不正、`3` 開始前の確認に失敗（同意がない場合を含む）、`4` 利用上限・ログイン切れ・ページ数や費用の上限で停止（`--resume` で続ける）、`130` 中断。

### `settings.json`

```json
{
  "schemaVersion": 1,
  "input": { "paths": ["D:\\docs"], "recurse": true, "types": ["docx", "xlsx", "pptx", "pdf"], "includeHiddenSheets": false, "includeHiddenSlides": false, "includeOffSlideText": true },
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

場所は、環境変数 `MARKPORTO_SETTINGS` で変えられます。アクセスキーは、値ではなく環境変数の*名前*（`apiKeyEnv`）を書きます。画面で入力したキーは、現在のWindowsユーザーだけが読める形（DPAPI）で保存し、ログや出力には出ません。

### 外部コマンドにMarkdownを書かせる

ファイルの種類ごとに、自分で入れたプログラム(MarkItDown、Docling、Pandoc など)にMarkdownを書かせられます。MarkPortOは、そのプログラムをインストールも更新もしません。与えた引数で、シェルを通さずに実行するだけです。結果は、コマンドが書いた文書全体のMarkdownで、**ページの区切りも、ページ画像の参照もありません**(ページ画像は `images/` に保存され、VLMの結果は `名前_vlm.md` に書かれます)。その種類での、MarkPortOの機能(複数ページの表、結合セル、太字・斜体など)は効きません。MarkItDown・Docling・Pandoc で、サンプルの文書を試しましたが、すべてのツール・版・文書では確かめていません。ツールによって結果も違います。出てきたMarkdownを確認してください(設定の「試す…」で、1つの文書に実行できます)。

コマンドを使うときのStepの動き: Step1は、ページ画像を(いつもどおり)保存し、テキストはコマンドが書きます。Step2は、コマンドのMarkdownには画像を差し込みません。Step3は、いつもどおりページ画像を読み、結果は `名前_vlm.md` に書きます。種類の横のオプション(非表示のシート、シートの範囲など)は内蔵の抽出用で、コマンドを選んでいる間は灰色になります。

コマンドは、メニューの「設定 > 抽出コマンドの設定」で登録します(名前、コマンド、引数、タイムアウト、環境変数、文書を外へ送ることへの同意、「試す…」で1つの文書に実行、「ひな形…」でMarkItDown・Docling・Pandocの通常の設定を追加)。下の例のように `settings.json` に書いても同じです。ファイルの種類ごとに選びます(条件の「抽出方法」。コマンドがあると出ます。または `--extractor-docx|xlsx|pptx|pdf <名前>`。`builtin` は内蔵)。

```json
"extractors": [
  { "name": "markitdown", "command": "markitdown", "args": ["{input}", "-o", "{output}"] },
  { "name": "docling", "command": "docling", "args": ["{input}", "--to", "md", "--image-export-mode", "placeholder", "--output", "{outdir}"], "timeoutSeconds": 600 }
],
"input": { "extractors": { "pdf": "docling" } }
```

`{input}` は入力のファイル、`{output}` は書き出すMarkdownのファイル、`{outdir}` はコマンド用のフォルダです(`{outdir}` だけを渡したときは、コマンドがそこに書いた `.md` を読みます)。`"output": "stdout"` なら、標準出力を読みます。コマンドは `PATH` から探します(フルパスでも可)。たとえば `uv tool install "markitdown[all]"` 、`uv tool install docling`、`winget install JohnMacFarlane.Pandoc` で入れたあとです。Pandocは、docx と pptx はよく読みますが、PDFは読めず、xlsx のシートは、マス目のままの表にします。引数はツールの版で違うので、先に手で試してください。文書をPCの外へ送るコマンドは、`"sendsExternally": true` と同意が要ります。抽出コマンドの設定の「同意...」を押すか、`markporto consent <名前>` を実行します(`--revoke` で取り消し)。

### `claude`・`codex`・`opencode` で読み取る

```json
{ "name": "cloud", "kind": "command", "command": "claude", "model": "haiku", "effort": "low", "parallel": 2, "maxPages": 200, "maxCostUsd": 3.0 }
```

ページ画像ごとに、空の一時フォルダで、コマンドを1回実行します。**自分の設定・フック・メモリ・ツールは使いません。** ページ数や費用の上限、利用上限、ログイン切れ、同じ失敗が3ページ続いたときは、停止します（再開できます）。

**ページの画像と、ページのテキストは、コマンドの先のサービスへ、ネットワークの外に送られ、費用がかかることがあります。** 機密の文書には使わないでください。このような接続（と、ローカルネットワークの外にあるOpenAI互換の接続）は、同意したあとだけ使います。接続の設定の「同意...」を押すか、`markporto consent <名前> --settings settings.json` を実行します（`--revoke` で取り消し）。

## 出力

```text
out/report/
├── report.md          # 元のファイル名と、構造の説明から始まります
├── report_vlm.md      # --vlm-separate on のときだけ: Step3の結果
├── report.log         # ページごとの記録(大きさ、画像、削除ページ・空白ページ)、警告、失敗
└── images/
    ├── page-001.png
    └── page-002.png
```

結合セルは notePM 方式で書きます（`<` は左のセル、`^` は上のセルと結合）。取り消し線の付いた文字は、`~~文字~~` で囲んで書きます。`<!-- page: N -->` の行が、各ページの始まりです。

## 0.3.0 の変更

- Word・PowerPoint・Excelに貼られた画像を保存し、EMF・WMF・BMP・TIFFをPNGに変換します。
- PowerPoint・Excelの図形をページ画像から切り出して保存します。
- 再開時の同時実行を防ぎ、外部抽出の同意を再確認し、中断時は実行中のページ処理を完了してから終了します。
- 終了処理が応答しなくてもアプリのプロセスが残らないようにしました。
- Excelの画像・図形の配置を修正し、使用範囲を越える図形も正しい大きさで扱います。

## 開発

```powershell
dotnet test MarkPortO.slnx                             # エンジンが要るテストは、無ければスキップ
./scripts/build.ps1 -Task Publish                      # ビルド、テスト、artifacts/MarkPortO-<version>-win-x64.zip
```

`src/MarkPortO.Core`（変換。UIに依存しない）、`src/MarkPortO.Cli`、`src/MarkPortO.App`（WPF）、`tests`、`scripts`、`tools`。すべてのソースに同じライセンスヘッダーが付きます（`python tools/add-license-header.py`）。CI: `.github/workflows/ci.yml`。

## ライセンス

[GPL-3.0-only](LICENSE)（「それ以降の版」の選択肢はありません）。Copyright (C) 2026 umineko73。依存するものと、同梱の Mermaid のライセンスは、[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) にあります。
