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

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class DocxReaderTests
{
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
