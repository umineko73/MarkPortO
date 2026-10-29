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

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Docx;

/// <summary>FR-077 (the automatic numbers of the headings) and FR-078 (a table that runs over several pages).</summary>
public class TableAndNumberingTests
{
    private static DocxBlock TableBlock(string[][] rows, bool header = true, bool repeats = true) =>
        new(
            new Block { Kind = BlockKind.Table, Markdown = "table" },
            rows[0][0],
            new DocxTable([.. rows.Select(r => (IReadOnlyList<string>)r)], [.. rows.Select(r => (IReadOnlyList<string>)r)], header, repeats));

    [Fact]
    public void Assign_ATableWhoseFirstCellIsTooShort_IsPlacedByItsFirstRow()
    {
        // "No" has two letters: it is not looked for alone, so the first row as a whole is.
        DocxBlock[] blocks = [new(new Block { Kind = BlockKind.Paragraph }, "Introduction of the report"), TableBlock([["No", "Item", "Amount"], ["1", "Pencil", "100"]])];

        DocxPageAssigner.Assign(blocks, ["Introduction of the report", "No Item Amount 1 Pencil 100"]);

        Assert.Equal([1, 2], blocks.Select(b => b.Block.PageIndex).ToArray());
    }

    [Fact]
    public void Assign_TheTextOfAChartThatThePdfListsAfterThePageText_DoesNotMoveTheSearchForTheNextBlocks()
    {
        // A PDF lists the text of a chart, a SmartArt or a text box after the text of the page.
        var chart = new DocxBlock(new Block { Kind = BlockKind.Paragraph }, "Chart title here") { Floating = true };
        DocxBlock[] blocks =
        [
            new(new Block { Kind = BlockKind.Paragraph }, "First paragraph of page one"),
            chart,
            new(new Block { Kind = BlockKind.Paragraph }, "Second paragraph of page one"),
            new(new Block { Kind = BlockKind.Paragraph }, "Paragraph of page two"),
        ];

        var warnings = DocxPageAssigner.Assign(blocks, ["First paragraph of page one Second paragraph of page one Chart title here", "Paragraph of page two"]);

        Assert.Equal([1, 1, 1, 2], blocks.Select(b => b.Block.PageIndex).ToArray());
        Assert.Empty(warnings);
    }

    [Fact]
    public void Assign_TheRowsOfALongTable_GetThePageTheyAreOn()
    {
        var table = TableBlock([["No", "Item", "Amount"], ["1", "Pencil", "100"], ["2", "Eraser", "200"], ["3", "Ruler", "300"], ["4", "Compass", "400"]]);
        DocxBlock[] blocks = [new(new Block { Kind = BlockKind.Paragraph }, "The table below"), table];

        DocxPageAssigner.Assign(blocks, ["The table below No Item Amount 1 Pencil 100 2 Eraser 200", "No Item Amount 3 Ruler 300 4 Compass 400"]);

        Assert.Equal([1, 1, 1, 2, 2], table.RowPages);
    }

    [Fact]
    public void ExpandTables_CutsATableAtThePageBreaks_AndRepeatsTheHeaderWhenWordDoes()
    {
        var table = TableBlock([["No", "Item"], ["1", "Pencil"], ["2", "Eraser"], ["3", "Ruler"]]);
        table.Block.PageIndex = 1;
        table.RowPages = [1, 1, 2, 2];

        var parts = DocxPageAssigner.ExpandTables([table]);

        Assert.Equal([1, 2], parts.Select(p => p.Block.PageIndex).ToArray());
        Assert.Equal("| No | Item |\n|---|---|\n| 1 | Pencil |", parts[0].Block.Markdown);
        Assert.Equal("| No | Item |\n|---|---|\n| 2 | Eraser |\n| 3 | Ruler |", parts[1].Block.Markdown);
    }

    [Fact]
    public void ExpandTables_WithoutARepeatedHeader_TheLaterPartsHaveAnEmptyHeader()
    {
        var table = TableBlock([["No", "Item"], ["1", "Pencil"], ["2", "Eraser"]], header: true, repeats: false);
        table.Block.PageIndex = 1;
        table.RowPages = [1, 1, 2];

        var parts = DocxPageAssigner.ExpandTables([table]);

        Assert.Equal("|  |  |\n|---|---|\n| 2 | Eraser |", parts[1].Block.Markdown);
    }

    [Fact]
    public void ExpandTables_ATableOnOnePage_StaysAsItIs()
    {
        var table = TableBlock([["a", "b"], ["c", "d"]]);
        table.RowPages = [2, 2];

        var parts = DocxPageAssigner.ExpandTables([table]);

        Assert.Same(table, Assert.Single(parts));
    }

    [Fact]
    public void Read_TheNumberOfAHeadingThatIsLinkedToAList_IsWrittenInFrontOfIt()
    {
        using var dir = new TempDir();
        var path = dir.Combine("numbered.docx");
        CreateNumbered(path);

        var headings = DocxReader.Read(path).Select(b => b.Block).Where(b => b.Kind == BlockKind.Heading).Select(b => b.Markdown).ToArray();

        Assert.Equal(["# 1. Overview", "## 1.1 Purpose", "## 1.2 Scope", "# 2. Data", "## 2.1 Table", "# 第一章 Appendix"], headings);
    }

    // A multilevel list linked to Heading 1 and 2 by their styles (the numbers are not in the paragraphs), and a heading
    // that has its own list "第%1章" counted in Japanese.
    private static void CreateNumbered(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();

        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new Style(new StyleName { Val = "heading 1" }, new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 1 }))) { Type = StyleValues.Paragraph, StyleId = "Heading1" },
            new Style(new StyleName { Val = "heading 2" }, new StyleParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 1 }, new NumberingId { Val = 1 }))) { Type = StyleValues.Paragraph, StyleId = "Heading2" });

        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new ParagraphStyleIdInLevel { Val = "Heading1" }, new LevelText { Val = "%1." }) { LevelIndex = 0 },
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.Decimal }, new ParagraphStyleIdInLevel { Val = "Heading2" }, new LevelText { Val = "%1.%2" }) { LevelIndex = 1 }) { AbstractNumberId = 0 },
            new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.JapaneseCounting }, new LevelText { Val = "第%1章" }) { LevelIndex = 0 }) { AbstractNumberId = 1 },
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 });

        static Paragraph Heading(string style, string text) =>
            new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text)));

        var appendix = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }, new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 2 })),
            new Run(new Text("Appendix")));
        main.Document = new Document(new Body(
            Heading("Heading1", "Overview"),
            new Paragraph(new Run(new Text("Some text of the overview."))),
            Heading("Heading2", "Purpose"),
            Heading("Heading2", "Scope"),
            Heading("Heading1", "Data"),
            Heading("Heading2", "Table"),
            appendix));
    }
}
