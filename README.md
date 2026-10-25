# MarkPort

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)

**English** | [日本語](README.ja.md)

MarkPort converts Word, Excel and PowerPoint documents (docx, xlsx, pptx) into Markdown plus one PNG image per page. It comes as a WPF desktop app and a command-line tool, and both use the same conversion code, so it can also run unattended in a build or CI job.

> **Status: under development.** Some parts still need a manual check on a real machine.

![The main window of MarkPort](docs/images/main-window.png)

*The main window, drawn with sample data.*

## Features

- **Markdown and page images.** The text of a document becomes Markdown, and every page is saved as a PNG. Each page's place in the Markdown refers to its image.
- **Excel sheets in one image.** All columns of a sheet fit in a single image. A sheet with an extreme number of columns is split at column boundaries into several images.
- **Deleted pages.** A page that is crossed out with a full-page "X" shape is treated as deleted: its image is kept, and the Markdown only marks the place with a notice.
- **Folders and filters.** Convert a single file or a whole folder, optionally including sub-folders, and choose which of docx / xlsx / pptx to convert. If the output folder already exists, a number is added (`name (2)`); existing output is never overwritten.
- **Interrupt and resume.** A run can be interrupted and continued later. Files that are already done are not converted again.
- **Optional VLM reading (Step3).** A VLM (a model that can read images) behind an OpenAI-compatible server reads the text from each page image and adds it to the Markdown. You can register several connections and switch between them. Token usage is recorded per page and per file.
- **Two engines.** Microsoft Office (COM) or LibreOffice (headless) turns documents into PDF. The app lists only the engines that are installed.
- **English and Japanese UI.** The first start follows the language of Windows (English if that language is not supported), and it can be switched from the menu without a restart. The language of the Markdown text follows the UI language at the start of a run. Command-line messages are always English.

## How it works

A conversion has three steps, and each step runs on its own thread so that the steps overlap across files:

| Step | What it does |
| ---- | ------------ |
| Step1 | Extracts the text into a Markdown file and saves the PNG of every page. Always runs. |
| Step2 | Inserts the images into the Markdown. |
| Step3 | Optional. Reads each page image with a VLM and inserts the text into the Markdown. |

## Requirements

- Windows 11
- .NET 10 SDK (to build)
- Either Microsoft Office (Word, Excel and PowerPoint) or [LibreOffice](https://www.libreoffice.org/). It is used to turn documents into PDF, and no window is shown. If only LibreOffice is installed, set `engine.kind` to `libreoffice`.
- For Step3: an OpenAI-compatible server with a model that can read images (for example LM Studio).

## Quick start

```powershell
git clone <repository-url>
cd MarkPort
dotnet build MarkPort.slnx

# Command line
dotnet run --project src/MarkPort.Cli -- convert D:\docs\report.docx --out D:\out

# Desktop app
dotnet run --project src/MarkPort.App
```

In the app, add files or a folder, choose an output folder, and press **Start**.

## Usage

### Command line

```powershell
dotnet run --project src/MarkPort.Cli -- convert D:\docs\report.docx --out D:\out
dotnet run --project src/MarkPort.Cli -- convert D:\docs --out D:\out --types docx,xlsx --recurse
dotnet run --project src/MarkPort.Cli -- convert --resume --out D:\out      # continue an interrupted run
dotnet run --project src/MarkPort.Cli -- list-jobs --out D:\out             # list the runs in an output folder
dotnet run --project src/MarkPort.Cli -- convert D:\docs --out D:\out --step3 on --settings settings.json   # read with a VLM
dotnet run --project src/MarkPort.Cli -- check-connection --settings settings.json    # test the VLM server
dotnet run --project src/MarkPort.Cli -- list-connections --settings settings.json   # list the connections (keys are not shown)
```

Main options: `--settings <path>`, `--out`, `--types`, `--recurse`, `--include-hidden`, `--engine`, `--dpi`, `--step2 on|off`, `--step3 on|off`, `--result <file>` (save the result as JSON), `--resume`, `--job`, `--retry-failed`, `--quiet`. Options on the command line override `settings.json`.

Exit codes: `0` everything succeeded, `1` some files failed, `2` invalid arguments or settings, `3` the checks before the run failed, `130` interrupted (`Ctrl+C`; continue with `--resume`).

### Desktop app

The **Settings** menu has the language (follow Windows / Japanese / English), the VLM connections and the prompts. The **Help** menu has the version and the license notices.

The list shows the state of each file: waiting, Step1 to Step3 running, done, done (some pages failed), failed, interrupted. For a folder, a file is shown by its path relative to that folder. If the VLM could not read some pages of a file, **Retry failed** reads only those pages again. Values you set in the app are saved to `settings.json` (`%APPDATA%\MarkPort\settings.json`) at once and restored on the next start.

### `settings.json`

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

The location of the file can be changed with the environment variable `MARKPORT_SETTINGS`; both the app and the command line use it when `--settings` is not given. `ui.language` is `system`, `ja` or `en`. `ui.theme` is `system`, `light` or `dark`.

**About API keys.** Write the *name* of an environment variable (`apiKeyEnv`) in `settings.json`; the value itself is not stored. A key typed in the app is stored so that only the current Windows user can decrypt it (DPAPI). Keys never appear in logs, Markdown or state files.

## Output

Each document gets a folder named after it, without the extension:

```text
out/report/
├── report.md          # starts with the source file name and a description of the Markdown structure
└── images/
    ├── page-001.png
    └── page-002.png
```

## Development

```powershell
dotnet build MarkPort.slnx
dotnet test MarkPort.slnx                            # tests that need an engine are skipped if it is not installed
dotnet test MarkPort.slnx --filter "Engine!=Office"   # no Microsoft Office (LibreOffice only)
```

```text
src/MarkPort.Core/   all of the conversion code (no dependency on the UI)
src/MarkPort.Cli/    command-line tool
src/MarkPort.App/    desktop app (WPF)
tests/               tests (the test documents are generated by the test code)
tools/               helper scripts (icon, license headers)
```

- `src/MarkPort.App/Assets/MarkPort.ico` can be recreated with `python tools/make-icon.py` (needs Pillow).
- Every source file starts with the same license header. After adding a file, run `python tools/add-license-header.py`; a test fails if a header is missing.

## License

MarkPort is released under the [GNU General Public License, version 3 only (GPL-3.0-only)](LICENSE). The "or any later version" option is not offered. Copyright (C) 2026 umineko73.

The licenses of the dependencies are listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
