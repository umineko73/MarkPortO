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

using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Cli.Tests;

/// <summary>FR-154: where the output of a folder goes, on the command line (PDF: no Office is needed).</summary>
public class FolderLayoutCliTests
{
    private static string[] Convert(params string[] more) =>
        [.. new[] { "convert" }, .. more, "--dpi", "72", "--step2", "off", "--step3", "off"];

    [Fact]
    public async Task ContentsOnly_PutsWhatIsInTheFolderStraightIntoTheOutputFolder_AndTheDefaultMakesTheFolder()
    {
        using var dir = new TempDir();
        var input = dir.Combine("in", "支店A");
        PdfFactory.Create(Path.Combine(input, "top.pdf"), PageSize.A4, "Top text");
        PdfFactory.Create(Path.Combine(input, "第1章", "inner.pdf"), PageSize.A4, "Inner text");

        var contents = await Cli.RunAsync(Convert(input, "--recurse", "--out", dir.Combine("flat"), "--folder-layout", "contents"));
        var folder = await Cli.RunAsync(Convert(input, "--recurse", "--out", dir.Combine("grouped")));

        Assert.Equal(ExitCodes.Success, contents.ExitCode);
        Assert.True(File.Exists(dir.Combine("flat", "top", "top.md")));
        Assert.True(File.Exists(dir.Combine("flat", "第1章", "inner", "inner.md")));
        Assert.False(Directory.Exists(dir.Combine("flat", "支店A")));

        Assert.Equal(ExitCodes.Success, folder.ExitCode);
        Assert.True(File.Exists(dir.Combine("grouped", "支店A", "top", "top.md")));
        Assert.True(File.Exists(dir.Combine("grouped", "支店A", "第1章", "inner", "inner.md")));
    }

    [Fact]
    public async Task ContentsOnly_WithTwoFolders_PutsBothInTheOutputFolder_AndNumbersADocumentOfTheSameName()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "A");
        var b = dir.Combine("in", "B");
        PdfFactory.Create(Path.Combine(a, "report.pdf"), PageSize.A4, "From A");
        PdfFactory.Create(Path.Combine(b, "report.pdf"), PageSize.A4, "From B");
        PdfFactory.Create(Path.Combine(b, "other.pdf"), PageSize.A4, "Other");

        var run = await Cli.RunAsync(Convert(a, b, "--out", dir.Combine("out"), "--folder-layout", "contents"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(dir.Combine("out", "other", "other.md")));
        var reports = new[] { dir.Combine("out", "report", "report.md"), dir.Combine("out", "report (2)", "report.md") };
        Assert.All(reports, path => Assert.True(File.Exists(path), path));
        Assert.Equal(["From A", "From B"], reports.Select(File.ReadAllText).Select(t => t.Contains("From A", StringComparison.Ordinal) ? "From A" : "From B").Order());
        Assert.False(Directory.Exists(dir.Combine("out", "A")));
        Assert.False(Directory.Exists(dir.Combine("out", "B")));
    }

    [Fact]
    public async Task TheSameFolder_UsesThePlacesOfTheSameLayoutOnly()
    {
        using var dir = new TempDir();
        var input = dir.Combine("in", "docs");
        PdfFactory.Create(Path.Combine(input, "a.pdf"), PageSize.A4, "Alpha text");
        var outFolder = dir.Combine("out");

        Assert.Equal(ExitCodes.Success, (await Cli.RunAsync(Convert(input, "--out", outFolder))).ExitCode);
        var grouped = dir.Combine("out", "docs", "a", "a.md");
        var groupedTime = File.GetLastWriteTimeUtc(grouped);
        await Task.Delay(50);

        // The other layout wrote elsewhere: it is a new document, not the one in docs\a.
        var contents = await Cli.RunAsync(Convert(input, "--out", outFolder, "--folder-layout", "contents", "--same-folder", "on"));
        Assert.Equal(ExitCodes.Success, contents.ExitCode);
        Assert.True(File.Exists(dir.Combine("out", "a", "a.md")));
        Assert.Equal(groupedTime, File.GetLastWriteTimeUtc(grouped));

        // The same layout again: into the place of its last conversion.
        var again = await Cli.RunAsync(Convert(input, "--out", outFolder, "--folder-layout", "contents", "--same-folder", "on"));
        Assert.Equal(ExitCodes.Success, again.ExitCode);
        Assert.False(Directory.Exists(dir.Combine("out", "a (2)")));
    }

    [Fact]
    public async Task AWrongLayout_OrALayoutWithResume_IsAnErrorOfTheArguments()
    {
        using var dir = new TempDir();
        var input = dir.Combine("in");
        PdfFactory.Create(Path.Combine(input, "a.pdf"), PageSize.A4, "Alpha text");

        var wrong = await Cli.RunAsync(Convert(input, "--out", dir.Combine("out"), "--folder-layout", "flat"));
        Assert.Equal(ExitCodes.InvalidArguments, wrong.ExitCode);
        Assert.Contains("--folder-layout", wrong.Error, StringComparison.Ordinal);

        var resume = await Cli.RunAsync(Convert("--resume", "--out", dir.Combine("out"), "--folder-layout", "contents"));
        Assert.Equal(ExitCodes.InvalidArguments, resume.ExitCode);
        Assert.Contains("--folder-layout", resume.Error, StringComparison.Ordinal);
    }
}
