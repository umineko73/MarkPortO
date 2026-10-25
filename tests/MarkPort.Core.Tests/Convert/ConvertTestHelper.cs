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

using MarkPort.Core.Convert;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Convert;

/// <summary>Runs a conversion with LibreOffice into a temporary folder.</summary>
public static class ConvertTestHelper
{
    public static OptionsSnapshot Options(int dpi = 72, bool step2 = true, bool includeHidden = false, bool step3 = false, string? connection = null, string markdownLanguage = "ja") => new()
    {
        Types = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx],
        Dpi = dpi,
        Engine = EngineKind.LibreOffice,
        Step2 = step2,
        Step3 = step3,
        Connection = connection,
        IncludeHidden = includeHidden,
        MarkdownLanguage = markdownLanguage,
    };

    public static ConvertRequest Request(string outputFolder, OptionsSnapshot options, params string[] files) => new()
    {
        Inputs = [.. files.Select(f => new PlannedInput(f))],
        GivenInputs = files,
        OutputFolder = outputFolder,
        Options = options,
    };

    public static LibreOfficeEngine Engine(TempDir dir) => new(profileRoot: dir.Combine("profiles"));

    public static async Task<ConvertResult> RunAsync(TempDir dir, ConvertRequest request, CancellationToken cancellationToken = default)
    {
        using var engine = Engine(dir);
        var service = new ConvertService(engine);
        return await service.RunAsync(request, cancellationToken);
    }

    public static string[] ImageFiles(string documentFolder) =>
        Directory.Exists(Path.Combine(documentFolder, "images"))
            ? [.. Directory.GetFiles(Path.Combine(documentFolder, "images"), "*.png").Order(StringComparer.Ordinal)]
            : [];
}
