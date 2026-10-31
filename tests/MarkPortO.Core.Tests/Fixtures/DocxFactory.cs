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

namespace MarkPortO.Core.Tests.Fixtures;

/// <summary>A page of a test document: its paragraphs, and shapes anchored in the first paragraph.</summary>
public sealed record DocPage(string[] Paragraphs, DocShape[]? Shapes = null);

/// <summary>A drawn shape. Its size is a fraction of the page (the position does not matter for the tests).</summary>
public sealed record DocShape(string Geometry, double WidthFraction, double HeightFraction, bool FlipH = false, bool FlipV = false)
{
    /// <summary>A "x" made of one multiply shape over (nearly) the whole page.</summary>
    public static DocShape[] CrossShape(double size = 0.9) => [new("mathMultiply", size, size)];

    /// <summary>A "x" made of two crossing diagonal lines over (nearly) the whole page.</summary>
    public static DocShape[] CrossLines(double size = 0.9) =>
        [new("line", size, size), new("line", size, size, FlipV: true)];
}

/// <summary>Builds small .docx files with a known layout, so page assignment can be tested against a known answer.</summary>
public static class DocxFactory
{
    /// <summary>A4 portrait, 2 cm margins.</summary>
    private const int PageWidthTwips = 11906;
    private const int PageHeightTwips = 16838;
    private const int MarginTwips = 1134;

    /// <summary>
    /// One list of paragraphs per page. Each page after the first starts with "page break before",
    /// so the page of every paragraph is known without asking Word or LibreOffice.
    /// </summary>
    public static void CreatePaged(string path, params string[][] pages)
    {
        using var document = NewDocument(path, out var body);
        for (var page = 0; page < pages.Length; page++)
        {
            for (var i = 0; i < pages[page].Length; i++)
            {
                var paragraph = Plain(pages[page][i]);
                if (page > 0 && i == 0)
                {
                    paragraph.ParagraphProperties = new ParagraphProperties(new PageBreakBefore());
                }

                body.AppendChild(paragraph);
            }
        }

        Finish(body);
    }

    /// <summary>
    /// A long text that flows over several pages by itself. Paragraph <c>n</c> starts with the token
    /// <c>Pnnnn</c> (for example <c>P0007</c>), so a test can find it in the PDF text.
    /// </summary>
    public static void CreateFlow(string path, int paragraphs, int wordsPerParagraph = 70)
    {
        using var document = NewDocument(path, out var body);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet" };
        for (var n = 1; n <= paragraphs; n++)
        {
            var text = new System.Text.StringBuilder($"P{n:0000}");
            for (var w = 0; w < wordsPerParagraph; w++)
            {
                text.Append(' ').Append(words[(n + w) % words.Length]);
            }

            body.AppendChild(Plain(text.ToString()));
        }

        Finish(body);
    }

