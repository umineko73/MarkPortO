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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-012 through the whole conversion: a page with a "x" over it keeps its image but not its text.</summary>
[Trait("Engine", "LibreOffice")]
public class DeletedPageRenderingTests
{
    [LibreOfficeFact]
    public async Task Docx_ADeletedPage_KeepsItsImage_AndTheMarkdownHasOnlyTheNotice()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "report.docx");
        DocxFactory.CreateWithShapes(
            source,
            [
                new DocPage(["Page one text"]),
                new DocPage(["Page two secret text"], DocShape.CrossShape()),
                new DocPage(["Page three text"]),
            ]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal([false, true, false], document.Pages.Select(p => p.IsDeleted));
        Assert.Equal(PageStep3Status.Skipped, document.Pages[1].Step3);

        // SC-002: every page has an image, the deleted one included.
        Assert.Equal(3, ImageFiles(document.OutputFolder).Length);
        Assert.True(File.Exists(Path.Combine(document.OutputFolder, "images", "page-002.png")));

        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "report.md"));
        Assert.Contains("Page one text", markdown);
        Assert.Contains("Page three text", markdown);
        Assert.DoesNotContain("Page two secret text", markdown);
        Assert.Contains("> **[削除ページ]** 元の文書の2ページ目は、全面に×印が付けられていたため除外しました。画像は `images/page-002.png` に保存しています。", markdown);
        Assert.DoesNotContain("![ページ 2]", markdown);
        Assert.Contains("![ページ 1](images/page-001.png)", markdown);
        Assert.Contains("![ページ 3](images/page-003.png)", markdown);
    }

    [LibreOfficeFact]
    public async Task Docx_ShapesInTheHeader_DoNotDeleteEveryPage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "header.docx");
        DocxFactory.CreateWithShapes(
            source,
            [new DocPage(["Page one text"]), new DocPage(["Page two text"])],
            headerShapes: DocShape.CrossShape());

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.All(document.Pages, p => Assert.False(p.IsDeleted));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "header.md"));
        Assert.Contains("Page one text", markdown);
        Assert.Contains("Page two text", markdown);
    }

    [LibreOfficeFact]
    public async Task Docx_ASmallMarkOnAPage_IsNotADeletedPage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "small.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["Page one text"], [new DocShape("mathMultiply", 0.1, 0.1)])]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.False(Assert.Single(result.Documents).Pages[0].IsDeleted);
    }

    [LibreOfficeFact]
    public async Task Pptx_ADeletedSlide_IsHandledLikeADeletedPage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(
            source,
            new SlideSpec("Kept slide", ["kept body"]),
            new SlideSpec("Deleted slide", ["deleted body"], Shapes: ShapeSpec.CrossLines()),
            new SlideSpec("Last slide", ["last body"]));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.Equal([false, true, false], document.Pages.Select(p => p.IsDeleted));
        Assert.Equal(3, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "deck.md"));
        Assert.Contains("kept body", markdown);
        Assert.Contains("last body", markdown);
        Assert.DoesNotContain("deleted body", markdown);
        Assert.DoesNotContain("Deleted slide", markdown);
        Assert.Contains("> **[削除ページ]** 元の文書の2枚目のスライドは", markdown);
    }

    [LibreOfficeFact]
    public async Task Xlsx_ACrossedSheet_IsDeletedInAllItsParts()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "book.xlsx");
        var wide = XlsxFactory.WideSheet("Crossed", columns: 160, rows: 4, columnWidthChars: 14) with
        {
            Shapes = [new CellShapeSpec("mathMultiply", 0, 0, 160, 5)],
        };
        XlsxFactory.Create(source, new SheetSpec("Kept", [["kept-cell"]]), wide);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.Equal(4, document.Pages.Count); // Kept (1) + Crossed in 3 parts
        Assert.Equal([false, true, true, true], document.Pages.Select(p => p.IsDeleted));
        Assert.Equal(4, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "book.md"));
        Assert.Contains("kept-cell", markdown);
        Assert.DoesNotContain("r1c1", markdown); // none of the crossed sheet's cells
        Assert.Equal(3, markdown.Split("> **[削除ページ]** 元の文書のシート「Crossed」").Length - 1);
    }

    [LibreOfficeFact]
    public async Task AFreehandDrawingOverThePage_IsConvertedAsUsual_WithAWarning()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(source, new SlideSpec("Freehand slide", ["body text"], Shapes: [new ShapeSpec("custGeom", 0.05, 0.05, 0.9, 0.9)]));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.False(document.Pages[0].IsDeleted);
        Assert.Contains("ShapeUnsupported", document.Warnings.Select(w => w.Code));
        Assert.Contains("body text", File.ReadAllText(Path.Combine(document.OutputFolder, "deck.md")));
    }

    [LibreOfficeFact]
    public async Task ADeletedPageIsListedInTheResult()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(source, new SlideSpec("a"), new SlideSpec("b", Shapes: ShapeSpec.CrossShape()));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var json = MarkPortO.Core.Convert.ConvertResultJson.Serialize(result);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var deleted = parsed.RootElement.GetProperty("documents")[0].GetProperty("deletedPages");
        Assert.Equal([2], deleted.EnumerateArray().Select(e => e.GetInt32()));
    }
}
