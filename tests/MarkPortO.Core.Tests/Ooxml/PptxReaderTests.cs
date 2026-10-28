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
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class PptxReaderTests
{
    [Fact]
    public void Read_SlideWithTitleAndBody_ProducesAHeadingAndBulletItems()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan", ["first point", "second point"]));

        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false));

        Assert.Equal(1, slide.Number);
        Assert.Equal("Plan", slide.Title);
        Assert.Equal("## Plan\n\n- first point\n- second point", slide.Markdown);
    }

    [Fact]
    public void Read_NotesLinksPicturesChartsAndDiagrams_AreWritten()
    {
        using var dir = new TempDir();
        var path = dir.Combine("rich.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan", ["see the site"]));
        AddRichParts(path);

        var slide = PptxReader.Read(path, false).Single();

        Assert.Contains("- [see the site](https://example.com/a%20b)", slide.Markdown);
        Assert.Contains("![Company logo]", slide.Markdown);
        Assert.Contains("#### Chart: Sales by year (bar)", slide.Markdown);
        Assert.Contains("| 2025 | 1,800 |", slide.Markdown);
        Assert.Contains("- Start\n- Finish", slide.Markdown);
        Assert.EndsWith("> **Notes**: Say hello first\n>\n> **Notes**: Then the numbers", slide.Markdown);
    }

    // A link on the text, a picture with an alternative text, a chart, a SmartArt diagram and the speaker notes.
    private static void AddRichParts(string path)
    {
        using var document = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, isEditable: true);
        var slidePart = document.PresentationPart!.SlideParts.First();
        var tree = slidePart.Slide!.CommonSlideData!.ShapeTree!;

        // The text of the body gets a link.
        var run = tree.Descendants<DocumentFormat.OpenXml.Drawing.Run>().First(r => r.Text?.Text == "see the site");
        var relationship = slidePart.AddHyperlinkRelationship(new Uri("https://example.com/a b"), isExternal: true);
        run.RunProperties = new DocumentFormat.OpenXml.Drawing.RunProperties(new DocumentFormat.OpenXml.Drawing.HyperlinkOnClick { Id = relationship.Id });

        tree.AppendChild(new DocumentFormat.OpenXml.Presentation.Picture(
            new DocumentFormat.OpenXml.Presentation.NonVisualPictureProperties(
                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 90, Name = "Picture 1", Description = "Company logo" },
                new DocumentFormat.OpenXml.Presentation.NonVisualPictureDrawingProperties(),
                new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties()),
            new DocumentFormat.OpenXml.Presentation.BlipFill(),
            new DocumentFormat.OpenXml.Presentation.ShapeProperties()));

        var chart = slidePart.AddNewPart<DocumentFormat.OpenXml.Packaging.ChartPart>();
        chart.ChartSpace = new DocumentFormat.OpenXml.Drawing.Charts.ChartSpace(XlsxReaderTests.BarChartXml);

        var diagram = slidePart.AddNewPart<DocumentFormat.OpenXml.Packaging.DiagramDataPart>();
        diagram.DataModelRoot = new DocumentFormat.OpenXml.Drawing.Diagrams.DataModelRoot(
            "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><dgm:ptLst>"
            + "<dgm:pt modelId=\"0\" type=\"doc\"><dgm:t><a:bodyPr/><a:p><a:r><a:t>root</a:t></a:r></a:p></dgm:t></dgm:pt>"
            + "<dgm:pt modelId=\"1\"><dgm:t><a:bodyPr/><a:p><a:r><a:t>Start</a:t></a:r></a:p></dgm:t></dgm:pt>"
            + "<dgm:pt modelId=\"2\"><dgm:t><a:bodyPr/><a:p><a:r><a:t>Finish</a:t></a:r></a:p></dgm:t></dgm:pt>"
            + "<dgm:pt modelId=\"3\" type=\"pres\"><dgm:t><a:bodyPr/><a:p><a:r><a:t>not a node</a:t></a:r></a:p></dgm:t></dgm:pt>"
            + "</dgm:ptLst></dgm:dataModel>");

        var notes = slidePart.AddNewPart<DocumentFormat.OpenXml.Packaging.NotesSlidePart>();
        notes.NotesSlide = new DocumentFormat.OpenXml.Presentation.NotesSlide(
            new DocumentFormat.OpenXml.Presentation.CommonSlideData(new DocumentFormat.OpenXml.Presentation.ShapeTree(
                NotesShape(1, DocumentFormat.OpenXml.Presentation.PlaceholderValues.SlideImage),
                NotesShape(2, DocumentFormat.OpenXml.Presentation.PlaceholderValues.Body, "Say hello first", "Then the numbers"),
                NotesShape(3, DocumentFormat.OpenXml.Presentation.PlaceholderValues.SlideNumber, "1"))));
    }

    private static DocumentFormat.OpenXml.Presentation.Shape NotesShape(uint id, DocumentFormat.OpenXml.Presentation.PlaceholderValues type, params string?[] lines)
    {
        var shape = new DocumentFormat.OpenXml.Presentation.Shape(
            new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = id, Name = "Notes " + id },
                new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties(),
                new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties(new DocumentFormat.OpenXml.Presentation.PlaceholderShape { Type = type })),
            new DocumentFormat.OpenXml.Presentation.ShapeProperties());
        if (lines.Length > 0 && lines[0] is not null)
        {
            shape.TextBody = new DocumentFormat.OpenXml.Presentation.TextBody(
                new DocumentFormat.OpenXml.Drawing.BodyProperties(),
                new DocumentFormat.OpenXml.Drawing.ListStyle(),
                new DocumentFormat.OpenXml.Drawing.Paragraph(lines.Select(l => (DocumentFormat.OpenXml.OpenXmlElement)new DocumentFormat.OpenXml.Drawing.Run(new DocumentFormat.OpenXml.Drawing.Text(l!))).Take(1)));
            foreach (var extra in lines.Skip(1))
            {
                shape.TextBody.AppendChild(new DocumentFormat.OpenXml.Drawing.Paragraph(new DocumentFormat.OpenXml.Drawing.Run(new DocumentFormat.OpenXml.Drawing.Text(extra!))));
            }
        }

        return shape;
    }

    [Fact]
    public void Read_Table_BecomesAPipeTable()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Numbers", Table: [["A", "B"], ["1", "2"]]));

        var slide = PptxReader.Read(path, false).Single();

        Assert.Contains("| A | B |\n|---|---|\n| 1 | 2 |", slide.Markdown);
    }

    [Fact]
    public void Read_Table_WithMergedCells_MarksTheHiddenCells()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.pptx");
        PptxFactory.Create(path, new SlideSpec("Merged", Table: [["Title", "<h>", "X"], ["A", "b", "c"], ["<v>", "d", ""]]));

        var slide = PptxReader.Read(path, false).Single();

        Assert.Contains("| Title | Title | X |\n|---|---|---|\n| A | b | c |\n| ^ | d |  |", MarkdownText.ResolveMarks(slide.Markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Table_WithoutAHeaderRowOption_KeepsTheFirstRowAsData()
    {
        using var dir = new TempDir();
        var path = dir.Combine("n.pptx");
        PptxFactory.Create(path, new SlideSpec("NoHeader", Table: [["A", "B"], ["1", "2"]], TableHeader: false));

        var slide = PptxReader.Read(path, false).Single();

        Assert.Contains("|  |  |\n|---|---|\n| A | B |\n| 1 | 2 |", slide.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_HiddenSlides_AreSkippedUnlessRequested_AndKeepTheirNumbers()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(
            path,
            new SlideSpec("One"),
            new SlideSpec("Two (hidden)", Hidden: true),
            new SlideSpec("Three"));

        var visible = PptxReader.Read(path, includeHidden: false);
        Assert.Equal([1, 3], visible.Select(s => s.Number));

        var all = PptxReader.Read(path, includeHidden: true);
        Assert.Equal([1, 2, 3], all.Select(s => s.Number));
        Assert.Equal([false, true, false], all.Select(s => s.Hidden));
        Assert.Equal([false, true, false], PptxReader.ReadHiddenFlags(path));
    }

    [Fact]
    public void Read_PresentationWithoutSlides_ReturnsNothing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("empty.pptx");
        PptxFactory.Create(path);

        Assert.Empty(PptxReader.Read(path, includeHidden: true));
    }

    [Fact]
    public void Read_NeutralisesMarkPortOMarkers()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("A <!-- page: 3 --> B"));

        var slide = PptxReader.Read(path, false).Single();

        Assert.DoesNotContain("<!--", slide.Markdown);
    }

    [Fact]
    public void Read_CorruptFile_ThrowsInvalidDocumentException()
    {
        using var dir = new TempDir();
        var path = dir.Combine("broken.pptx");
        File.WriteAllText(path, "not a presentation");

        Assert.Throws<InvalidDocumentException>(() => PptxReader.Read(path, false));
    }
}
