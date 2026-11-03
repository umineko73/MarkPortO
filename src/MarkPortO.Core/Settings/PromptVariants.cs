// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Settings;

/// <summary>A prompt with a name, besides the standard prompt of a kind of file (FR-115).</summary>
public sealed class NamedPrompt
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The text. Empty for a built-in example that was not edited: it is taken from <see cref="Preset"/> and follows the language.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary><c>local</c> or <c>cloud</c> for a built-in example (<see cref="PromptPresets"/>); <c>null</c> for a prompt of the user.</summary>
    public string? Preset { get; set; }

    /// <summary>The text to send: the text of the prompt, or that of the built-in example in the language.</summary>
    public string TextIn(DocumentKind kind, string language) =>
        Text.Length > 0 ? Text : Preset is null ? string.Empty : PromptPresets.TextOf(kind, Preset, language);
}

/// <summary>The prompts of one kind of file besides the standard one, and the one that is used (FR-115).</summary>
public sealed class PromptVariants
{
    /// <summary>The name of the prompt that is used; empty for the standard prompt.</summary>
    public string Selected { get; set; } = string.Empty;

    public List<NamedPrompt> Items { get; set; } = [];

    /// <summary>The built-in examples that a new settings file starts with.</summary>
    public static PromptVariants Seeded() => new()
    {
        Items =
        [
            new NamedPrompt { Name = PromptPresets.LocalName, Preset = PromptPresets.Local },
            new NamedPrompt { Name = PromptPresets.CloudName, Preset = PromptPresets.Cloud },
        ],
    };

