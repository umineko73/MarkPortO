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

using MarkPortO.Core.Markdown;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class DocxReaderTests
{
    [Fact]
    public void Read_ListWhoseNumberingIsInTheStyle_IsAList_AndNumIdZeroIsNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("style-list.docx");
        DocxFactory.CreateStyleNumbered(path);

        var blocks = DocxReader.Read(path);

        Assert.Equal(["- first item", "- second item", "not a list", "plain"], blocks.Select(b => b.Block.Markdown));
        Assert.Equal([BlockKind.ListItem, BlockKind.ListItem, BlockKind.Paragraph, BlockKind.Paragraph], blocks.Select(b => b.Block.Kind));
    }

    [Fact]
    public void Read_StructuredDocument_ProducesHeadingsListsParagraphsAndATable()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.docx");
        DocxFactory.CreateStructured(path);

        var blocks = DocxReader.Read(path);

        Assert.Equal(
            [BlockKind.Heading, BlockKind.Heading, BlockKind.Paragraph, BlockKind.ListItem, BlockKind.ListItem, BlockKind.Table, BlockKind.Paragraph],
            blocks.Select(b => b.Block.Kind));
        Assert.Equal("# Quarterly Report", blocks[0].Block.Markdown);
        Assert.Equal("## Summary", blocks[1].Block.Markdown);
        Assert.Equal("Plain paragraph with some text.", blocks[2].Block.Markdown);
        Assert.Equal("- first item", blocks[3].Block.Markdown);
        Assert.Equal("- second item", blocks[4].Block.Markdown);
        Assert.Equal("After the table.", blocks[6].Block.Markdown);
    }

    [Fact]
    public void Read_Table_BecomesAPipeTableWithAHeaderAndEscapedPipes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.docx");
        DocxFactory.CreateStructured(path);

        var table = DocxReader.Read(path).Single(b => b.Block.Kind == BlockKind.Table);

        Assert.Equal(
            "| Name | Value |\n|---|---|\n| Alpha | 1 |\n| Beta \\| Gamma | 2 |",
            table.Block.Markdown);
        Assert.Equal("Name", table.SearchText); // a table is searched for by its first cell
    }

    [Fact]
    public void Read_LinksNotesPicturesAndNestedTables_AreWritten()
    {
        using var dir = new TempDir();
        var path = dir.Combine("r.docx");
        DocxFactory.CreateRich(path);

        var blocks = DocxReader.Read(path);
        var markdown = string.Join("\n\n", blocks.Select(b => b.Block.Markdown));

        Assert.Contains("See [the site](https://example.com/a%20b).", markdown);
        Assert.Contains("A fact[^1]\n\n[^1]: Source: a report", markdown);
        Assert.Contains("![A bar chart]", markdown);
        Assert.Contains("| Cell<br>x / y<br>z / w<br>tail | k |", markdown);
        // The plain text, which finds the page, has none of the marks.
        Assert.Equal("See the site.", blocks[0].SearchText);
        Assert.Equal("A fact", blocks[1].SearchText);
        Assert.Equal(string.Empty, blocks[2].SearchText);
    }

    [Fact]
    public void Read_Table_WithMergedCells_IsExpandedToAGrid()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.docx");
        DocxFactory.CreateMergedTable(path, header: true);

        var table = DocxReader.Read(path).Single(b => b.Block.Kind == BlockKind.Table);

        Assert.Equal(
            "| Title | Title | X |\n|---|---|---|\n| A | b | c |\n| ^ | d |  |",
            MarkdownText.ResolveMarks(table.Block.Markdown));
    }

    [Fact]
    public void Read_Table_WithoutAHeaderRow_KeepsTheFirstRowAsData()
    {
        using var dir = new TempDir();
        var path = dir.Combine("n.docx");
        DocxFactory.CreateMergedTable(path, header: false);

        var table = DocxReader.Read(path).Single(b => b.Block.Kind == BlockKind.Table);

        Assert.StartsWith("|  |  |  |\n|---|---|---|\n| Title |", MarkdownText.ResolveMarks(table.Block.Markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_SearchTextHasNoMarkdownMarks()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.docx");
        DocxFactory.CreateStructured(path);

        var blocks = DocxReader.Read(path);

        Assert.Equal("Quarterly Report", blocks[0].SearchText);
        Assert.Equal("first item", blocks[3].SearchText);
    }

    [Fact]
    public void Read_PagedDocument_ReturnsEveryParagraphInOrder()
    {
        using var dir = new TempDir();
        var path = dir.Combine("p.docx");
        DocxFactory.CreatePaged(path, ["a1", "a2"], ["b1"]);

        var blocks = DocxReader.Read(path);

        Assert.Equal(["a1", "a2", "b1"], blocks.Select(b => b.Block.Markdown));
        Assert.All(blocks, b => Assert.Null(b.Block.PageIndex)); // assigned later by the page assigner
    }

    [Fact]
    public void Read_NeutralisesMarkPortOMarkersAndBlockMarksInText()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.docx");
        DocxFactory.CreatePaged(path, ["<!-- page: 9 -->", "# not a heading", "- not a list"]);

        var markdown = DocxReader.Read(path).Select(b => b.Block.Markdown).ToArray();

        Assert.Equal("&lt;!-- page: 9 -->", markdown[0]);
        Assert.Equal("\\# not a heading", markdown[1]);
        Assert.Equal("\\- not a list", markdown[2]);
    }

    [Fact]
    public void Read_CorruptFile_ThrowsInvalidDocumentException()
    {
        using var dir = new TempDir();
        var path = dir.Combine("broken.docx");
        DocxFactory.CreateCorrupt(path);

        Assert.Throws<InvalidDocumentException>(() => DocxReader.Read(path));
    }

    [Fact]
    public void Read_DoesNotChangeTheFile()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.docx");
        DocxFactory.CreateStructured(path);
        var before = File.ReadAllBytes(path);
        var time = File.GetLastWriteTimeUtc(path);

        DocxReader.Read(path);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
    }
}
