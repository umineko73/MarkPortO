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

using System.Text.Json;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Convert;

/// <summary>FR-155 / SC-063: a conversion leaves <c>conversion.json</c> in each document folder.</summary>
[Trait("Engine", "LibreOffice")]
public class ConversionRecordConvertTests
{
    [LibreOfficeFact]
    public async Task AConvertedDocx_HasItsRecord_Completed_WithStep3NotUsed()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "報告書.docx");
        DocxFactory.CreatePaged(source, ["First page"], ["Second page"]);
        var out1 = dir.Combine("out");

        var result = await RunAsync(dir, Request(out1, Options(), source));

        var document = Assert.Single(result.Documents);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(document.OutputFolder, ConversionRecord.FileName)));
        var r = json.RootElement;
        Assert.Equal(source, r.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal("報告書.docx", r.GetProperty("source").GetProperty("relativePath").GetString());
        Assert.Equal(out1, r.GetProperty("outputRoot").GetString());
        Assert.Equal(result.Job.JobId.ToString("D"), r.GetProperty("jobId").GetString());
        Assert.Equal("completed", r.GetProperty("status").GetString());
        Assert.Equal("done", r.GetProperty("steps").GetProperty("step1").GetString());
        Assert.Equal("done", r.GetProperty("steps").GetProperty("step2").GetString());
        Assert.Equal("notApplicable", r.GetProperty("steps").GetProperty("step3").GetString());
        Assert.Equal(2, r.GetProperty("pages").GetProperty("total").GetInt32());
        Assert.Equal(["報告書.md"], r.GetProperty("files").GetProperty("markdown").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("libreOffice", r.GetProperty("conditions").GetProperty("engine").GetString());
    }

    [LibreOfficeFact]
    public async Task AnXlsxWrittenPerSheet_HasOneRecord_WithEachSheetAndItsPages()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "売上.xlsx");
        XlsxFactory.Create(source, new SheetSpec("東", [["月", "額"], ["1", "100"]]), new SheetSpec("西", [["月", "額"], ["1", "200"]]));
        var options = Options();
        options.Types = [DocumentKind.Xlsx];
        options.ExcelPerSheet = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var document = Assert.Single(result.Documents);
        Assert.Single(Directory.GetFiles(document.OutputFolder, ConversionRecord.FileName));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(document.OutputFolder, ConversionRecord.FileName)));
        var files = json.RootElement.GetProperty("files");
        Assert.Equal(["売上_東.md", "売上_西.md"], files.GetProperty("markdown").EnumerateArray().Select(e => e.GetString()));
        var sheets = files.GetProperty("sheets").EnumerateArray().ToList();
        Assert.Equal(["東", "西"], sheets.Select(s => s.GetProperty("sheet").GetString()));
        Assert.All(sheets, s => Assert.NotEmpty(s.GetProperty("pages").EnumerateArray()));
    }
}
