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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Tests.Fixtures;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Convert;

[Trait("Engine", "LibreOffice")]
public class HiddenContentTests
{
    [LibreOfficeFact]
    public async Task HiddenSheets_AreLeftOutByDefault_NoImageNoTextAndNoPageCount()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Shown", [["shown-cell"]]), new SheetSpec("Secret", [["secret-cell"]], Hidden: true));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.Single(document.Pages);
        Assert.Single(ImageFiles(document.OutputFolder));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "book.md"));
        Assert.Contains("shown-cell", markdown);
        Assert.DoesNotContain("secret-cell", markdown);
        Assert.DoesNotContain("Secret", markdown.Replace("`", string.Empty, StringComparison.Ordinal).Split("<!-- page:", 2)[1]);
    }

    [LibreOfficeFact]
    public async Task HiddenSheets_AreConvertedLikeTheOthers_WhenIncluded()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Shown", [["shown-cell"]]), new SheetSpec("Secret", [["secret-cell"]], Hidden: true));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(includeHidden: true), source));

        var document = Assert.Single(result.Documents);
        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(2, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "book.md"));
        Assert.Contains("shown-cell", markdown);
        Assert.Contains("secret-cell", markdown);
        Assert.Contains("<!-- sheet: Secret part=1/1 columns=A:A -->", markdown);
    }

    [LibreOfficeFact]
    public async Task HiddenSlides_AreLeftOutByDefault()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(source, new SlideSpec("Shown slide", ["shown"]), new SlideSpec("Hidden slide", ["secret"], Hidden: true));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.Single(document.Pages);
        Assert.Single(ImageFiles(document.OutputFolder));
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(document.OutputFolder, "deck.md")));
    }

    [LibreOfficeFact]
    public async Task HiddenSlides_AreConvertedLikeTheOthers_WhenIncluded_AndTheOriginalStaysHidden()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(source, new SlideSpec("Shown slide", ["shown"]), new SlideSpec("Hidden slide", ["secret"], Hidden: true), new SlideSpec("Last slide"));
        var before = File.ReadAllBytes(source);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(includeHidden: true), source));

        var document = Assert.Single(result.Documents);
        Assert.Equal(3, document.Pages.Count);
        Assert.Equal(3, ImageFiles(document.OutputFolder).Length);
        Assert.Equal(["slide:1", "slide:2", "slide:3"], document.Pages.Select(p => p.SourceLabel));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "deck.md"));
        Assert.Contains("secret", markdown);
        Assert.Equal(before, File.ReadAllBytes(source)); // FR-010a: the copy is changed, never the original
    }
}
