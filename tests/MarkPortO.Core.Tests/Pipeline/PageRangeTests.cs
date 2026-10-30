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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>The page range of a run (FR-097).</summary>
public class PageRangeTests
{
    [Theory]
    [InlineData("3", new[] { 3 }, new[] { 1, 2, 4 })]
    [InlineData("3-5,8", new[] { 3, 4, 5, 8 }, new[] { 2, 6, 7, 9 })]
    [InlineData("8-", new[] { 8, 9, 100 }, new[] { 1, 7 })]
    [InlineData("-4", new[] { 1, 4 }, new[] { 5, 6 })]
    [InlineData(" 1 , 3 - 4 ", new[] { 1, 3, 4 }, new[] { 2, 5 })]
    [InlineData("１，３－４", new[] { 1, 3, 4 }, new[] { 2, 5 })]
    public void Parse_ReadsThePages(string text, int[] inside, int[] outside)
    {
        var range = PageRange.Parse(text)!;
        Assert.All(inside, p => Assert.True(range.Contains(p), $"page {p}"));
        Assert.All(outside, p => Assert.False(range.Contains(p), $"page {p}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_MeansEveryPage(string? text) => Assert.Null(PageRange.Parse(text));

    [Theory]
    [InlineData("0")]
    [InlineData("-")]
    [InlineData("5-2")]
    [InlineData("a")]
    [InlineData("1,,2")]
    [InlineData("1-2-3")]
    [InlineData("-3-")]
    public void Bad_Text_IsRefused(string text) => Assert.Throws<FormatException>(() => PageRange.Parse(text));

    [Fact]
    public void Highest_IsTheLastPageNamed()
    {
        Assert.Equal(8, PageRange.Parse("1,3-5,8")!.Highest);
        Assert.Equal(8, PageRange.Parse("8-")!.Highest);
    }

    [Fact]
    public async Task ARangeForMoreThanOneFile_StopsBeforeAnythingIsWritten()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "a.pdf");
        var b = dir.Combine("in", "b.pdf");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [1]);
        var options = Options();
        options.Pages = "1";
        var request = Request(dir.Combine("out"), options, a, b);

        using var engine = Engine(dir);
        await Assert.ThrowsAsync<ConvertStartException>(() => new ConvertService(engine).RunAsync(request));
        Assert.False(Directory.Exists(dir.Combine("out")));
    }

    [Fact]
    public async Task ABadRange_StopsBeforeAnythingIsWritten()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "a.pdf");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllBytes(a, [1]);
        var options = Options();
        options.Pages = "5-2";

        using var engine = Engine(dir);
        var error = await Assert.ThrowsAsync<ConvertStartException>(() => new ConvertService(engine).RunAsync(Request(dir.Combine("out"), options, a)));
        Assert.Contains("5-2", error.Message);
        Assert.False(Directory.Exists(dir.Combine("out")));
    }
}