    /// <summary>The prompt with this name (also its built-in key, <c>local</c> or <c>cloud</c>); <c>null</c> when there is none.</summary>
    public NamedPrompt? Find(string name) =>
        Items.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Items.FirstOrDefault(p => p.Preset is not null && string.Equals(p.Preset, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The names of the prompts that a run uses, for each kind of file; empty is the standard prompt. Kept in the job, so that a resume uses the same ones.</summary>
public sealed class PromptNames
{
    public string Docx { get; set; } = string.Empty;

    public string Xlsx { get; set; } = string.Empty;

    public string Pptx { get; set; } = string.Empty;

    public string Pdf { get; set; } = string.Empty;

    public string For(DocumentKind kind) => kind switch
    {
        DocumentKind.Docx => Docx,
        DocumentKind.Xlsx => Xlsx,
        DocumentKind.Pptx => Pptx,
        DocumentKind.Pdf => Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>The prompt that a name does not point to.</summary>
public sealed class PromptNotFoundException(string message) : Exception(message);

/// <summary>The built-in examples of prompts: one for a small local model, one for a cloud LLM (FR-115).</summary>
public static class PromptPresets
{
    public const string Local = "local";

    public const string Cloud = "cloud";

    public const string LocalName = "小さいローカルモデル用";

    public const string CloudName = "クラウドLLM用";

    /// <summary>The words that name the standard prompt (in the settings the standard prompt has no name: it is the empty choice).</summary>
    public static bool IsStandardName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || string.Equals(name.Trim(), "standard", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name.Trim(), "default", StringComparison.OrdinalIgnoreCase)
        || name.Trim() == "標準";

    public static string TextOf(DocumentKind kind, string preset, string language)
    {
        var ja = language == "ja";
        return preset == Cloud ? CloudText(kind, ja) : LocalText(kind, ja);
    }

    // The standard prompt with a paragraph before the extracted text: what a strong model can do, and a small one cannot.
    private static string CloudText(DocumentKind kind, bool ja)
    {
        var standard = PromptSettings.DefaultFor(kind, ja ? "ja" : "en");
        var marker = ja ? "\n\n抽出テキスト:\n{pagetext}" : "\n\nExtracted text:\n{pagetext}";
        var sheet = kind == DocumentKind.Xlsx;
        var extra = ja
            ? "\n\n追加の指示:\n"
                + (sheet ? "- タイトルや注記のように1つのセルだけのまとまりは、表にせず、見出し（「#」）や文として書いてください。\n" : "- 見出しは、文字の大きさや太さから階層を推定して、「#」の数で表してください。\n")
                + "- 構造が読み取れる図（流れ図、構成図など）は、Mermaid 記法で書いてください。矢印の向き、分岐、差し戻しも省かないでください。\n- グラフは、数値が読み取れるときは Mermaid の xychart-beta で書き、そのあとに、軸、凡例、色の意味を1〜2文で説明してください。写真は内容を1〜2文で説明してください。\n- 数式の記法（$ ... $）は使わないでください。矢印は「→」と書いてください。\n- 注記、出典、脚注は、本文と区別して残してください。"
            : "\n\nAdditional instructions:\n"
                + (sheet ? "- A block of a single cell, such as a title or a note, is not a table: write it as a heading (\"#\") or as a sentence.\n" : "- Give headings their level by the size and weight of the text, with the number of \"#\".\n")
                + "- Write a diagram whose structure can be read (a flow chart, a block diagram) in Mermaid. Keep the direction of the arrows, the branches and the returns.\n- Write a chart whose values can be read in Mermaid xychart-beta, then explain the axes, the legend and the meaning of the colors in one or two sentences. Describe a photo in one or two sentences.\n- Do not use the math notation ($ ... $). Write an arrow as \"→\".\n- Keep notes, sources and footnotes, apart from the body text.";
        var at = standard.LastIndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? standard + extra : standard[..at] + extra + standard[at..];
    }

    // A short list of plain rules: a small model keeps to it better than to a long prompt, and is not asked for what it cannot do (charts, long descriptions).
    private static string LocalText(DocumentKind kind, bool ja)
    {
        if (kind == DocumentKind.Xlsx)
        {
            return ja
                ? "この画像は Excel シートの一部です。「抽出テキスト」の文字と数値を正確に使って、画像に写っている範囲をMarkdownで書き起こしてください。\n決まり:\n1. 空白の行や列で区切られたまとまりごとに、別々の表にする。表と表の間は空行を1行置く。\n2. 左右に並んだまとまりは、左から順に、別々の表で書く。\n3. 1つの表の中では、全行の列の数を同じにする。空のセルも「| |」と書く。\n4. すべての行を「|」で始めて「|」で終える。\n5. 1行目は見出し行にして、次の行に「|---|---|」を置く。見出しがない表は、空のセルの見出し行（「| | |」）を置く。\n6. タイトルや注記のように、1つのセルだけのまとまりは、表にしない。タイトルは「#」の見出し、注記は文として、1回だけ書く。\n7. 数式の記法（$ ... $）は使わない。\n8. 説明や前置きは書かない。回答を ``` で囲まない。\n\n抽出テキスト:\n{pagetext}"
                : "This image is a part of an Excel sheet. Using the characters and numbers of the \"Extracted text\" exactly, write the range in the image as Markdown.\nRules:\n1. Make a separate table for each block that is set apart by empty rows or columns. Put one empty line between tables.\n2. Blocks side by side are written one after the other, from the left, as separate tables.\n3. In one table, every row has the same number of columns. Write an empty cell as \"| |\".\n4. Start and end every row with \"|\".\n5. The first row is the header row; put \"|---|---|\" in the next row. A table without a header gets a header row of empty cells (\"| | |\").\n6. A block of a single cell, such as a title or a note, is not a table. Write a title as a \"#\" heading and a note as a sentence, once.\n7. Do not use the math notation ($ ... $).\n8. Write no explanation or preamble. Do not wrap the answer in ```.\n\nExtracted text:\n{pagetext}";
        }

        var head = kind == DocumentKind.Pdf
            ? (ja ? "この画像は PDF の1ページです。" : "This image is one page of a PDF file.")
            : kind == DocumentKind.Pptx
                ? (ja ? "この画像はスライドの1枚です。" : "This image is one slide.")
                : (ja ? "この画像は Word 文書の1ページです。" : "This image is one page of a Word document.");
        return ja
            ? head + "画像に写っている内容を、見出し・段落・表の構造を保ったMarkdownで書き起こしてください。\n決まり:\n1. 内容は1回だけ書く。同じ内容を繰り返さない。\n2. 「抽出テキスト」の文字を、そのまま正確に使う。画像と食い違うときは画像に合わせる。\n3. 大きい行や太い行は、見出し（「#」）にする。\n4. 表は、各行を「|」で始めて終え、2行目に「|---|---|」を置く。\n5. 流れ図は、矢印の向きと分岐が読み取れるときだけ、```mermaid のコードブロックで書く。読み取れないときは、図の中の文字だけを書く。\n6. 数式の記法（$ ... $）は使わない。矢印は「→」と書く。\n7. ヘッダー、フッター、ページ番号は書かない。\n8. 説明や前置きは書かない。回答全体を ``` で囲まない。\n\n抽出テキスト:\n{pagetext}"
            : head + " Write what is in the image as Markdown, keeping the structure of headings, paragraphs and tables.\nRules:\n1. Write the content once. Do not repeat it.\n2. Use the characters of the \"Extracted text\" exactly. When they differ from the image, follow the image.\n3. A large or bold line is a heading (\"#\").\n4. Start and end every row of a table with \"|\", and put \"|---|---|\" in the second row.\n5. Write a flow chart in a ```mermaid code block only when the direction of the arrows and the branches can be read. Otherwise write only the words in the figure.\n6. Do not use the math notation ($ ... $). Write an arrow as \"→\".\n7. Do not write the header, the footer or the page number.\n8. Write no explanation or preamble. Do not wrap the whole answer in ```.\n\nExtracted text:\n{pagetext}";
    }
}
