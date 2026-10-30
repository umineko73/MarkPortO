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
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Docx;

/// <summary>Text in bold or italic is written between ** or * (FR-101); not when the whole paragraph is, and not in a heading.</summary>
public class EmphasisReadingTests
{
    private static Run R(string text, bool bold = false, bool italic = false, bool strike = false)
    {
        var properties = new RunProperties();
        if (bold)
        {
            properties.Append(new Bold());
        }

        if (italic)
        {
            properties.Append(new Italic());
        }

        if (strike)
        {
            properties.Append(new Strike());
        }

        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static string Create(TempDir dir, params OpenXmlElement[] body)
    {
        var path = dir.Combine("emphasis.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(body));
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "Strong text" }, new StyleRunProperties(new Bold())) { Type = StyleValues.Character, StyleId = "StrongText" },
                new Style(new StyleName { Val = "heading 1" }, new StyleRunProperties(new Bold())) { Type = StyleValues.Paragraph, StyleId = "Heading1" });
        }

        return path;
    }

    private static string[] Lines(string path, bool emphasis) =>
        [.. DocxReader.Read(path, emphasis).Select(b => MarkdownText.ResolveMarks(b.Block.Markdown))];

    [Fact]
    public void BoldAndItalicParts_AreMarked()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("これは"), R("重要", bold: true), R("です。次は"), R("斜体", italic: true), R("と"), R("両方", bold: true, italic: true)));

        Assert.Equal(["これは**重要**です。次は*斜体*と***両方***"], Lines(path, emphasis: true));
    }

    [Fact]
    public void WithTheSettingOff_NothingIsMarked()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("これは"), R("重要", bold: true), R("です")));

        Assert.Equal(["これは重要です"], Lines(path, emphasis: false));
    }

    [Fact]
    public void AParagraphThatIsAllBold_IsNotMarked()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("全体が", bold: true), R("太字です", bold: true)), new Paragraph(R("全体が斜体です", italic: true)));

        Assert.Equal(["全体が太字です", "全体が斜体です"], Lines(path, emphasis: true));
    }

    [Fact]
    public void AParagraphAllBoldButNotItalic_KeepsItsItalicPart()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("太字の", bold: true), R("斜体", bold: true, italic: true), R("です", bold: true)));

        Assert.Equal(["太字の*斜体*です"], Lines(path, emphasis: true));
    }

    [Fact]
    public void ABoldCharacterStyle_IsBold()
    {
        using var dir = new TempDir();
        var styled = new Run(new RunProperties(new RunStyle { Val = "StrongText" }), new Text("強調"));
        var path = Create(dir, new Paragraph(R("これは"), styled, R("です")));

        Assert.Equal(["これは**強調**です"], Lines(path, emphasis: true));
    }

    [Fact]
    public void AHeading_IsNotMarked_AndALineBelowIsNot()
    {
        using var dir = new TempDir();
        var heading = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), R("見出しの"), R("一部", italic: true));
        var path = Create(dir, heading, new Paragraph(R("本文の"), R("一部", bold: true)));

        Assert.Equal(["# 見出しの一部", "本文の**一部**"], Lines(path, emphasis: true));
    }

    [Fact]
    public void ARowOfTheTableThatIsAllBold_IsNotMarked_ButAPartOfACellIs()
    {
        using var dir = new TempDir();
        var table = new Table(
            new TableRow(new TableCell(new Paragraph(R("項目", bold: true))), new TableCell(new Paragraph(R("備考", bold: true)))),
            new TableRow(new TableCell(new Paragraph(R("A"))), new TableCell(new Paragraph(R("一部が"), R("太字", bold: true)))));
        var path = Create(dir, table);

        var markdown = Lines(path, emphasis: true).Single();
        Assert.Contains("| 項目 | 備考 |", markdown);
        Assert.Contains("| A | 一部が**太字** |", markdown);
    }

    [Fact]
    public void BoldWithAStrikethrough_IsWrittenInside()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("前"), R("消した", bold: true, strike: true), R("後")));

        Assert.Equal(["前~~**消した**~~後"], Lines(path, emphasis: true));
    }

    [Fact]
    public void ThePlainTextThatFindsThePage_HasNoMarks()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(R("これは"), R("重要", bold: true), R("です")));

        Assert.Equal("これは重要です", DocxReader.Read(path, emphasis: true).Single().SearchText);
    }
}
