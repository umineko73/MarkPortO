# MarkPortO

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Version](https://img.shields.io/badge/version-0.3.2-2ea44f.svg)

**English** | [日本語](README.ja.md)

MarkPortO is a **Windows app that converts Word, Excel, PowerPoint and PDF files into Markdown and page images (PNG)**.

![The main window of MarkPortO](docs/images/main-window.png)

## Features

- Convert headings, tables, lists, code and equations into Markdown.
- Compare the result with the original page images in a preview.
- Convert folders of documents and resume interrupted runs.
- Optionally read pages with an image-capable model (VLM).

Under development. Results vary with document layout, so check the preview after conversion.

## Use cases

- **Knowledge extraction for LLMs and RAG**: Turn a library of documents into Markdown that a language model can read. Each page is marked (`<!-- page: N -->`) and has its own page image, so a passage can be traced back to its page, and a page that is hard to extract as text (a scan, a diagram) can be read from its image with a VLM.
- **Office-to-Markdown migration**: Move Word, Excel, PowerPoint and PDF documents into a wiki, a notes tool or a Git repository as Markdown, with their tables, lists, code, equations and pictures. Convert whole folders, and check each result against the original page in the preview.

## Getting started

You need **Windows 11** and the **.NET 10 Desktop Runtime**. Office documents also require Microsoft Office or [LibreOffice](https://www.libreoffice.org/).

1. Download `MarkPortO-<version>-win-x64.zip` from [Releases](https://github.com/umineko73/MarkPortO/releases).
2. Extract the ZIP and run `MarkPortO.exe`.
3. If you use LibreOffice, select it as the engine in the app.

## Basic usage

1. Add files or a folder, or drag them onto the app.
2. Choose an output folder.
3. Press **Start**. Turn off Step3 if you do not want to use the VLM.
4. Double-click a completed file to preview the result.

Each document gets a folder containing its Markdown (`.md`) and page images (`images/`). Open them from the file list's right-click menu.

To use the VLM, set up a VLM connection in **Settings** and turn on Step3. External services receive document images and text, and may charge for usage.

## Preview

Double-click a file to open the preview. The image of each page is shown next to its Markdown, so you can check tables, merged cells, lists, code, equations and pictures against the original.

![The preview: the page image of a Word document and its Markdown](docs/images/preview-document.png)

- Show the page image, the extracted text and the VLM result, side by side or stacked. The Markdown can be shown rendered or as source.
- Move between pages with the arrow keys, and to the previous or next file with ◀ ▶. Ctrl+wheel zooms.
- **Follow** shows the last page that was read while a conversion is running.

## Converting again

You do not have to convert everything again to fix part of a result.

- **Read some pages again with the VLM (from the preview)**: check the pages in the preview's page list, or press **Mark pages that could not be read**. Then press **Run Step 3 again** in the main window. Only the marked pages are read again, with the connection and prompt chosen on the screen. After you change the connection or a prompt, **Run Step 3 again** also reads the pages that were read with the earlier settings.
- **Convert some files again**: select them in the list and press **Convert again from Step 1** (also in the right-click menu). Their Markdown and page images in the earlier folder are replaced.
- **Convert the files that changed**: **Reconvert updated files** converts only the changed and new files.
- **Resume**: goes on with an interrupted conversion, or runs the failed files and pages again.

## Command line

You can also use `markporto-cli.exe` from the ZIP (in the same folder as `MarkPortO.exe`):

```powershell
markporto-cli convert D:\docs\report.docx --out D:\out
markporto-cli convert D:\docs --out D:\out --types docx,xlsx --recurse
```

Up to 0.3.1 the command line was `cli/markporto.exe` in a folder of its own; it now has a name of its own, so that it does not look like `MarkPortO.exe`.

See [Detailed usage](docs/usage.md) for reconversion, VLM connections, all CLI options and configuration examples.

## Development

```powershell
dotnet test MarkPortO.slnx                             # tests that need an engine are skipped if it is not installed
./scripts/build.ps1 -Task Publish                      # build, test, and write artifacts/MarkPortO-<version>-win-x64.zip
```

`src/MarkPortO.Core` (conversion, no UI), `src/MarkPortO.Cli`, `src/MarkPortO.App` (WPF), `tests`, `scripts`, `tools`. Every source file has the same license header (`python tools/add-license-header.py`). CI: `.github/workflows/ci.yml`.

## License

[GPL-3.0-only](LICENSE) (the "or any later version" option is not offered). Copyright (C) 2026 umineko73. The licenses of the dependencies, including Mermaid, are in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