    /// <summary>
    /// Two items of a list whose numbering is in the style ("List Bullet": the paragraphs have no numbering of their own), a paragraph whose style turns
    /// the numbering off (<c>numId</c> 0), and a plain paragraph.
    /// </summary>
    public static void CreateStyleNumbered(string path)
    {
        using var document = NewDocument(path, out var body);
        var main = document.MainDocumentPart!;
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        var listBullet = Style("ListBullet", "List Bullet", basedOn: "Normal");
        listBullet.AppendChild(new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 1 })));
        var listBulletTwo = Style("ListBullet2", "List Bullet 2", basedOn: "ListBullet");
        var noList = Style("NoList", "No List", basedOn: "ListBullet");
        noList.AppendChild(new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 0 })));
        styles.Styles = new Styles(Style("Normal", "Normal", isDefault: true), listBullet, listBulletTwo, noList);
        AddBulletNumbering(main);

        body.AppendChild(Styled("ListBullet", "first item"));
        body.AppendChild(Styled("ListBullet2", "second item"));
        body.AppendChild(Styled("NoList", "not a list"));
        body.AppendChild(Plain("plain"));
        Finish(body);
    }

    /// <summary>A heading, two bullet items, a paragraph, and a 2 x 2 table.</summary>
    public static void CreateStructured(string path)
    {
        using var document = NewDocument(path, out var body);
        AddStyles(document.MainDocumentPart!);
        AddBulletNumbering(document.MainDocumentPart!);

        body.AppendChild(Styled("Heading1", "Quarterly Report"));
        body.AppendChild(Styled("Heading2", "Summary"));
        body.AppendChild(Plain("Plain paragraph with some text."));
        body.AppendChild(Bullet("first item"));
        body.AppendChild(Bullet("second item"));
        body.AppendChild(NewTable([["Name", "Value"], ["Alpha", "1"], ["Beta | Gamma", "2"]]));
        body.AppendChild(Plain("After the table."));

        Finish(body);
    }

    /// <summary>
    /// Pages of text where a page can carry shapes anchored in its first paragraph (for example a "×" over the page).
    /// <paramref name="headerShapes"/> are drawn in the page header, so they appear on every page but belong to none.
    /// </summary>
    public static void CreateWithShapes(string path, DocPage[] pages, DocShape[]? headerShapes = null)
    {
        using var document = NewDocument(path, out var body);
        for (var page = 0; page < pages.Length; page++)
        {
            for (var i = 0; i < pages[page].Paragraphs.Length; i++)
            {
                var paragraph = Plain(pages[page].Paragraphs[i]);
                if (page > 0 && i == 0)
                {
                    paragraph.ParagraphProperties = new ParagraphProperties(new PageBreakBefore());
                }

                if (i == 0 && pages[page].Shapes is { } shapes)
                {
                    foreach (var shape in shapes)
                    {
                        paragraph.AppendChild(ShapeRun(shape));
                    }
                }

                body.AppendChild(paragraph);
            }
        }

        var sectionProperties = new SectionProperties();
        if (headerShapes is { Length: > 0 })
        {
            var headerPart = document.MainDocumentPart!.AddNewPart<HeaderPart>();
            var header = new Header(new Paragraph());
            foreach (var shape in headerShapes)
            {
                header.GetFirstChild<Paragraph>()!.AppendChild(ShapeRun(shape));
            }

            headerPart.Header = header;
            sectionProperties.AppendChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = document.MainDocumentPart.GetIdOfPart(headerPart) });
        }

        sectionProperties.AppendChild(new PageSize { Width = PageWidthTwips, Height = PageHeightTwips });
        sectionProperties.AppendChild(new PageMargin
        {
            Top = MarginTwips,
            Bottom = MarginTwips,
            Left = (uint)MarginTwips,
            Right = (uint)MarginTwips,
            Header = 567,
            Footer = 567,
        });
        body.AppendChild(sectionProperties);
        body.Ancestors<Document>().First().Save();
    }

    /// <summary>An anchored drawing (a "wps" shape) of the given size, relative to the A4 page.</summary>
    private static Run ShapeRun(DocShape shape)
    {
        const double pageWidthEmu = PageWidthTwips * 635.0;
        const double pageHeightEmu = PageHeightTwips * 635.0;
        var cx = (long)(shape.WidthFraction * pageWidthEmu);
        var cy = (long)(shape.HeightFraction * pageHeightEmu);
        var flips = (shape.FlipH ? " flipH=\"1\"" : string.Empty) + (shape.FlipV ? " flipV=\"1\"" : string.Empty);
        var geometry = shape.Geometry == "custGeom"
            ? "<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"100\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo></a:path></a:pathLst></a:custGeom>"
            : $"<a:prstGeom prst=\"{shape.Geometry}\"><a:avLst/></a:prstGeom>";

        return new Run($"""
            <w:r xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                 xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                 xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                 xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
              <w:drawing>
                <wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="251659264" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">
                  <wp:simplePos x="0" y="0"/>
                  <wp:positionH relativeFrom="page"><wp:posOffset>0</wp:posOffset></wp:positionH>
                  <wp:positionV relativeFrom="page"><wp:posOffset>0</wp:posOffset></wp:positionV>
                  <wp:extent cx="{cx}" cy="{cy}"/>
                  <wp:effectExtent l="0" t="0" r="0" b="0"/>
                  <wp:wrapNone/>
                  <wp:docPr id="{Interlocked.Increment(ref _shapeId)}" name="Shape"/>
                  <wp:cNvGraphicFramePr/>
                  <a:graphic>
                    <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                      <wps:wsp>
                        <wps:cNvSpPr/>
                        <wps:spPr>
                          <a:xfrm{flips}><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                          {geometry}
                          <a:ln w="38100"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></a:ln>
                        </wps:spPr>
                        <wps:bodyPr/>
                      </wps:wsp>
                    </a:graphicData>
                  </a:graphic>
                </wp:anchor>
              </w:drawing>
            </w:r>
            """);
    }

    private static int _shapeId;

    /// <summary>A file that has the .docx extension but is not a document.</summary>
    public static void CreateCorrupt(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a zip file, so it is not a docx");
    }

    private static WordprocessingDocument NewDocument(string path, out Body body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        body = main.Document.Body!;
        return document;
    }

    private static void Finish(Body body)
    {
        body.AppendChild(new SectionProperties(
            new PageSize { Width = PageWidthTwips, Height = PageHeightTwips },
            new PageMargin
            {
                Top = MarginTwips,
                Bottom = MarginTwips,
                Left = (uint)MarginTwips,
                Right = (uint)MarginTwips,
                Header = 567,
                Footer = 567,
            }));
        body.Ancestors<Document>().First().Save();
    }

    private static Paragraph Plain(string text) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Styled(string styleId, string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static Paragraph Bullet(string text) =>
        new(new ParagraphProperties(
                new ParagraphStyleId { Val = "ListParagraph" },
                new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>
    /// A table with a cell that spans two columns (row 1) and a cell merged over two rows (rows 2 and 3).
    /// <paramref name="header"/> sets the "header row" look of the table.
    /// </summary>
    /// <summary>A link, a footnote, a picture with an alternative text, and a table with a table in a cell.</summary>
    public static void CreateRich(string path)
    {
        using var document = NewDocument(path, out var body);
        var main = document.MainDocumentPart!;
        var relationship = main.AddHyperlinkRelationship(new Uri("https://example.com/a b"), isExternal: true);
        body.AppendChild(new Paragraph(
            new Run(new Text("See ") { Space = SpaceProcessingModeValues.Preserve }),
            new Hyperlink(new Run(new Text("the site"))) { Id = relationship.Id },
            new Run(new Text("."))));

        var footnotes = main.AddNewPart<FootnotesPart>();
        footnotes.Footnotes = new Footnotes(
            new Footnote(new Paragraph(new Run(new Text("Separator")))) { Type = FootnoteEndnoteValues.Separator, Id = -1 },
            new Footnote(new Paragraph(new Run(new FootnoteReferenceMark()), new Run(new Text(" Source: a report")))) { Id = 1 });
        body.AppendChild(new Paragraph(new Run(new Text("A fact")), new Run(new FootnoteReference { Id = 1 })));

        var picture = new Drawing(new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = 1, Name = "Picture 1", Description = "A bar chart" },
            new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(new DocumentFormat.OpenXml.Drawing.Blip()))));
        body.AppendChild(new Paragraph(new Run(picture)));

        var inner = new Table(new TableRow(new TableCell(Plain("x")), new TableCell(Plain("y"))), new TableRow(new TableCell(Plain("z")), new TableCell(Plain("w"))));
        var outer = new Table(
            new TableProperties(new TableLook { Val = "0420" }),
            new TableRow(new TableCell(Plain("Head")), new TableCell(Plain("Other"))),
            new TableRow(new TableCell(Plain("Cell"), inner, Plain("tail")), new TableCell(Plain("k"))));
        body.AppendChild(outer);
        Finish(body);
    }

    public static void CreateMergedTable(string path, bool header)
    {
        using var document = NewDocument(path, out var body);
        var table = new Table(new TableProperties(new TableLook { Val = header ? "0420" : "0400" }));
        var grid = new TableGrid();
        for (var i = 0; i < 3; i++)
        {
            grid.AppendChild(new GridColumn { Width = "3000" });
        }

        table.AppendChild(grid);

        TableCell Cell(string text, int span = 1, MergedCellValues? vertical = null)
        {
            var properties = new TableCellProperties();
            if (span > 1)
            {
                properties.AppendChild(new GridSpan { Val = span });
            }

            if (vertical is { } v)
            {
                properties.AppendChild(v == MergedCellValues.Restart ? new VerticalMerge { Val = MergedCellValues.Restart } : new VerticalMerge());
            }

            return new TableCell(properties, Plain(text));
        }

        table.AppendChild(new TableRow(Cell("Title", span: 2), Cell("X")));
        table.AppendChild(new TableRow(Cell("A", vertical: MergedCellValues.Restart), Cell("b"), Cell("c")));
        table.AppendChild(new TableRow(Cell(string.Empty, vertical: MergedCellValues.Continue), Cell("d"), Cell(string.Empty)));
        body.AppendChild(table);
        body.AppendChild(Plain("After the table."));
        Finish(body);
    }

    private static Table NewTable(string[][] rows)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableLook { Val = "0420" }));

        // The grid (one column definition per column) is required before the rows.
        var grid = new TableGrid();
        for (var column = 0; column < rows.Max(r => r.Length); column++)
        {
            grid.AppendChild(new GridColumn { Width = "4000" });
        }

        table.AppendChild(grid);
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
            {
                tableRow.AppendChild(new TableCell(Plain(cell)));
            }

            table.AppendChild(tableRow);
        }

        return table;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            Style("Normal", "Normal", isDefault: true),
            Style("Heading1", "heading 1", basedOn: "Normal", outline: 0, size: "32", bold: true),
            Style("Heading2", "heading 2", basedOn: "Normal", outline: 1, size: "26", bold: true),
            Style("ListParagraph", "List Paragraph", basedOn: "Normal"));
    }

    private static Style Style(string id, string name, bool isDefault = false, string? basedOn = null, int? outline = null, string? size = null, bool bold = false)
    {
        var runProperties = new StyleRunProperties();
        if (bold)
        {
            runProperties.AppendChild(new Bold());
        }

        if (size is not null)
        {
            runProperties.AppendChild(new FontSize { Val = size });
        }

        var style = new Style(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id };
        if (isDefault)
        {
            style.Default = true;
        }

        if (basedOn is not null)
        {
            style.AppendChild(new BasedOn { Val = basedOn });
        }

        if (outline is not null)
        {
            style.AppendChild(new StyleParagraphProperties(new OutlineLevel { Val = outline.Value }));
        }

        if (runProperties.HasChildren)
        {
            style.AppendChild(runProperties);
        }

        return style;
    }

    private static void AddBulletNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new Numbering(
            new AbstractNum(
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 0 },
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
    }
}
