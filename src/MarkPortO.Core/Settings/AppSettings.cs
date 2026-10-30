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

using System.Text.Json.Serialization;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Settings;

/// <summary>The contents of <c>settings.json</c>: every value that can be set on the screen (FR-018).</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public InputSettings Input { get; set; } = new();

    public OutputSettings Output { get; set; } = new();

    public EngineSettings Engine { get; set; } = new();

    public ImageSettings Images { get; set; } = new();

    public StepSettings Steps { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public VlmSettings Vlm { get; set; } = new();

    public static AppSettings CreateDefault() => new();

    /// <summary>
    /// Checks the rules of <c>data-model.md</c>. Returns one message per problem; an empty list means valid.
    /// Rules that only matter when a run starts (output folder, selected connection) are in
    /// <see cref="ValidateForRun"/>.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Ui.Language is not (LanguageResolver.System or LanguageResolver.Japanese or LanguageResolver.English))
        {
            errors.Add(Loc.T("ui.language: must be system, ja or en (was '{0}').", Ui.Language));
        }

        if (Ui.Theme is not (UiSettings.SystemTheme or UiSettings.LightTheme or UiSettings.DarkTheme))
        {
            errors.Add(Loc.T("ui.theme: must be system, light or dark (was '{0}').", Ui.Theme));
        }

        if (Input.Types.Count == 0)
        {
            errors.Add(Loc.T("input.types: choose at least one of docx, xlsx, pptx."));
        }
        else if (Input.Types.Distinct().Count() != Input.Types.Count)
        {
            errors.Add(Loc.T("input.types: each type may appear only once."));
        }

        if (Images.Dpi is < SettingsLimits.MinDpi or > SettingsLimits.MaxDpi)
        {
            errors.Add(Loc.T("images.dpi: must be between {0} and {1} (was {2}).", SettingsLimits.MinDpi, SettingsLimits.MaxDpi, Images.Dpi));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Vlm.Connections.Count; i++)
        {
            var connection = Vlm.Connections[i];
            var prefix = $"vlm.connections[{i}]";

            if (string.IsNullOrWhiteSpace(connection.Name))
            {
                errors.Add(Loc.T("{0}.name: must not be empty.", prefix));
            }
            else if (!names.Add(connection.Name))
            {
                errors.Add(Loc.T("{0}.name: '{1}' is used more than once.", prefix, connection.Name));
            }

            if (connection.Kind == ConnectionKind.OpenAi)
            {
                if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(Loc.T("{0}.baseUrl: must be an http or https URL (was '{1}').", prefix, connection.BaseUrl));
                }

                if (string.IsNullOrWhiteSpace(connection.Model))
                {
                    errors.Add(Loc.T("{0}.model: must not be empty.", prefix));
                }
            }
            else
            {
                ValidateCommand(connection, prefix, errors);
            }

            if (connection.MaxPages is < 1)
            {
                errors.Add(Loc.T("{0}.maxPages: must be empty or at least 1 (was {1}).", prefix, connection.MaxPages));
            }

            if (connection.MaxCostUsd is { } cost && !(cost > 0 && double.IsFinite(cost)))
            {
                errors.Add(Loc.T("{0}.maxCostUsd: must be empty or more than 0 (was {1}).", prefix, cost));
            }

            if (connection.MaxImageSide != 0 && connection.MaxImageSide < SettingsLimits.MinImageSide)
            {
                errors.Add(Loc.T("{0}.maxImageSide: must be 0 or at least {1} (was {2}).", prefix, SettingsLimits.MinImageSide, connection.MaxImageSide));
            }

            if (connection.MaxLinesPerRead < 0)
            {
                errors.Add(Loc.T("{0}.maxLinesPerRead: must be 0 or more (was {1}).", prefix, connection.MaxLinesPerRead));
            }

            if (connection.TimeoutSeconds is < SettingsLimits.MinTimeoutSeconds or > SettingsLimits.MaxTimeoutSeconds)
            {
                errors.Add(Loc.T("{0}.timeoutSeconds: must be between {1} and {2} (was {3}).", prefix, SettingsLimits.MinTimeoutSeconds, SettingsLimits.MaxTimeoutSeconds, connection.TimeoutSeconds));
            }
        }

        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            if (string.IsNullOrWhiteSpace(Vlm.Prompts.For(kind)))
            {
                errors.Add(Loc.T("vlm.prompts.{0}: must not be empty.", kind.ToString().ToLowerInvariant()));
            }
        }

        return errors;
    }

    private static void ValidateCommand(ConnectionProfile connection, string prefix, List<string> errors)
    {
        if (connection.Command is null)
        {
            errors.Add(Loc.T("{0}.command: choose claude, codex or opencode.", prefix));
        }

        if (!IsPlainWord(connection.Model))
        {
            errors.Add(Loc.T("{0}.model: may contain only letters, digits and . _ : / @ + - (was '{1}').", prefix, connection.Model));
        }

        if (!IsPlainExecutable(connection.Executable))
        {
            errors.Add(Loc.T("{0}.executable: must be the name or the path of the command, without arguments (was '{1}').", prefix, connection.Executable));
        }

        if (connection.Effort is not ("" or "low" or "medium" or "high"))
        {
            errors.Add(Loc.T("{0}.effort: must be empty, low, medium or high (was '{1}').", prefix, connection.Effort));
        }

        if (connection.Parallel is < 1 or > SettingsLimits.MaxParallel)
        {
            errors.Add(Loc.T("{0}.parallel: must be between 1 and {1} (was {2}).", prefix, SettingsLimits.MaxParallel, connection.Parallel));
        }
    }

    /// <summary>The model is passed to the command as an argument: a word, never an option or a piece of a command line.</summary>
    private static bool IsPlainWord(string? text) =>
        string.IsNullOrEmpty(text)
        || (!text.StartsWith('-') && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '/' or '@' or '+' or '-'));

    /// <summary>A name or a path. A path with spaces must be a full path of a program, so that arguments cannot be hidden in it.</summary>
    private static bool IsPlainExecutable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Any(c => char.IsControl(c) || c is '"' or '%' or '^' or '&' or '|' or '<' or '>'))
        {
            return false;
        }

        return !text.Any(char.IsWhiteSpace)
            || (Path.IsPathRooted(text) && new[] { ".exe", ".cmd", ".bat", ".com" }.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Extra rules checked when a run starts.</summary>
    public IReadOnlyList<string> ValidateForRun()
    {
        var errors = new List<string>(Validate());

        if (string.IsNullOrWhiteSpace(Output.Folder))
        {
            errors.Add(Loc.T("output.folder: choose an output folder."));
        }

        if (Steps.Step3)
        {
            if (string.IsNullOrWhiteSpace(Vlm.Selected)
                || Vlm.Connections.All(c => !string.Equals(c.Name, Vlm.Selected, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(Loc.T("vlm.selected: step 3 is on, but no usable connection is selected."));
            }
        }

        return errors;
    }

    public ConnectionProfile? FindConnection(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Vlm.Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class SettingsLimits
{
    public const int MinImageSide = 256;

    public const int MinDpi = 72;
    public const int MaxDpi = 600;
    public const int DefaultDpi = 150;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The most external commands that may run at the same time (FR-044).</summary>
    public const int MaxParallel = 8;
}

public sealed class InputSettings
{
    public List<string> Paths { get; set; } = [];

    public bool Recurse { get; set; }

    public List<DocumentKind> Types { get; set; } = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf];

    /// <summary>
    /// The setting of before "hidden sheets" and "hidden slides" were two: it is read (and sets both), and never written.
    /// It is declared first, so that a file that has the new settings too takes them.
    /// </summary>
    [JsonPropertyName("includeHidden")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeHiddenBoth
    {
        get => null;
        set
        {
            if (value is { } both)
            {
                IncludeHiddenSheets = both;
                IncludeHiddenSlides = both;
            }
        }
    }

    /// <summary>Include hidden sheets of a workbook (xlsx) (FR-010a).</summary>
    public bool IncludeHiddenSheets { get; set; }

    /// <summary>Include hidden slides of a presentation (pptx) (FR-010a).</summary>
    public bool IncludeHiddenSlides { get; set; }

    /// <summary>Write the text of the shapes that lie outside a slide (memos on the pasteboard), as a quote of its own (pptx, FR-086).</summary>
    public bool IncludeOffSlideText { get; set; } = true;

    /// <summary>Use the text layer of a PDF as the text of its pages, next to the page images (pdf, FR-076).</summary>
    public bool UsePdfText { get; set; } = true;
}

public sealed class OutputSettings
{
    public string Folder { get; set; } = string.Empty;
}

/// <summary>The language and the theme of the screen (FR-033, FR-035). <c>system</c> follows the operating system.</summary>
public sealed class UiSettings
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    /// <summary><c>system</c>, <c>ja</c> or <c>en</c>.</summary>
    public string Language { get; set; } = LanguageResolver.System;

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = SystemTheme;
}

public sealed class EngineSettings
{
    /// <summary>Microsoft Office by default, as in <c>data-model.md</c>; LibreOffice can be chosen.</summary>
    public EngineKind Kind { get; set; } = EngineKind.Office;

    /// <summary>Path of <c>soffice.exe</c>. Empty means: look in the usual places.</summary>
    public string LibreOfficePath { get; set; } = string.Empty;
}

public sealed class ImageSettings
{
    public int Dpi { get; set; } = SettingsLimits.DefaultDpi;

    /// <summary>How the pages of an Excel sheet are cut (FR-073).</summary>
    public ExcelRange ExcelRange { get; set; } = ExcelRange.Columns;
}

public sealed class StepSettings
{
    public bool Step2 { get; set; } = true;

    public bool Step3 { get; set; }

    /// <summary>Write the VLM results to a separate file, <c>name_vlm.md</c>, not into the Markdown of the document (FR-058).</summary>
    public bool VlmSeparate { get; set; }
}

public sealed class VlmSettings
{
    public string Selected { get; set; } = string.Empty;

    public List<ConnectionProfile> Connections { get; set; } = [];

    public PromptSettings Prompts { get; set; } = new();
}

/// <summary>What a connection talks to (FR-043).</summary>
[JsonConverter(typeof(LowercaseEnumConverter<ConnectionKind>))]
public enum ConnectionKind
{
    /// <summary>An OpenAI-compatible server (<c>chat/completions</c>).</summary>
    OpenAi,

    /// <summary>An external command: <c>claude</c>, <c>codex</c> or <c>opencode</c>.</summary>
    Command,
}

[JsonConverter(typeof(LowercaseEnumConverter<CommandKind>))]
public enum CommandKind
{
    Claude,
    Codex,
    OpenCode,
}

/// <summary>The consent to send page images outside the local network (FR-046, FR-052).</summary>
public sealed class ConsentRecord
{
    public DateTimeOffset At { get; set; }

    /// <summary>The version of the notice the user agreed to. A newer notice asks again.</summary>
    public int NoticeVersion { get; set; }
}

/// <summary>The initial values of a new connection of the kind "command" (FR-043).</summary>
public static class CommandDefaults
{
    public static string InitialModel(CommandKind kind) => kind switch
    {
        CommandKind.Claude => "haiku",
        CommandKind.Codex => "gpt-6-luna",
        CommandKind.OpenCode => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class ConnectionProfile
{
    public string Name { get; set; } = string.Empty;

    public ConnectionKind Kind { get; set; } = ConnectionKind.OpenAi;

    /// <summary>The command, when <see cref="Kind"/> is <see cref="ConnectionKind.Command"/>.</summary>
    public CommandKind? Command { get; set; }

    /// <summary>Name or path of the executable. Empty means: the usual name, looked up in the PATH. No arguments (FR-043).</summary>
    public string? Executable { get; set; }

    /// <summary>How hard the command thinks: empty (the command's default), low, medium or high (FR-053).</summary>
    public string Effort { get; set; } = "low";

    /// <summary>The most commands that run at the same time (FR-044).</summary>
    public int Parallel { get; set; } = 1;

    /// <summary>Stop after this many pages have been read in a job. Empty means no limit (FR-057).</summary>
    public int? MaxPages { get; set; }

    /// <summary>Stop after this cost (US dollars) has been spent in a job. Empty means no limit (FR-057).</summary>
    public double? MaxCostUsd { get; set; }

    /// <summary>The consent to send images outside the local network. Null: not given (FR-046).</summary>
    public ConsentRecord? ExternalConsent { get; set; }

    /// <summary>The most lines read in one request; 0 never splits a page (FR-041).</summary>
    public int MaxLinesPerRead { get; set; }

    /// <summary>The longer side, in pixels, that a page image is scaled down to before it is sent to the VLM; 0 sends it as it is (FR-064).</summary>
    public int MaxImageSide { get; set; }

    /// <summary>For example <c>http://localhost:1234/v1</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Name of the environment variable that holds the API key. Takes precedence over <see cref="ApiKey"/>.</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary><c>dpapi:&lt;Base64&gt;</c> for a value entered on the screen, or a hand-written plain value.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = SettingsLimits.DefaultTimeoutSeconds;
}

public sealed class PromptSettings
{
    // Each default takes in the text that Step1 got from the document ({pagetext}, FR-061): the image tells the layout and
    // what the text cannot (figures, pictures), the text gives the exact characters.
    public const string DefaultDocx =
        "この画像は Word 文書の1ページです。次の「抽出テキスト」は、同じページから文書ファイルを直接読んで取り出した文字列です。画像のレイアウトを見ながら、抽出テキストの文字を正確に使って、見出し・段落・表の構造を保ったMarkdownで書き起こしてください。抽出テキストにない文字（図や画像に含まれる文字）は、画像から読み取って補ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}";

    public const string DefaultXlsx =
        "この画像は Excel シートの一部です。次の「抽出テキスト」は、同じシートから文書ファイルを直接読んで取り出した内容です（画像に写っていない列や行も含みます）。画像のレイアウトを見ながら、抽出テキストの文字と数値を正確に使って、この画像に写っている範囲を、行と列の構造を保ったMarkdownの表で書き起こしてください。グラフや図形に含まれる文字は、画像から読み取ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。\n\n表は、次の決まりで書いてください。\n1. シートの中の、空白の行や列で区切られた「まとまり」（タイトル、項目と値の一覧、年ごとの表など）ごとに、別々の表にします。全体を1つの表にしないでください。表と表の間は、空行を1行置きます。\n2. 左右に並んだ複数のまとまり（間に空の列がある）は、左のまとまりの表を書き終えてから、右のまとまりの表を書きます。1行の中に、左右のまとまりを一緒に書かないでください。\n3. 1つの表の中では、全行の列の数を同じにします。空のセルも省かず、「| |」と書いてください。幅の違うまとまりは、別の表にします。\n4. すべての行を「|」で始めて「|」で終えます。「項目 | 値 |」のように、行頭の「|」を省かないでください。\n5. 表の1行目は見出し行にし、その次の行に必ず区切り行（「|---|---|」）を置きます。見出しにあたる行がない表は、空のセルの見出し行（「| | |」）を置き、1行目からデータ行として書いてください。見出しの文字を作らないでください。\n6. 表にならない文章（注記など）は、「|」を使わず、そのまま書きます。\n\n例（項目と値の一覧は、見出しなしの2列の表）:\n| | |\n|---|---|\n| 初期投資 | 0,000 |\n| 耐用年数 | 10 |\n\n抽出テキスト:\n{pagetext}";

    public const string DefaultPptx =
        "この画像は PowerPoint のスライド1枚です。次の「抽出テキスト」は、同じスライドから文書ファイルを直接読んで取り出した文字列です。画像のレイアウトを見ながら、抽出テキストの文字を正確に使って、タイトルと本文の構造を保ったMarkdownで書き起こしてください。抽出テキストにない文字（図や画像に含まれる文字）は、画像から読み取って補ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}";

    public const string DefaultDocxEn =
        "This image is one page of a Word document. The \"Extracted text\" below is the text that was read directly from the document file for the same page. Looking at the layout in the image, transcribe the page as Markdown that keeps the structure of headings, paragraphs and tables, using the characters of the extracted text exactly. Read the text that is not in the extracted text (text inside figures and pictures) from the image and add it. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}";

    public const string DefaultXlsxEn =
        "This image is part of an Excel sheet. The \"Extracted text\" below is the content that was read directly from the document file for the same sheet (it also includes columns and rows that are not in the image). Looking at the layout in the image, transcribe only the range shown in this image as Markdown tables that keep the structure of rows and columns, using the characters and numbers of the extracted text exactly. Read the text inside charts and shapes from the image. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble.\n\nWrite the tables by these rules.\n1. Make a separate table for each group of cells that is set apart by empty rows or columns (a title, a list of items and values, a table by year, and so on). Do not make the whole sheet one table. Put one empty line between tables.\n2. Groups that stand side by side (with an empty column between them): write the table of the left group first, then the table of the right group. Do not write the left and the right group in one line.\n3. Inside one table, every row has the same number of columns. Do not leave out an empty cell: write \"| |\". A group of a different width is another table.\n4. Start every row with \"|\" and end it with \"|\". Do not leave out the leading \"|\" as in \"Item | Value |\".\n5. The first row of a table is the header row, and the row after it must be the separator row (\"|---|---|\"). A table that has no header row gets a header row of empty cells (\"| | |\"), and its first row is written as a data row. Do not make up header text.\n6. Text that is not a table (a note, for example) is written as it is, without \"|\".\n\nExample (a list of items and values is a table of two columns without a header):\n| | |\n|---|---|\n| Initial investment | $50,000 |\n| Lifetime | 10 |\n\nExtracted text:\n{pagetext}";

    public const string DefaultPptxEn =
        "This image is one slide of a PowerPoint presentation. The \"Extracted text\" below is the text that was read directly from the document file for the same slide. Looking at the layout in the image, transcribe the slide as Markdown that keeps the structure of the title and the body, using the characters of the extracted text exactly. Read the text that is not in the extracted text (text inside figures and pictures) from the image and add it. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}";

    // A PDF is read mostly from the image; its text layer, when it has one, only helps (FR-076).
    public const string DefaultPdf =
        "この画像は PDF 文書の1ページです。画像を見て、見出し・段落・表・箇条書きの構造を保ったMarkdownで書き起こしてください。次の「抽出テキスト」は、同じページのテキスト層から取り出した文字列で、補助として使います（空のこともあり、順序や改行が崩れていることもあります）。画像の文字を読み取り、抽出テキストは固有名詞・数字・記号を確かめるために使ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}";

    public const string DefaultPdfEn =
        "This image is one page of a PDF document. Looking at the image, transcribe the page as Markdown that keeps the structure of headings, paragraphs, tables and lists. The \"Extracted text\" below is the text of the text layer of the same page; use it only as a help (it may be empty, and its order and line breaks may be wrong). Read the characters from the image, and use the extracted text to check proper names, numbers and symbols. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}";

    /// <summary>The built-in prompt of the kind, in the language ("ja", or English for anything else).</summary>
    public static string DefaultFor(DocumentKind kind, string language)
    {
        var ja = language == "ja";
        return kind switch
        {
            DocumentKind.Docx => ja ? DefaultDocx : DefaultDocxEn,
            DocumentKind.Xlsx => ja ? DefaultXlsx : DefaultXlsxEn,
            DocumentKind.Pptx => ja ? DefaultPptx : DefaultPptxEn,
            DocumentKind.Pdf => ja ? DefaultPdf : DefaultPdfEn,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // The default prompts of earlier versions: a saved prompt that is one of them was never edited, so it follows the language.
    private static readonly string[] SupersededDefaults =
    [
        "この画像は Excel シートの一部です。次の「抽出テキスト」は、同じシートから文書ファイルを直接読んで取り出した内容です（画像に写っていない列や行も含みます）。画像のレイアウトを見ながら、抽出テキストの文字と数値を正確に使って、この画像に写っている範囲を、行と列の構造を保ったMarkdownの表で書き起こしてください。グラフや図形に含まれる文字は、画像から読み取ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。表の行は、すべて「|」で始めて「|」で終え、列の数を見出し行と同じにしてください（空のセルも省かず、「|」と「|」の間を空けて書き、列をずらさないでください）。\n\n抽出テキスト:\n{pagetext}",
        "This image is part of an Excel sheet. The \"Extracted text\" below is the content that was read directly from the document file for the same sheet (it also includes columns and rows that are not in the image). Looking at the layout in the image, transcribe only the range shown in this image as a Markdown table that keeps the structure of rows and columns, using the characters and numbers of the extracted text exactly. Read the text inside charts and shapes from the image. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble. Write every row of a table with a leading and a trailing \"|\", with the same number of columns as the header row (write an empty cell between two \"|\" so that no column shifts).\n\nExtracted text:\n{pagetext}",
        "この画像は Excel シートの一部です。次の「抽出テキスト」は、同じシートから文書ファイルを直接読んで取り出した内容です（画像に写っていない列や行も含みます）。画像のレイアウトを見ながら、抽出テキストの文字と数値を正確に使って、この画像に写っている範囲を、行と列の構造を保ったMarkdownの表で書き起こしてください。グラフや図形に含まれる文字は、画像から読み取ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。回答全体や表を ```markdown などのコードブロックで囲まないでください。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}",
        "This image is part of an Excel sheet. The \"Extracted text\" below is the content that was read directly from the document file for the same sheet (it also includes columns and rows that are not in the image). Looking at the layout in the image, transcribe only the range shown in this image as a Markdown table that keeps the structure of rows and columns, using the characters and numbers of the extracted text exactly. Read the text inside charts and shapes from the image. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not wrap the whole answer or a table in a code block such as ```markdown. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}",
        "この画像は Word 文書の1ページです。次の「抽出テキスト」は、同じページから文書ファイルを直接読んで取り出した文字列です。画像のレイアウトを見ながら、抽出テキストの文字を正確に使って、見出し・段落・表の構造を保ったMarkdownで書き起こしてください。抽出テキストにない文字（図や画像に含まれる文字）は、画像から読み取って補ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}",
        "この画像は Excel シートの一部です。次の「抽出テキスト」は、同じシートから文書ファイルを直接読んで取り出した内容です（画像に写っていない列や行も含みます）。画像のレイアウトを見ながら、抽出テキストの文字と数値を正確に使って、この画像に写っている範囲を、行と列の構造を保ったMarkdownの表で書き起こしてください。グラフや図形に含まれる文字は、画像から読み取ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}",
        "この画像は PowerPoint のスライド1枚です。次の「抽出テキスト」は、同じスライドから文書ファイルを直接読んで取り出した文字列です。画像のレイアウトを見ながら、抽出テキストの文字を正確に使って、タイトルと本文の構造を保ったMarkdownで書き起こしてください。抽出テキストにない文字（図や画像に含まれる文字）は、画像から読み取って補ってください。抽出テキストと画像が食い違うときは、画像を優先してください。図（フローチャート、構成図、シーケンス図など）は、構造が読み取れるなら Mermaid 記法（```mermaid のコードブロック）で書いて構いません。説明や前置きは書かないでください。\n\n抽出テキスト:\n{pagetext}",
        "This image is one page of a Word document. The \"Extracted text\" below is the text that was read directly from the document file for the same page. Looking at the layout in the image, transcribe the page as Markdown that keeps the structure of headings, paragraphs and tables, using the characters of the extracted text exactly. Read the text that is not in the extracted text (text inside figures and pictures) from the image and add it. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}",
        "This image is part of an Excel sheet. The \"Extracted text\" below is the content that was read directly from the document file for the same sheet (it also includes columns and rows that are not in the image). Looking at the layout in the image, transcribe only the range shown in this image as a Markdown table that keeps the structure of rows and columns, using the characters and numbers of the extracted text exactly. Read the text inside charts and shapes from the image. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}",
        "This image is one slide of a PowerPoint presentation. The \"Extracted text\" below is the text that was read directly from the document file for the same slide. Looking at the layout in the image, transcribe the slide as Markdown that keeps the structure of the title and the body, using the characters of the extracted text exactly. Read the text that is not in the extracted text (text inside figures and pictures) from the image and add it. If the extracted text and the image disagree, trust the image. You may write a diagram (flowchart, architecture diagram, sequence diagram, etc.) in Mermaid syntax (a ```mermaid code block) when its structure can be read. Do not write explanations or a preamble.\n\nExtracted text:\n{pagetext}",
    ];

    /// <summary>True if the text is the built-in prompt of the kind in either language, that is, it was never edited.</summary>
    public static bool IsDefault(DocumentKind kind, string text) =>
        text == DefaultFor(kind, "ja") || text == DefaultFor(kind, "en") || SupersededDefaults.Contains(text);

    public string Docx { get; set; } = DefaultDocx;

    public string Xlsx { get; set; } = DefaultXlsx;

    public string Pptx { get; set; } = DefaultPptx;

    public string Pdf { get; set; } = DefaultPdf;

    public string For(DocumentKind kind) => kind switch
    {
        DocumentKind.Docx => Docx,
        DocumentKind.Xlsx => Xlsx,
        DocumentKind.Pptx => Pptx,
        DocumentKind.Pdf => Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The prompt to send. A prompt that was never edited (the built-in one, in either language) follows the language.</summary>
    public string For(DocumentKind kind, string language)
    {
        var text = For(kind);
        return IsDefault(kind, text) ? DefaultFor(kind, language) : text;
    }
}
