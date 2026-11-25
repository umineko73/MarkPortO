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

using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Jobs;

/// <summary>FR-155: an output without <c>.markporto</c> is read from the <c>conversion.json</c> of its document folders.</summary>
[Trait("Engine", "LibreOffice")]
public class PastRecordsTests
{
    [LibreOfficeFact]
    public async Task AnOutputWhoseStateWasRemoved_IsReadFromItsRecords_WithItsPages_AndCannotBeResumed()
    {
        using var dir = new TempDir();
        var docx = dir.Combine("in", "報告書.docx");
        DocxFactory.CreatePaged(docx, ["One"], ["Two"], ["Three"]);
        var xlsx = dir.Combine("in", "売上.xlsx");
        XlsxFactory.Create(xlsx, new SheetSpec("東", [["月", "額"], ["1", "100"]]), new SheetSpec("西", [["月", "額"], ["1", "200"]]));
        var options = Options();
        options.ExcelPerSheet = true;
        var out1 = dir.Combine("out");
        await RunAsync(dir, Request(out1, options, docx, xlsx));
        Directory.Delete(Path.Combine(out1, StateStore.StateFolderName), recursive: true);

        Assert.Null(PastOutputs.FindRoot(out1));
        var records = PastOutputs.LoadRecords(out1);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.True(r.FromRecordOnly && r.FolderFound && r.CanPreview));
        var report = records.Single(r => r.Document.Kind == DocumentKind.Docx);
        Assert.Equal(docx, report.Document.SourcePath);
        Assert.Equal(StepStatus.Done, report.Document.Step2);
        Assert.Equal(StepStatus.NotApplicable, report.Document.Step3);
        Assert.Equal([1, 2, 3], report.Document.Pages.Select(p => p.Index));
        Assert.Equal("images/page-001.png", report.Document.Pages[0].ImageFile);
        Assert.Equal(JobStatus.Completed, report.Job.Status);
        Assert.Empty(PreviousRunFinder.Resumable(out1)); // nothing to resume without the state
        var book = records.Single(r => r.Document.Kind == DocumentKind.Xlsx);
        Assert.Equal(["売上_東", "売上_西"], book.Document.SheetFiles);
        Assert.Equal(["東", "西"], book.Document.Pages.Select(p => p.Sheet?.Name).Distinct());
    }

    [LibreOfficeFact]
    public async Task ADocumentFolderCopiedAlone_IsReadFromItsRecord()
    {
        using var dir = new TempDir();
        var docx = dir.Combine("in", "報告書.docx");
        DocxFactory.CreatePaged(docx, ["One"], ["Two"]);
        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), docx));
        var copy = dir.Combine("elsewhere", "報告書");
        CopyFolder(result.Documents[0].OutputFolder, copy);

        var records = PastOutputs.LoadRecords(dir.Combine("elsewhere"));

        var only = Assert.Single(records);
        Assert.Equal(copy, only.Document.OutputFolder);
        Assert.Equal(2, only.Document.Pages.Count);
        Assert.True(File.Exists(Path.Combine(copy, only.Document.Pages[0].ImageFile)));
    }

    [Fact]
    public void ABrokenRecord_OrOneOfANewerForm_IsLeftOut_AndTheStateFolderIsNotRead()
    {
        using var dir = new TempDir();
        Write(dir.Combine("broken", ConversionRecord.FileName), "{ not json");
        Write(dir.Combine("newer", ConversionRecord.FileName), "{\"schemaVersion\": 99}");
        Write(dir.Combine(StateStore.StateFolderName, "x", ConversionRecord.FileName), "{\"schemaVersion\": 1}");

        Assert.Empty(PastOutputs.LoadRecords(dir.Path));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
