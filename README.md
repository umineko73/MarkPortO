# MarkPortO

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue.svg)](LICENSE)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Version](https://img.shields.io/badge/version-0.4.6-2ea44f.svg)

**English** | [日本語](README.ja.md)

A Windows app that converts Word, Excel, PowerPoint and PDF files into Markdown.
You can check the result side by side with the original page images, and make data for RAG. Every passage can be traced back to its page.

![The main window of MarkPortO](docs/images/main-window.png)

## Features

- **Markdown**: headings, tables, lists, code, equations and pictures are read straight from the Office files.
- **Compare with the original**: the preview shows the page image next to the result.
- **Diagrams cut out**: diagrams drawn with shapes are saved as pictures. A page crossed out with an "x" is left out of the text.
- **Figures read by a VLM** (optional): a model that reads images describes figures and charts. Scanned pages are read whole.
- **Data for RAG**: chunks with their headings and pages. The bundled MCP server lets Claude Code and similar tools search them.
- **Whole folders**: convert a folder at once, and resume a conversion that stopped.
- **External tools**: MarkItDown, Docling and other commands can do the conversion too.

MarkPortO is still in development. Check the results in the preview.

## Usage

1. Add files or a folder, or drag them onto the app.
2. Choose a **Preset**: **For reading** or **For RAG**.
3. Choose an output folder and press **Start**.
4. Double-click a file to check the result.

Each document gets a folder with its Markdown and page images.

To use a VLM, add a connection in **Settings**. External services receive the images and the text of your documents, and may charge for it. Without a VLM, turn Step3 off.

The built-in reading of PDF files is simple. For PDF files, MarkItDown or Docling is recommended ([more](docs/usage.md#pdf-files)).

## Preview

The page image, the extracted text and the VLM result side by side, to compare with the original page.

![The preview: the page image of a Word document, its Markdown, and the VLM result with the chart described](docs/images/preview-document.png)

## Converting again

You do not need to convert everything again to fix part of the result.

- **Some pages**: mark them in the preview, then choose **More** > **Run Step 3 again** to have the VLM read them again.
- **Some files**: select them in the list and choose **Convert the selected files again from Step 1** from the right-click menu.
- **Only changed files**: use **Reconvert updated files**.
- **Go on**: **Resume** continues a conversion that stopped. Step3 can also be added later.
- **Back to earlier work**: choosing the inputs brings back their output folder and conditions. **File** > **Past history…** does it too.

## Using it for RAG

With the **For RAG** preset, a file of chunks for search (`<name>_chunks.jsonl`) is written next to the Markdown. With the bundled MCP server (`tools\markporto-rag\`), Claude Code and similar tools can search them and trace each answer to its page.

[The format of the chunks](docs/usage.md#chunks-for-rag-_chunksjsonl) | [Setting up the search server](tools/markporto-rag/README.md)

## Installation

Download the ZIP from [Releases](https://github.com/umineko73/MarkPortO/releases), extract it, and run `MarkPortO.exe`. For the command line, use `markporto-cli.exe` in the same folder.

```powershell
markporto-cli convert D:\docs --out D:\out
```

Requirements:

- Windows 11
- .NET 10 Desktop Runtime
- Microsoft Office or [LibreOffice](https://www.libreoffice.org/)
- For a VLM: an OpenAI-compatible server with a model that reads images (LM Studio, for example)

[Detailed usage](docs/usage.md)

## Development

```powershell
dotnet build MarkPortO.slnx
dotnet test MarkPortO.slnx               # tests that need an engine are skipped without one
./scripts/build.ps1 -Task Publish        # makes the ZIP in artifacts/
```

## License

[GPL-3.0-only](LICENSE). Copyright (C) 2026 umineko73. The licenses of the dependencies are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
