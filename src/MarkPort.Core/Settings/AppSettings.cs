// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Text.Json.Serialization;
using MarkPort.Core.Localization;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Settings;

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

    public List<DocumentKind> Types { get; set; } = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx];

    /// <summary>Include hidden sheets (xlsx) and hidden slides (pptx) (FR-010a).</summary>
    public bool IncludeHidden { get; set; }
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
}

public sealed class StepSettings
{
    public bool Step2 { get; set; } = true;

    public bool Step3 { get; set; }
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
    public const string DefaultDocx =
        "この画像は Word 文書の1ページです。ページに書かれているテキストを、見出し・段落・表の構造を保ってMarkdownで書き起こしてください。図や画像に含まれる文字も読み取ってください。説明や前置きは書かないでください。";

    public const string DefaultXlsx =
        "この画像は Excel シートの一部です。表のセルの内容を、行と列の構造を保ってMarkdownの表で書き起こしてください。グラフや図形に含まれる文字も読み取ってください。説明や前置きは書かないでください。";

    public const string DefaultPptx =
        "この画像は PowerPoint のスライド1枚です。スライドに書かれているテキストを、タイトルと本文の構造を保ってMarkdownで書き起こしてください。図や画像に含まれる文字も読み取ってください。説明や前置きは書かないでください。";

    public string Docx { get; set; } = DefaultDocx;

    public string Xlsx { get; set; } = DefaultXlsx;

    public string Pptx { get; set; } = DefaultPptx;

    public string For(DocumentKind kind) => kind switch
    {
        DocumentKind.Docx => Docx,
        DocumentKind.Xlsx => Xlsx,
        DocumentKind.Pptx => Pptx,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
