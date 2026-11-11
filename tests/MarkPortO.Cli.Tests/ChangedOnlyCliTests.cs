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

using MarkPortO.Cli;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Cli.Tests;

/// <summary>FR-126: only the files that changed since their last conversion are converted, into the folders of that conversion.</summary>
public class ChangedOnlyCliTests
{
    private static string[] Convert(params string[] more) =>
        [.. new[] { "convert" }, .. more, "--dpi", "72", "--step2", "off", "--step3", "off"];

    [Fact]
    public async Task ChangedOnly_ConvertsTheChangedAndNewFiles_IntoTheirEarlierFolders()
    {
        using var dir = new TempDir();
        var input = dir.Combine("in");
        var a = PdfFactory.Create(Path.Combine(input, "a.pdf"), PageSize.A4, "Alpha text");
        var b = PdfFactory.Create(Path.Combine(input, "b.pdf"), PageSize.A4, "Bravo old", "Bravo second page");
        var outFolder = dir.Combine("out");

        var first = await Cli.RunAsync(Convert(input, "--out", outFolder));
        Assert.Equal(ExitCodes.Success, first.ExitCode);
        var aMarkdown = dir.Combine("out", "in", "a", "a.md");
        var bFolder = dir.Combine("out", "in", "b");
        Assert.True(File.Exists(aMarkdown));
        Assert.True(File.Exists(Path.Combine(bFolder, "images", "page-002.png")));

        // Nothing changed: nothing is converted, and no new folder is made.
        var unchanged = await Cli.RunAsync(Convert(input, "--out", outFolder, "--changed-only"));
        Assert.Equal(ExitCodes.Success, unchanged.ExitCode);
        Assert.Contains("No file has changed", unchanged.Output);
        Assert.False(Directory.Exists(dir.Combine("out", "in (2)")));

        // b changes (one page now) and c is new; a stays as it was.
        var aWritten = File.GetLastWriteTimeUtc(aMarkdown);
        PdfFactory.Create(b, PageSize.A4, "Bravo new");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));
        PdfFactory.Create(Path.Combine(input, "c.pdf"), PageSize.A4, "Charlie text");

        var changed = await Cli.RunAsync(Convert(input, "--out", outFolder, "--changed-only"));
        Assert.Equal(ExitCodes.Success, changed.ExitCode);
        Assert.Contains("2 of 3 files changed", changed.Output);
        Assert.False(Directory.Exists(dir.Combine("out", "in (2)")));
        Assert.False(Directory.Exists(dir.Combine("out", "in", "b (2)")));
        Assert.Contains("Bravo new", File.ReadAllText(Path.Combine(bFolder, "b.md")));
        Assert.False(File.Exists(Path.Combine(bFolder, "images", "page-002.png"))); // the page of the earlier result is gone
        Assert.True(File.Exists(dir.Combine("out", "in", "c", "c.md")));
        Assert.Equal(aWritten, File.GetLastWriteTimeUtc(aMarkdown));
    }

    // FR-129: every file is converted again; with --same-folder into the folders of the earlier conversion, without it into new folders.
    [Fact]
    public async Task SameFolder_ConvertsEveryFile_IntoTheirEarlierFolders()
    {
        using var dir = new TempDir();
        var input = dir.Combine("in");
        PdfFactory.Create(Path.Combine(input, "a.pdf"), PageSize.A4, "Alpha text");
        var b = PdfFactory.Create(Path.Combine(input, "b.pdf"), PageSize.A4, "Bravo old", "Bravo second page");
        var outFolder = dir.Combine("out");

        var first = await Cli.RunAsync(Convert(input, "--out", outFolder));
        Assert.Equal(ExitCodes.Success, first.ExitCode);
        var aMarkdown = dir.Combine("out", "in", "a", "a.md");
        var bFolder = dir.Combine("out", "in", "b");
        var aWritten = File.GetLastWriteTimeUtc(aMarkdown);

        PdfFactory.Create(b, PageSize.A4, "Bravo new");
        PdfFactory.Create(Path.Combine(input, "c.pdf"), PageSize.A4, "Charlie text");
        await Task.Delay(50);

        var same = await Cli.RunAsync(Convert(input, "--out", outFolder, "--same-folder", "on"));
        Assert.Equal(ExitCodes.Success, same.ExitCode);
        Assert.False(Directory.Exists(dir.Combine("out", "in (2)")));
        Assert.False(Directory.Exists(dir.Combine("out", "in", "b (2)")));
        Assert.Contains("Bravo new", File.ReadAllText(Path.Combine(bFolder, "b.md")));
        Assert.False(File.Exists(Path.Combine(bFolder, "images", "page-002.png")));
        Assert.True(File.Exists(dir.Combine("out", "in", "c", "c.md")));
        Assert.True(File.GetLastWriteTimeUtc(aMarkdown) > aWritten); // the file that did not change is converted again too

        // Without the option, the files go to a new folder, as before.
        var fresh = await Cli.RunAsync(Convert(input, "--out", outFolder));
        Assert.Equal(ExitCodes.Success, fresh.ExitCode);
        Assert.True(Directory.Exists(dir.Combine("out", "in (2)")));
    }
}
