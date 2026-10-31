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
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Settings;

/// <summary>How the Markdown comes back from an extractor (FR-110).</summary>
[JsonConverter(typeof(LowercaseEnumConverter<ExtractorOutput>))]
public enum ExtractorOutput
{
    /// <summary>The command writes it to the file <c>{output}</c>.</summary>
    File,

    /// <summary>The command writes it to the standard output.</summary>
    Stdout,
}

/// <summary>
/// An external command that writes Markdown for a file (FR-110; experimental). The program is installed by the user; MarkPortO only runs it,
/// without a shell, with the arguments below.
/// </summary>
public sealed class ExtractorProfile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Name or path of the executable.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>One argument for each item. <c>{input}</c>, <c>{outdir}</c> and <c>{output}</c> are replaced (also inside an argument).</summary>
    public List<string> Args { get; set; } = [];

    public ExtractorOutput Output { get; set; } = ExtractorOutput.File;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Where the command runs. Empty: the work folder of the document.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Environment variables to add. A value is <c>dpapi:&lt;Base64&gt;</c> when it was entered on the screen, or a hand-written plain value.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>The command sends the content of the document outside the PC (a service in the cloud). It runs only with a consent.</summary>
    public bool SendsExternally { get; set; }

    /// <summary>The consent to send documents outside (the same notice as for the VLM). Null: not given.</summary>
    public ConsentRecord? ExternalConsent { get; set; }

    public const int DefaultTimeoutSeconds = 300;
}

/// <summary>The extractor (a name in <see cref="AppSettings.Extractors"/>) for each kind of file; empty is the built-in one (FR-111).</summary>
public sealed class ExtractorChoice
{
    public string Docx { get; set; } = string.Empty;

    public string Xlsx { get; set; } = string.Empty;

    public string Pptx { get; set; } = string.Empty;

    public string Pdf { get; set; } = string.Empty;

    /// <summary>The name for a kind; empty when the built-in one is used.</summary>
    public string For(DocumentKind kind) => (kind switch
    {
        DocumentKind.Docx => Docx,
        DocumentKind.Xlsx => Xlsx,
        DocumentKind.Pptx => Pptx,
        DocumentKind.Pdf => Pdf,
        _ => string.Empty,
    }).Trim();

    public void Set(DocumentKind kind, string name)
    {
        switch (kind)
        {
            case DocumentKind.Docx: Docx = name; break;
            case DocumentKind.Xlsx: Xlsx = name; break;
            case DocumentKind.Pptx: Pptx = name; break;
            case DocumentKind.Pdf: Pdf = name; break;
        }
    }

    public ExtractorChoice Clone() => new() { Docx = Docx, Xlsx = Xlsx, Pptx = Pptx, Pdf = Pdf };

    public bool IsAny => Docx.Length + Xlsx.Length + Pptx.Length + Pdf.Length > 0;
}
