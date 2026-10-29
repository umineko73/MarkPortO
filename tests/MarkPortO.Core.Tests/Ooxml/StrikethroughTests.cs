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
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-092: text with a strikethrough is written between <c>~~</c>.</summary>
public class StrikethroughTests
{
    [Theory]
    [InlineData("deleted", "~~deleted~~")]
    [InlineData(" deleted ", " ~~deleted~~ ")]
    [InlineData("   ", "   ")]
    public void Wrap_PutsTheTextBetweenTildes_AndKeepsTheSpacesOutside(string text, string expected) =>
        Assert.Equal(expected, Strikethrough.Wrap(text));

    [Fact]
    public void Merge_JoinsTwoNeighbouringStrikethroughs() =>
        Assert.Equal("~~ab~~ cd", Strikethrough.Merge("~~a~~~~b~~ cd"));

    // ---- docx

    private static string[] ReadParagraphs(string path) => [.. DocxReader.Read(path).Select(b => b.Block.Markdown)];

    private static Run Struck(string text, OpenXmlElement? strike = null) =>
        new(new RunProperties(strike ?? new Strike()), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run Plain(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    [Fact]
    public void Docx_ARunWithAStrikethrough_IsWrittenBetweenTildes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("strike.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "Crossed" }, new StyleRunProperties(new Strike())) { Type = StyleValues.Character, StyleId = "Crossed" });
            main.Document = new Document(new Body(
                new Paragraph(Plain("The price is "), Struck("100 yen"), Plain(" now 80 yen.")),
                new Paragraph(Struck("one "), Struck("two", new DoubleStrike()), Plain(" three")),
                new Paragraph(Plain("Not struck: "), new Run(new RunProperties(new Strike { Val = false }), new Text("still plain"))),
                new Paragraph(Plain("By a style: "), new Run(new RunProperties(new RunStyle { Val = "Crossed" }), new Text("crossed by its style"))),
                new Paragraph(Struck(" spaces stay outside "))));
        }

        var markdown = ReadParagraphs(path);

        Assert.Equal("The price is ~~100 yen~~ now 80 yen.", markdown[0]);
        Assert.Equal("~~one two~~ three", markdown[1]); // two neighbouring runs are one strikethrough
        Assert.Equal("Not struck: still plain", markdown[2]);
        Assert.Equal("By a style: ~~crossed by its style~~", markdown[3]);
        Assert.Equal("~~spaces stay outside~~", markdown[4]); // the paragraph is trimmed, the spaces are not inside the tildes
    }

    [Fact]
    public void Docx_TheTextThatPagesAreFoundBy_HasNoTildes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("strike2.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            document.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(Plain("before "), Struck("crossed out"), Plain(" after"))));
        }

        var block = Assert.Single(DocxReader.Read(path));

        Assert.Equal("before crossed out after", block.SearchText);
        Assert.False(block.Splittable); // a paragraph with marks is not cut at a page break
    }

    // ---- pptx

    [Fact]
    public void Pptx_ARunWithAStrikethrough_IsWrittenBetweenTildes()
    {
        using var dir = new TempDir();
        var path = dir.Combine("strike.pptx");
        PptxFactory.Create(path, new SlideSpec("Title", ["old price 100 yen"]));
        using (var document = PresentationDocument.Open(path, isEditable: true))
        {
            var slide = document.PresentationPart!.SlideParts.First();
            var run = slide.Slide!.Descendants<A.Run>().First(r => r.Text?.Text == "old price 100 yen");
            run.RunProperties = new A.RunProperties { Strike = A.TextStrikeValues.SingleStrike };
            slide.Slide.Save();
        }

        var markdown = PptxReader.Read(path, includeHidden: false).Single().Markdown;

        Assert.Contains("~~old price 100 yen~~", markdown);
    }
}
