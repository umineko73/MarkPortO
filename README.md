# MarkPort

Word・Excel・PowerPoint の文書（docx・xlsx・pptx）を、Markdownと、ページごとの印刷イメージ（PNG）に変換するWindows向けのツールです。画面（WPF）とコマンドライン（CLI）があり、どちらも同じ変換処理を使います。

> 開発中です。仕様・計画・タスクは [`specs/001-office-to-markdown/`](specs/001-office-to-markdown/)、進み具合は [`tasks.md`](specs/001-office-to-markdown/tasks.md) のチェックボックスを見てください。

## できること

- 文書のテキストをMarkdownにし、全ページの画像（PNG）を保存します。Markdownの各ページの位置に、そのページの画像への参照を入れます。
- Excelは、シートの全列が1枚の画像に収まるようにします。列が極端に多いシートは、列の区切りで複数の画像に分けます。
- ページ全体に「×」の図形が付いたページは、削除ページとして扱います。画像は保存し、Markdownにはそのページの位置に削除を示す文言だけを書きます。
- フォルダ単位の変換、サブフォルダの走査、種類（docx / xlsx / pptx）の選択ができます。出力先に同名のフォルダがあれば `名前 (2)` のように連番を付け、既存の出力は上書きしません。
- 途中で中断でき、次回に続きから再開できます。完了済みのファイルは、やり直しません。
- Step3（任意）: OpenAI互換のVLMサーバー（画像を読めるモデル）で、ページの画像から文字を読み取ってMarkdownに入れます。接続先は複数登録して切り替えられ、使用トークン数をページごと・ファイルごとに記録します。
- 変換エンジンは Microsoft Office（COM）か LibreOffice（画面を出さずに実行）を選べます。既定は Office です。画面の選択肢には、インストールされているエンジンだけが出ます。
- 画面は、日本語と English に対応しています。初めて起動したときは、OSの言語（対応していない言語は English）で表示します。言語は、メニューの「設定」から、再起動せずに切り替えられます。ダークテーマは保留中です（画面はライトのみです）。出力するMarkdownの文言も、画面の言語に合わせます（変換を始めたときの言語で、その変換の間は変わりません）。コマンドラインのメッセージは English 固定です。

状態: 画面の操作（一覧の更新、中断と再開、設定の復元）と、実際のVLMモデルでの読み取りは、手動の確認がまだです（[`tasks.md`](specs/001-office-to-markdown/tasks.md) の T050・T080・T088）。

## 必要なもの

