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
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Tests.Fixtures;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Convert;

/// <summary>FR-013a: the file list shows the path from the folder that was given, or the file name.</summary>
public class DisplayNameTests
{
    private static List<DocumentRecord> Plan(TempDir dir, params string[] given)
    {
        var enumerated = InputEnumerator.Enumerate(given, [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx], recurse: true);
        var request = new ConvertRequest
        {
            Inputs = enumerated.Inputs,
            InputFolder = enumerated.InputFolder,
            GivenInputs = given,
            OutputFolder = dir.Combine("out"),
            Options = Options(),
        };

        return ConvertService.Plan(request, new StateStore(dir.Combine("out"))).Documents;
    }

    [Fact]
    public void AFolder_ShowsThePathFromThatFolder()
    {
        using var dir = new TempDir();
        DocxFactory.CreatePaged(dir.Combine("in", "top.docx"), ["a"]);
        DocxFactory.CreatePaged(dir.Combine("in", "支店A", "report.docx"), ["a"]);
        DocxFactory.CreatePaged(dir.Combine("in", "支店A", "2026", "deep.docx"), ["a"]);

        var names = Plan(dir, dir.Combine("in")).Select(d => d.Display()).Order().ToList();

        Assert.Equal(["top.docx", Path.Combine("支店A", "2026", "deep.docx"), Path.Combine("支店A", "report.docx")], names.Order().ToList());
    }

    [Fact]
    public void FilesNamedOneByOne_ShowTheirFileName()
    {
        using var dir = new TempDir();
        DocxFactory.CreatePaged(dir.Combine("x", "a.docx"), ["a"]);
        DocxFactory.CreatePaged(dir.Combine("x", "b.docx"), ["a"]);

        var names = Plan(dir, dir.Combine("x", "a.docx"), dir.Combine("x", "b.docx")).Select(d => d.Display()).ToList();

        Assert.Equal(["a.docx", "b.docx"], names);
    }

    [Fact]
    public void FilesWithTheSameName_FromDifferentFolders_GetTheNameOfTheirFolder()
    {
        using var dir = new TempDir();
        DocxFactory.CreatePaged(dir.Combine("x", "a.docx"), ["a"]);
        DocxFactory.CreatePaged(dir.Combine("y", "a.docx"), ["a"]);

        var names = Plan(dir, dir.Combine("x", "a.docx"), dir.Combine("y", "a.docx")).Select(d => d.Display()).Order().ToList();

        Assert.Equal([Path.Combine("x", "a.docx"), Path.Combine("y", "a.docx")], names);
    }

    [Fact]
    public void SeveralFolders_ShowTheFolderNameInFront()
    {
        using var dir = new TempDir();
        DocxFactory.CreatePaged(dir.Combine("one", "a.docx"), ["a"]);
        DocxFactory.CreatePaged(dir.Combine("two", "a.docx"), ["a"]);

        var names = Plan(dir, dir.Combine("one"), dir.Combine("two")).Select(d => d.Display()).Order().ToList();

        Assert.Equal([Path.Combine("one", "a.docx"), Path.Combine("two", "a.docx")], names);
    }

    [Fact]
    public void TheRow_ShowsTheDisplayName_AndTheFullPathForTheTooltip()
    {
        using var dir = new TempDir();
        DocxFactory.CreatePaged(dir.Combine("in", "支店A", "report.docx"), ["a"]);

        var document = Plan(dir, dir.Combine("in")).Single();
        var row = FileStatusProjector.Project(document);

        Assert.Equal(Path.Combine("支店A", "report.docx"), row.FileName);
        Assert.Equal(Path.GetFullPath(dir.Combine("in", "支店A", "report.docx")), row.FullPath);
    }

    [Fact]
    public void StateWrittenBeforeTheDisplayNameExisted_ShowsTheFileName()
    {
        var document = new DocumentRecord { SourcePath = @"C:\docs\old.docx" };

        Assert.Equal("old.docx", document.Display());
    }
}
