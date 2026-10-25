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

namespace MarkPort.Core.Tests.Convert;

public class InputEnumeratorTests
{
    private static readonly DocumentKind[] All = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx];

    private static void Touch(params string[] paths)
    {
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private static string[] Names(EnumerationResult result, string root) =>
        [.. result.Inputs.Select(i => Path.GetRelativePath(root, i.Path))];

    [Fact]
    public void Folder_OnlyTheChosenTypesAreListed()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "a.docx"), Path.Combine(root, "b.xlsx"), Path.Combine(root, "c.pptx"), Path.Combine(root, "d.txt"));

        Assert.Equal(["a.docx"], Names(InputEnumerator.Enumerate([root], [DocumentKind.Docx], recurse: false), root));
        Assert.Equal(["a.docx", "b.xlsx"], Names(InputEnumerator.Enumerate([root], [DocumentKind.Docx, DocumentKind.Xlsx], false), root));
        Assert.Equal(["a.docx", "b.xlsx", "c.pptx"], Names(InputEnumerator.Enumerate([root], All, false), root));
    }

    [Fact]
    public void Folder_ExtensionsAreMatchedIgnoringCase()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "Upper.DOCX"), Path.Combine(root, "Mixed.XlsX"));

        Assert.Equal(2, InputEnumerator.Enumerate([root], All, false).Inputs.Count);
    }

    [Fact]
    public void Folder_SubFoldersAreSearchedOnlyWhenAsked_AndKeepTheirRelativeDirectory()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "top.docx"), Path.Combine(root, "sub", "inner.docx"), Path.Combine(root, "sub", "deep", "deeper.docx"));

        var flat = InputEnumerator.Enumerate([root], All, recurse: false);
        var deep = InputEnumerator.Enumerate([root], All, recurse: true);

        Assert.Equal(["top.docx"], Names(flat, root));
        Assert.Equal(
            [("", "top.docx"), ("sub", "inner.docx"), (Path.Combine("sub", "deep"), "deeper.docx")],
            deep.Inputs.Select(i => (i.RelativeDirectory, Path.GetFileName(i.Path))).OrderBy(t => t.Item1.Length));
    }

    [Fact]
    public void Folder_OfficeLockFilesAreSkipped()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "real.docx"), Path.Combine(root, "~$real.docx"));

        Assert.Equal(["real.docx"], Names(InputEnumerator.Enumerate([root], All, false), root));
    }

    [Fact]
    public void Folder_MarkPortsOwnStateFolderIsNeverSearched()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "a.docx"), Path.Combine(root, ".markport", "jobs", "x", "work", "copy.docx"));

        Assert.Equal(["a.docx"], Names(InputEnumerator.Enumerate([root], All, recurse: true), root));
    }

    [Fact]
    public void Folder_ResultIsInAFixedOrder()
    {
        using var dir = new TempDir();
        var root = dir.Combine("docs");
        Touch(Path.Combine(root, "b.docx"), Path.Combine(root, "a.docx"), Path.Combine(root, "c.docx"));

        var first = Names(InputEnumerator.Enumerate([root], All, false), root);
        var second = Names(InputEnumerator.Enumerate([root], All, false), root);

        Assert.Equal(["a.docx", "b.docx", "c.docx"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void OneFolder_IsTheInputFolder_ForTheOutputGrouping()
    {
        using var dir = new TempDir();
        var root = dir.Combine("報告書");
        Touch(Path.Combine(root, "a.docx"));

        var result = InputEnumerator.Enumerate([root], All, false);

        Assert.Equal(root, result.InputFolder);
    }

    [Fact]
    public void Files_AreTakenAsGiven_WhateverTheTypes()
    {
        using var dir = new TempDir();
        var file = dir.Combine("in", "a.xlsx");
        Touch(file);

        var result = InputEnumerator.Enumerate([file], [DocumentKind.Docx], recurse: false);

        Assert.Equal([file], result.Inputs.Select(i => i.Path));
        Assert.Null(result.InputFolder);
    }

    [Fact]
    public void ASingleFileOrMixedInputs_HaveNoInputFolder_AndFoldersKeepTheirNameAsAPrefix()
    {
        using var dir = new TempDir();
        var file = dir.Combine("single.docx");
        var folderA = dir.Combine("A");
        var folderB = dir.Combine("B");
        Touch(file, Path.Combine(folderA, "x.docx"), Path.Combine(folderB, "sub", "y.docx"));

        var result = InputEnumerator.Enumerate([file, folderA, folderB], All, recurse: true);

        Assert.Null(result.InputFolder);
        Assert.Equal(
            new[] { ("", "single.docx"), ("A", "x.docx"), (Path.Combine("B", "sub"), "y.docx") }.OrderBy(t => t.Item1, StringComparer.Ordinal),
            result.Inputs.Select(i => (i.RelativeDirectory, Path.GetFileName(i.Path))));
    }

    [Fact]
    public void ProblemsAreReportedAsWarnings_NotErrors()
    {
        using var dir = new TempDir();
        var text = dir.Combine("notes.txt");
        var lockFile = dir.Combine("~$a.docx");
        var good = dir.Combine("good.docx");
        Touch(text, lockFile, good);

        var result = InputEnumerator.Enumerate([text, lockFile, dir.Combine("missing.docx"), good], All, false);

        Assert.Equal([good], result.Inputs.Select(i => i.Path));
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("notes.txt"));
        Assert.Contains(result.Warnings, w => w.Contains("lock file"));
        Assert.Contains(result.Warnings, w => w.Contains("missing.docx") && w.Contains("not found"));
    }

    [Fact]
    public void TheSameFileGivenTwice_IsListedOnce()
    {
        using var dir = new TempDir();
        var file = dir.Combine("a.docx");
        Touch(file);

        Assert.Single(InputEnumerator.Enumerate([file, file.ToUpperInvariant().Replace(dir.Path.ToUpperInvariant(), dir.Path), file], All, false).Inputs);
    }

    [Theory]
    [InlineData(@"D:\in\報告書", "報告書")]
    [InlineData(@"D:\in\報告書\", "報告書")]
    [InlineData(@"D:\", "D")]
    public void FolderName_IsTheLastPartOfThePath(string folder, string expected)
    {
        Assert.Equal(expected, ConvertRequest.FolderName(folder));
    }
}