- Windows 11
- .NET 10 SDK（ビルドするとき）
- Microsoft Office（Word・Excel・PowerPoint）または [LibreOffice](https://www.libreoffice.org/) のどちらか（文書をPDFにするために使います。画面は出しません）。LibreOfficeだけの環境では、`engine.kind` を `libreoffice` にします

## アイコン

`src/MarkPort.App/Assets/MarkPort.ico` は `python tools/make-icon.py`（Pillow が必要）で作り直せます。

## ビルドとテスト

```powershell
dotnet build MarkPort.slnx
dotnet test MarkPort.slnx                            # 使えるエンジンが無い環境では、そのエンジンを使うテストはスキップされます
dotnet test MarkPort.slnx --filter "Engine!=Office"   # Officeが無い環境（LibreOfficeのみ）
```

## 使い方

### コマンドライン

```powershell
dotnet run --project src/MarkPort.Cli -- convert D:\docs\報告書.docx --out D:\out
dotnet run --project src/MarkPort.Cli -- convert D:\docs --out D:\out --types docx,xlsx --recurse
dotnet run --project src/MarkPort.Cli -- convert --resume --out D:\out      # 中断した変換を再開する
dotnet run --project src/MarkPort.Cli -- list-jobs --out D:\out             # 出力先にある変換の一覧
dotnet run --project src/MarkPort.Cli -- convert D:\docs --out D:\out --step3 on --settings settings.json   # VLMで読み取る
dotnet run --project src/MarkPort.Cli -- check-connection --settings settings.json    # VLMサーバーへの接続を確かめる
dotnet run --project src/MarkPort.Cli -- list-connections --settings settings.json   # 登録した接続の一覧（キーは表示しない）
```

主なオプション: `--settings <path>`（`settings.json`）、`--out`、`--types`、`--recurse`、`--include-hidden`、`--engine`、`--dpi`、`--step2 on|off`、`--step3 on|off`、`--result <file>`（結果をJSONで保存）、`--resume`、`--job`、`--retry-failed`、`--quiet`。コマンドラインの指定は `settings.json` より優先されます。全項目と終了コードは [`contracts/cli.md`](specs/001-office-to-markdown/contracts/cli.md) にあります。

アクセスキーは、`settings.json` に環境変数の**名前**（`apiKeyEnv`）として書くのが基本です（値そのものは保存しません）。画面で入力したキーは、現在のWindowsユーザーだけが復号できる形（DPAPI）で保存します。ログ・Markdown・状態ファイルにキーは出ません。

終了コード: `0` すべて成功、`1` 一部が失敗、`2` 引数または設定が不正、`3` 開始前のチェックに失敗、`130` 中断（`Ctrl+C`。`--resume` で再開できます）。

### 画面

```powershell
dotnet run --project src/MarkPort.App
```

メニューバーの「設定」に、言語（OSに合わせる / 日本語 / English）、VLM接続の設定、プロンプトの設定があります。「ヘルプ」に、バージョン情報とライセンス表記があります。

ファイルまたはフォルダを追加し、出力先を選んで「開始」を押します。「条件」には、対象の種類、範囲、変換（エンジンとDPI）、Step1〜Step3の行があります（Step1は常に実行します）。一覧に、ファイルごとの状態（待機中、Step1〜3の実行中、完了、完了（一部失敗）、失敗、中断）が出ます。フォルダを指定したファイルは、指定したフォルダからの相対パスで表示します。VLMが読み取れなかったページがあるファイルは、「失敗のみ再試行」で、そのページだけを読み直せます。画面で設定した値は、すぐに `settings.json`（`%APPDATA%\MarkPort\settings.json`）に保存され、次回の起動で元に戻ります。

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
      { "name": "local", "baseUrl": "http://localhost:1234/v1", "model": "vision-model", "apiKeyEnv": "MARKPORT_API_KEY", "timeoutSeconds": 120 }
    ]
  }
}
```

設定ファイルの場所は、環境変数 `MARKPORT_SETTINGS` で変えられます（画面もコマンドラインも、`--settings` を指定しないときに使います）。言語とテーマは `ui.language`（`system` / `ja` / `en`）と `ui.theme`（`system` / `light` / `dark`）です。

項目の定義は [`contracts/settings.schema.json`](specs/001-office-to-markdown/contracts/settings.schema.json) にあります。

## 出力

文書ごとに、拡張子を除いた名前のフォルダができます。

```text
出力先/報告書/
├── 報告書.md          # 先頭に、元のファイル名とMarkdownの構造の説明があります
└── images/
    ├── page-001.png
    └── page-002.png
```

Markdownの形式は [`contracts/markdown-format.md`](specs/001-office-to-markdown/contracts/markdown-format.md) を見てください。

## 構成

```text
src/MarkPort.Core/   変換処理のすべて（画面に依存しない）
src/MarkPort.Cli/    コマンドライン
src/MarkPort.App/    画面（WPF）
tests/               テスト（検証用の文書は、テストコードが生成します）
specs/               仕様・計画・タスク（Spec Kit）
```

原則は [`.specify/memory/constitution.md`](.specify/memory/constitution.md)（憲章）にあります。

## ライセンス

MarkPort は [GNU General Public License バージョン3（GPL-3.0-only）](LICENSE) のもとで公開します。「それ以降のバージョン」は選べません（`-only`）。著作者は umineko73 です。すべてのソースファイルの先頭に、同じヘッダーを付けています。新しいファイルを足したら、`python tools/add-license-header.py` でヘッダーを付けてください（付け忘れは試験で見つかります）。

依存パッケージのライセンスは [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) にあります。
