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
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Tests.Fixtures;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Convert;

[Trait("Engine", "LibreOffice")]
public class FolderOutputTests
{
    private static ConvertRequest FolderRequest(string folder, string outFolder, OptionsSnapshot options, bool recurse, params DocumentKind[] types)
    {
        var found = InputEnumerator.Enumerate([folder], types, recurse);
        return new ConvertRequest
        {
            Inputs = found.Inputs,
            GivenInputs = [folder],
            InputFolder = found.InputFolder,
            OutputFolder = outFolder,
            Options = options,
        };
    }

    [LibreOfficeFact]
    public async Task OnlyTheChosenTypes_AreConverted()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "mixed");
        DocxFactory.CreatePaged(Path.Combine(folder, "a.docx"), ["docx text"]);
        XlsxFactory.Create(Path.Combine(folder, "b.xlsx"), new SheetSpec("S", [["x"]]));

        var result = await RunAsync(dir, FolderRequest(folder, dir.Combine("out"), Options(), recurse: false, DocumentKind.Docx));

        var document = Assert.Single(result.Documents);
        Assert.Equal("a.docx", Path.GetFileName(document.SourcePath));
        Assert.False(Directory.Exists(Path.Combine(dir.Combine("out", "mixed"), "b")));
    }

    [LibreOfficeFact]
    public async Task SubFolders_AreConvertedOnlyWhenAskedFor()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "tree");
        DocxFactory.CreatePaged(Path.Combine(folder, "top.docx"), ["top"]);
        DocxFactory.CreatePaged(Path.Combine(folder, "sub", "inner.docx"), ["inner"]);
        var all = new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx };

        var flat = await RunAsync(dir, FolderRequest(folder, dir.Combine("flat"), Options(), recurse: false, all));
        var deep = await RunAsync(dir, FolderRequest(folder, dir.Combine("deep"), Options(), recurse: true, all));

        Assert.Equal(["top.docx"], flat.Documents.Select(d => Path.GetFileName(d.SourcePath)));
        Assert.Equal(["inner.docx", "top.docx"], deep.Documents.Select(d => Path.GetFileName(d.SourcePath)).Order());
    }

    [LibreOfficeFact]
    public async Task TheOutputIsGroupedInAFolderNamedAfterTheInputFolder_AndKeepsTheSubFolderStructure()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "報告書");
        DocxFactory.CreatePaged(Path.Combine(folder, "top.docx"), ["top"]);
        DocxFactory.CreatePaged(Path.Combine(folder, "第1章", "inner.docx"), ["inner"]);
        var outFolder = dir.Combine("out");

        var result = await RunAsync(dir, FolderRequest(folder, outFolder, Options(), recurse: true, DocumentKind.Docx));

        var root = Path.Combine(outFolder, "報告書");
        Assert.Equal(root, result.Job.OutputRoot);
        Assert.True(File.Exists(Path.Combine(root, "top", "top.md")));
        Assert.True(File.Exists(Path.Combine(root, "第1章", "inner", "inner.md")));
        Assert.Equal(folder, result.Job.InputRoot);
    }

    [LibreOfficeFact]
    public async Task ConvertingTheSameFolderAgain_UsesNumberedFolders_AndNeverTouchesEarlierOutput()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "報告書");
        DocxFactory.CreatePaged(Path.Combine(folder, "a.docx"), ["text"]);
        var outFolder = dir.Combine("out");
        var request = FolderRequest(folder, outFolder, Options(), false, DocumentKind.Docx);

        var first = await RunAsync(dir, request);
        var firstMarkdown = File.ReadAllText(Path.Combine(outFolder, "報告書", "a", "a.md"));
        var firstTime = File.GetLastWriteTimeUtc(Path.Combine(outFolder, "報告書", "a", "a.md"));
        var second = await RunAsync(dir, request);
        var third = await RunAsync(dir, request);

        Assert.Equal(Path.Combine(outFolder, "報告書"), first.Job.OutputRoot);
        Assert.Equal(Path.Combine(outFolder, "報告書 (2)"), second.Job.OutputRoot);
        Assert.Equal(Path.Combine(outFolder, "報告書 (3)"), third.Job.OutputRoot);
        Assert.Equal(firstMarkdown, File.ReadAllText(Path.Combine(outFolder, "報告書", "a", "a.md")));
        Assert.Equal(firstTime, File.GetLastWriteTimeUtc(Path.Combine(outFolder, "報告書", "a", "a.md")));
        Assert.True(File.Exists(Path.Combine(outFolder, "報告書 (2)", "a", "a.md")));
    }

    [LibreOfficeFact]
    public async Task ADocxAndAnXlsxWithTheSameName_DoNotOverwriteEachOther()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "same");
        DocxFactory.CreatePaged(Path.Combine(folder, "a.docx"), ["from the docx"]);
        XlsxFactory.Create(Path.Combine(folder, "a.xlsx"), new SheetSpec("S", [["from the xlsx"]]));

        var result = await RunAsync(dir, FolderRequest(folder, dir.Combine("out"), Options(), false, DocumentKind.Docx, DocumentKind.Xlsx));

        var root = Path.Combine(dir.Combine("out"), "same");
        var docx = result.Documents.Single(d => d.Kind == DocumentKind.Docx);
        var xlsx = result.Documents.Single(d => d.Kind == DocumentKind.Xlsx);
        Assert.NotEqual(docx.OutputFolder, xlsx.OutputFolder);
        Assert.Equal(Path.Combine(root, "a"), docx.OutputFolder);
        Assert.Equal(Path.Combine(root, "a (2)"), xlsx.OutputFolder);
        // Each Markdown names its own source file, so the folders can be told apart.
        Assert.Contains("from the docx", File.ReadAllText(Path.Combine(docx.OutputFolder, "a.md")));
        Assert.Contains("# a.docx", File.ReadAllText(Path.Combine(docx.OutputFolder, "a.md")));
        Assert.Contains("from the xlsx", File.ReadAllText(Path.Combine(xlsx.OutputFolder, "a.md")));
        Assert.Contains("# a.xlsx", File.ReadAllText(Path.Combine(xlsx.OutputFolder, "a.md")));
    }

    [LibreOfficeFact]
    public async Task TwoDocumentsOfTheSameNameInDifferentSubFolders_BothSurvive()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "twin");
        DocxFactory.CreatePaged(Path.Combine(folder, "x", "same.docx"), ["in x"]);
        DocxFactory.CreatePaged(Path.Combine(folder, "y", "same.docx"), ["in y"]);

        var result = await RunAsync(dir, FolderRequest(folder, dir.Combine("out"), Options(), true, DocumentKind.Docx));

        var root = Path.Combine(dir.Combine("out"), "twin");
        Assert.Contains("in x", File.ReadAllText(Path.Combine(root, "x", "same", "same.md")));
        Assert.Contains("in y", File.ReadAllText(Path.Combine(root, "y", "same", "same.md")));
        Assert.All(result.Documents, d => Assert.True(d.IsDone()));
    }

    [LibreOfficeFact]
    public async Task StatesOfSeveralDocuments_AreAllSavedInTheOneJob()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "many");
        for (var i = 1; i <= 3; i++)
        {
            DocxFactory.CreatePaged(Path.Combine(folder, $"d{i}.docx"), [$"text {i}"]);
        }

        var result = await RunAsync(dir, FolderRequest(folder, dir.Combine("out"), Options(), false, DocumentKind.Docx));

        Assert.Equal(["d0001", "d0002", "d0003"], result.Job.Documents);
        Assert.All(result.Documents, d => Assert.True(d.IsDone()));
        Assert.Equal(JobStatus.Completed, result.Job.Status);
    }
}
