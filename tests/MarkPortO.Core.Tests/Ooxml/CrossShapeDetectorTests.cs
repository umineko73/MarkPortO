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

using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class CrossShapeDetectorTests
{
    // ---- the rule (FR-012, research R5) ----

    [Theory]
    [InlineData(0.90, true)]
    [InlineData(1.00, true)]
    [InlineData(0.70, true)]
    [InlineData(0.69, false)]
    [InlineData(0.30, false)]
    public void OneMultiplyShape_IsACross_WhenItCoversAtLeast70PercentInBothDirections(double size, bool expected)
    {
        var verdict = CrossShapeDetector.Evaluate([new ShapeBox("mathMultiply", size, size)]);

        Assert.Equal(expected, verdict.IsCross);
    }

    [Fact]
    public void AMultiplyShapeThatIsWideButNotTall_IsNotACross()
    {
        Assert.False(CrossShapeDetector.Evaluate([new ShapeBox("mathMultiply", 0.95, 0.40)]).IsCross);
        Assert.False(CrossShapeDetector.Evaluate([new ShapeBox("mathMultiply", 0.40, 0.95)]).IsCross);
    }

    [Fact]
    public void TwoDiagonalLinesInOppositeDirections_AreACross()
    {
        var verdict = CrossShapeDetector.Evaluate(
        [
            new ShapeBox("line", 0.9, 0.9, AntiDiagonal: false),
            new ShapeBox("line", 0.9, 0.9, AntiDiagonal: true),
        ]);

        Assert.True(verdict.IsCross);
    }

    [Fact]
    public void ConnectorsCountAsLines()
    {
        var verdict = CrossShapeDetector.Evaluate(
        [
            new ShapeBox("straightConnector1", 0.9, 0.9, false),
            new ShapeBox("straightConnector1", 0.9, 0.9, true),
        ]);

        Assert.True(verdict.IsCross);
    }

    [Fact]
    public void TwoLinesInTheSameDirection_OrASingleLine_AreNotACross()
    {
        Assert.False(CrossShapeDetector.Evaluate([new ShapeBox("line", 0.9, 0.9, false), new ShapeBox("line", 0.9, 0.9, false)]).IsCross);
        Assert.False(CrossShapeDetector.Evaluate([new ShapeBox("line", 0.9, 0.9, true)]).IsCross);
    }

    [Fact]
    public void CrossingLinesThatAreTooSmall_AreNotACross()
    {
        var verdict = CrossShapeDetector.Evaluate([new ShapeBox("line", 0.2, 0.2, false), new ShapeBox("line", 0.2, 0.2, true)]);

        Assert.False(verdict.IsCross);
    }

    [Fact]
    public void ALargeRectangle_ForExampleABackground_IsNotACross()
    {
        Assert.Equal(CrossVerdict.None, CrossShapeDetector.Evaluate([new ShapeBox("rect", 1.0, 1.0)]));
    }

    [Fact]
    public void AFreehandDrawingOverThePage_IsNotACross_ButIsReported()
    {
        var verdict = CrossShapeDetector.Evaluate([new ShapeBox("custGeom", 0.9, 0.9)]);

        Assert.False(verdict.IsCross);
        Assert.True(verdict.HasUnsupportedLargeShape);
    }

    [Fact]
    public void AFreehandDrawing_IsNotReported_WhenThePageIsACrossAnyway()
    {
        var verdict = CrossShapeDetector.Evaluate([new ShapeBox("custGeom", 0.9, 0.9), new ShapeBox("mathMultiply", 0.9, 0.9)]);

        Assert.True(verdict.IsCross);
        Assert.False(verdict.HasUnsupportedLargeShape);
    }

    [Fact]
    public void ASmallFreehandDrawing_IsIgnored()
    {
        Assert.Equal(CrossVerdict.None, CrossShapeDetector.Evaluate([new ShapeBox("custGeom", 0.2, 0.2)]));
    }

    [Fact]
    public void NoShapes_IsNotACross()
    {
        Assert.Equal(CrossVerdict.None, CrossShapeDetector.Evaluate([]));
    }

    // ---- reading the shapes from the files ----

    [Fact]
    public void Pptx_FindsACrossMadeOfOneShape_OfTwoLines_AndOfAGroupOfLines()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deck.pptx");
        PptxFactory.Create(
            path,
            new SlideSpec("plain", ["text"]),
            new SlideSpec("one shape", Shapes: ShapeSpec.CrossShape()),
            new SlideSpec("two lines", Shapes: ShapeSpec.CrossLines()),
            new SlideSpec("grouped", Group: new ShapeGroupSpec(0.05, 0.05, 0.9, 0.9, ShapeSpec.CrossLines(1.0))),
            new SlideSpec("small", Shapes: [new ShapeSpec("mathMultiply", 0.1, 0.1, 0.2, 0.2)]),
            new SlideSpec("one line", Shapes: [new ShapeSpec("line", 0.05, 0.05, 0.9, 0.9)]),
            new SlideSpec("freehand", Shapes: [new ShapeSpec("custGeom", 0.05, 0.05, 0.9, 0.9)]));

        var verdicts = PptxCrossExtractor.Evaluate(path);

        Assert.Equal([false, true, true, true, false, false, false], Enumerable.Range(1, 7).Select(n => verdicts[n].IsCross));
        Assert.True(verdicts[7].HasUnsupportedLargeShape);
        Assert.False(verdicts[1].HasUnsupportedLargeShape);
    }

    [Fact]
    public void Pptx_AGroupIsMeasuredByItsSize_NotByTheSizeOfItsChildren()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deck.pptx");
        // The lines fill their group, but the group is only 30% of the slide.
        PptxFactory.Create(path, new SlideSpec("small group", Group: new ShapeGroupSpec(0.1, 0.1, 0.3, 0.3, ShapeSpec.CrossLines(1.0))));

        Assert.False(PptxCrossExtractor.Evaluate(path)[1].IsCross);
    }

    [Fact]
    public void Pptx_NumbersTheSlidesLikeThePresentation_IncludingHiddenOnes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deck.pptx");
        PptxFactory.Create(
            path,
            new SlideSpec("one"),
            new SlideSpec("two (hidden, crossed)", Hidden: true, Shapes: ShapeSpec.CrossShape()),
            new SlideSpec("three"));

        var verdicts = PptxCrossExtractor.Evaluate(path);

        Assert.Equal([false, true, false], [verdicts[1].IsCross, verdicts[2].IsCross, verdicts[3].IsCross]);
    }

    [Fact]
    public void Xlsx_FindsACrossOverTheWholeUsedRange_ButNotOverAPart()
    {
        using var dir = new TempDir();
        var path = dir.Combine("book.xlsx");
        var data = Enumerable.Range(1, 10).Select(r => new[] { $"a{r}", $"b{r}", $"c{r}", $"d{r}" }).ToArray();
        XlsxFactory.Create(
            path,
            new SheetSpec("crossed", data, Shapes: [new CellShapeSpec("mathMultiply", 0, 0, 4, 10)]),
            new SheetSpec("lines", data, Shapes: [new CellShapeSpec("line", 0, 0, 4, 10), new CellShapeSpec("line", 0, 0, 4, 10, FlipV: true)]),
            new SheetSpec("corner", data, Shapes: [new CellShapeSpec("mathMultiply", 0, 0, 1, 2)]),
            new SheetSpec("plain", data));
        var sheets = XlsxReader.Read(path, includeHidden: false);

        var verdicts = XlsxCrossExtractor.Evaluate(path, sheets);

        Assert.True(verdicts["crossed"].IsCross);
        Assert.True(verdicts["lines"].IsCross);
        Assert.False(verdicts["corner"].IsCross);
        Assert.False(verdicts.ContainsKey("plain"));
    }

    [Fact]
    public void Xlsx_ACrossOver70PercentOfTheUsedRange_Counts_AndOver50PercentDoesNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("book.xlsx");
        var data = Enumerable.Range(1, 10).Select(_ => new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" }).ToArray();
        XlsxFactory.Create(
            path,
            new SheetSpec("seven", data, Shapes: [new CellShapeSpec("mathMultiply", 0, 0, 7, 7)]),
            new SheetSpec("five", data, Shapes: [new CellShapeSpec("mathMultiply", 0, 0, 5, 5)]));
        var sheets = XlsxReader.Read(path, false);

        var verdicts = XlsxCrossExtractor.Evaluate(path, sheets);

        Assert.True(verdicts["seven"].IsCross);
        Assert.False(verdicts["five"].IsCross);
    }

    [Fact]
    public void Docx_ReadsTheShapesOfTheBody_WithTheirHostBlock_AndIgnoresTheHeader()
    {
        using var dir = new TempDir();
        var path = dir.Combine("doc.docx");
        DocxFactory.CreateWithShapes(
            path,
            [
                new DocPage(["page one text"]),
                new DocPage(["page two text", "more text"], DocShape.CrossShape()),
                new DocPage(["page three text"], DocShape.CrossLines()),
                new DocPage(["page four text"], [new DocShape("mathMultiply", 0.2, 0.2)]),
            ],
            headerShapes: DocShape.CrossShape());

        var content = DocxReader.ReadContent(path);

        // The shapes of pages 2, 3 and 4 are anchored in the first paragraph of those pages: blocks 1, 3 and 4.
        Assert.Equal([1, 3, 4], content.Shapes.Select(s => s.HostBlockIndex));
        Assert.True(CrossShapeDetector.Evaluate(content.Shapes[0].Boxes).IsCross);
        Assert.True(CrossShapeDetector.Evaluate(content.Shapes[1].Boxes).IsCross);
        Assert.False(CrossShapeDetector.Evaluate(content.Shapes[2].Boxes).IsCross);
        Assert.Equal(5, content.Blocks.Count);
    }

    [Fact]
    public void Docx_ADocumentWithoutShapes_HasNone()
    {
        using var dir = new TempDir();
        var path = dir.Combine("doc.docx");
        DocxFactory.CreatePaged(path, ["a"], ["b"]);

        Assert.Empty(DocxReader.ReadContent(path).Shapes);
    }
}
