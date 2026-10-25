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

using MarkPort.Core.Jobs;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Jobs;

public class OutputNamerTests
{
    [Theory]
    [InlineData("報告書", "報告書")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("a/b\\c", "a_b_c")]
    [InlineData("name. ", "name")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("", "_")]
    [InlineData("...", "_")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("Console", "Console")]
    public void Sanitize_MakesAValidWindowsName(string input, string expected)
    {
        Assert.Equal(expected, OutputNamer.Sanitize(input));
    }

    [Fact]
    public void Allocate_UsesTheNameWhenFree()
    {
        using var dir = new TempDir();
        var allocator = new OutputNamer.Allocator();

        Assert.Equal(dir.Combine("報告書"), allocator.Allocate(dir.Path, "報告書"));
    }

    [Fact]
    public void Allocate_AddsNumberWhenFolderExists_AndNeverChangesTheExistingOne()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("報告書"));
        File.WriteAllText(dir.Combine("報告書", "keep.txt"), "x");

        var first = new OutputNamer.Allocator().Allocate(dir.Path, "報告書");
        Assert.Equal(dir.Combine("報告書 (2)"), first);

        Directory.CreateDirectory(first);
        var second = new OutputNamer.Allocator().Allocate(dir.Path, "報告書");
        Assert.Equal(dir.Combine("報告書 (3)"), second);

        Assert.Equal("x", File.ReadAllText(dir.Combine("報告書", "keep.txt")));
    }

    [Fact]
    public void Allocate_SameNameTwiceInOneRun_GivesDifferentNames_ForADocxAndAnXlsxOfTheSameName()
    {
        using var dir = new TempDir();
        var allocator = new OutputNamer.Allocator();

        var forDocx = allocator.Allocate(dir.Path, "a");
        var forXlsx = allocator.Allocate(dir.Path, "a");

        Assert.Equal(dir.Combine("a"), forDocx);
        Assert.Equal(dir.Combine("a (2)"), forXlsx);
    }

    [Fact]
    public void Allocate_TreatsDifferentCaseAsTheSameName()
    {
        using var dir = new TempDir();
        var allocator = new OutputNamer.Allocator();

        allocator.Allocate(dir.Path, "Report");

        Assert.Equal(dir.Combine("report (2)"), allocator.Allocate(dir.Path, "report"));
    }

    [Fact]
    public void Allocate_SkipsNamesTakenByFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("a"), "a file");

        Assert.Equal(dir.Combine("a (2)"), new OutputNamer.Allocator().Allocate(dir.Path, "a"));
    }

    [Fact]
    public void Allocate_ShortensVeryLongNames_SoTheFullPathStaysBelowTheLimit()
    {
        using var dir = new TempDir();
        var longName = new string('あ', 300);

        var result = new OutputNamer.Allocator().Allocate(dir.Path, longName);

        // Room for \images\page-0000.png and the Markdown file name must remain.
        Assert.True(result.Length + OutputNamer.ReservedBelowFolder <= OutputNamer.MaxFullPathLength + 10,
            $"path too long: {result.Length}");
        Assert.StartsWith(dir.Path, result);
    }

    [Fact]
    public void Allocate_ShortenedNamesStillGetDistinctNumbers()
    {
        using var dir = new TempDir();
        var allocator = new OutputNamer.Allocator();
        var longName = new string('x', 400);

        var a = allocator.Allocate(dir.Path, longName);
        var b = allocator.Allocate(dir.Path, longName + "y");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Allocate_UsesTheInjectedExistenceCheck()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"X:\out\a", @"X:\out\a (2)" };
        var allocator = new OutputNamer.Allocator(taken.Contains);

        Assert.Equal(@"X:\out\a (3)", allocator.Allocate(@"X:\out", "a"));
    }
}
