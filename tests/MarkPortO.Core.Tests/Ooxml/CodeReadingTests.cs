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

/// <summary>Code in a document: a run in a code style or a fixed-width font is a span of code, paragraphs of code are a fenced block (FR-140).</summary>
public class CodeReadingTests
{
    private const string Fence = "```";

    private static Run Plain(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run Styled(string text, string styleId) =>
        new(new RunProperties(new RunStyle { Val = styleId }), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Run Font(string text, string font) =>
        new(new RunProperties(new RunFonts { Ascii = font, HighAnsi = font }), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static string Create(TempDir dir, params OpenXmlElement[] body)
    {
        var path = dir.Combine("code.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(body));
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "Verbatim Char" }, new StyleRunProperties(new Bold())) { Type = StyleValues.Character, StyleId = "VerbatimChar" },
                new Style(new StyleName { Val = "Keyword Tok" }, new BasedOn { Val = "VerbatimChar" }, new StyleRunProperties(new Italic())) { Type = StyleValues.Character, StyleId = "KeywordTok" },
                new Style(new StyleName { Val = "Source Code" }) { Type = StyleValues.Paragraph, StyleId = "SourceCode" },
                new Style(new StyleName { Val = "heading 1" }) { Type = StyleValues.Paragraph, StyleId = "Heading1" });
        }

        return path;
    }

    private static string[] Lines(string path, bool emphasis = true) =>
        [.. DocxReader.Read(path, emphasis).Select(b => MarkdownText.ResolveMarks(b.Block.Markdown))];

    private static Paragraph Code(params string[] lines)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "SourceCode" }));
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Append(new Run(new Break()));
            }

            paragraph.Append(Styled(lines[i], "KeywordTok"));
        }

        return paragraph;
    }

    [Fact]
    public void ARunInACodeStyle_IsASpanOfCode_AndTheStyleBoldIsNotMarked()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(Plain("Open "), Styled("settings.json", "VerbatimChar"), Plain(" first.")));

        Assert.Equal(["Open `settings.json` first."], Lines(path));
    }

    [Fact]
    public void ARunInAFixedWidthFont_IsASpanOfCode()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(Plain("Run "), Font("markporto convert", "Consolas"), Plain(".")));

        Assert.Equal(["Run `markporto convert`."], Lines(path, emphasis: false));
    }

    [Fact]
    public void RunsOfCodeNextToEachOther_AreOneSpan()
    {
        using var dir = new TempDir();
        var path = Create(dir, new Paragraph(Plain("See "), Styled("a", "VerbatimChar"), Styled(" b", "VerbatimChar"), Plain(" now")));

        Assert.Equal(["See `a b` now"], Lines(path));
    }

    [Fact]
    public void ACodeSpan_WithBackticksInIt_HasALongerFence()
    {
        Assert.Equal("``a`b``", CodeText.Span("a`b"));
        Assert.Equal("`` `x ``", CodeText.Span("`x"));
    }

    [Fact]
    public void ParagraphsOfCode_AreOneFencedBlock_WithTheirIndent()
    {
        using var dir = new TempDir();
        var path = Create(
            dir,
            new Paragraph(Plain("Before.")),
            Code("out/report/", "    ├── report.md", "    └── images/"),
            Code("markporto list-jobs"),
            new Paragraph(Plain("After.")));

        var lines = Lines(path);

        Assert.Equal(3, lines.Length);
        Assert.Equal(Fence + "\nout/report/\n    ├── report.md\n    └── images/\nmarkporto list-jobs\n" + Fence, lines[1]);
        Assert.Equal("After.", lines[2]);
    }

    [Fact]
    public void ACodeBlock_ContainingAFence_IsWrittenInALongerFence()
    {
        using var dir = new TempDir();
        var path = Create(dir, Code("```markdown", "# title", "```"));

        Assert.Equal("````\n```markdown\n# title\n```\n````", Lines(path).Single());
    }

    [Fact]
    public void ACodeBlock_DoesNotWriteTheMarkersOfMarkPortO()
    {
        using var dir = new TempDir();
        var path = Create(dir, Code("<!-- page: 3 -->"));

        Assert.DoesNotContain("<!--", Lines(path).Single());
    }

    [Fact]
    public void AnEmptyParagraphInCode_IsAnEmptyLine_AndOneAtTheEndGoes()
    {
        using var dir = new TempDir();
        var path = Create(dir, Code("a"), new Paragraph(), Code("b"), new Paragraph(), new Paragraph(Plain("text")));

        var lines = Lines(path);

        Assert.Equal([Fence + "\na\n\nb\n" + Fence, "text"], lines);
    }

    [Fact]
    public void AParagraphOfOnlyFixedWidthText_IsACodeBlock_ButAHeadingIsNot()
    {
        using var dir = new TempDir();
        var path = Create(
            dir,
            new Paragraph(Font("x = 1", "Courier New")),
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), Font("Title", "Consolas")));

        var lines = Lines(path);

        Assert.Equal(Fence + "\nx = 1\n" + Fence, lines[0]);
        Assert.StartsWith("# ", lines[1]);
    }

    [Theory]
    [InlineData("<m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f>", "a/b")]
    [InlineData("<m:f><m:num><m:r><m:t>a+1</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f>", "(a+1)/b")]
    [InlineData("<m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup>", "x^2")]
    [InlineData("<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:r><m:t>x</m:t></m:r></m:e></m:rad>", "√(x)")]
    public void AnEquation_IsALineOfText(string body, string expected)
    {
        using var dir = new TempDir();
        var paragraph = new Paragraph(Plain("式: "));
        paragraph.Append(new DocumentFormat.OpenXml.Math.OfficeMath { InnerXml = body });
        var file = Create(dir, paragraph);

        Assert.Equal("式: " + expected, Lines(file, emphasis: false).Single());
    }

    [Fact]
    public void AStyleWithALevelButNoNumber_IsNoList()
    {
        using var dir = new TempDir();
        var path = dir.Combine("subtitle.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Subtitle" }), Plain("サブタイトル"))));
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "Subtitle" }, new StyleParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 1 }))) { Type = StyleValues.Paragraph, StyleId = "Subtitle" });
        }

        Assert.Equal(["サブタイトル"], Lines(path, emphasis: false));
    }

    private static string CreateWithStyles(TempDir dir, string name, Styles styles, params OpenXmlElement[] body)
    {
        var path = dir.Combine(name);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body(body));
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
        return path;
    }

    [Fact]
    public void ALineOfTheTableOfContents_HasNoWarningWhenItsPageIsNotFound()
    {
        using var dir = new TempDir();
        var path = CreateWithStyles(
            dir,
            "toc.docx",
            new Styles(new Style(new StyleName { Val = "toc 1" }) { Type = StyleValues.Paragraph, StyleId = "TOC1" }),
            new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "TOC1" }), Plain("1. 概要" + new string('.', 30) + "3")),
            new Paragraph(Plain("ふつうの段落で、PDFには無い長い文章がここに入っている。")));

        var blocks = DocxReader.Read(path);
        var warnings = DocxPageAssigner.Assign(blocks, ["全く別の文字だけが入ったページ"]);

        Assert.True(blocks[0].IsToc);
        Assert.False(blocks[1].IsToc);
        Assert.Single(warnings); // the ordinary paragraph still has its warning
    }

    [Fact]
    public void ABulletInACell_BeginsWithABullet_AndANestedTableKeepsItsRows()
    {
        using var dir = new TempDir();
        var bullet = new Paragraph(
            new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
            Plain("失敗したファイルがある"));
        var inner = new Table(
            new TableRow(new TableCell(new Paragraph(Plain("状態"))), new TableCell(new Paragraph(Plain("ボタン")))),
            new TableRow(new TableCell(new Paragraph(Plain("Running"))), new TableCell(new Paragraph(Plain("中止")))));
        var cell = new TableCell(new Paragraph(Plain("次のいずれか:")), bullet, inner, new Paragraph(Plain("以上")));
        var path = dir.Combine("cell.docx");
        using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Table(new TableRow(new TableCell(new Paragraph(Plain("条件"))), cell))));
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(
                new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" }) { LevelIndex = 0 }) { AbstractNumberId = 0 },
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
        }

        var markdown = DocxReader.Read(path).Single().Block.Markdown;

        Assert.Contains("次のいずれか:<br>・失敗したファイルがある<br>状態 ｜ ボタン<br>Running ｜ 中止<br>以上", markdown);
    }

    // ---- pptx

    [Fact]
    public void Pptx_ParagraphsInAFixedWidthFont_AreAFencedBlock_AndARunIsASpan()
    {
        using var dir = new TempDir();
        var path = dir.Combine("code.pptx");
        PptxFactory.Create(path, new SlideSpec("Title", ["use convert now", "out/report/", "    report.md"]));
        using (var document = PresentationDocument.Open(path, isEditable: true))
        {
            var slide = document.PresentationPart!.SlideParts.First();
            foreach (var run in slide.Slide!.Descendants<A.Run>().Where(r => r.Text?.Text is "out/report/" or "    report.md"))
            {
                run.RunProperties = new A.RunProperties(new A.LatinFont { Typeface = "Consolas" });
            }

            slide.Slide.Save();
        }

        var markdown = PptxReader.Read(path, includeHidden: false).Single().Markdown;

        Assert.Contains(Fence + "\nout/report/\n    report.md\n" + Fence, markdown);
    }

    [Fact]
    public void Pptx_TheSlideNumber_IsNotInTheText()
    {
        using var dir = new TempDir();
        var path = dir.Combine("number.pptx");
        PptxFactory.Create(path, new SlideSpec("Title", ["body text"]));
        using (var document = PresentationDocument.Open(path, isEditable: true))
        {
            var slide = document.PresentationPart!.SlideParts.First();
            var tree = slide.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.ShapeTree>().First();
            tree.Append(new DocumentFormat.OpenXml.Presentation.Shape(
                new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                    new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 90, Name = "Number" },
                    new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties(),
                    new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties(
                        new DocumentFormat.OpenXml.Presentation.PlaceholderShape { Type = DocumentFormat.OpenXml.Presentation.PlaceholderValues.SlideNumber })),
                new DocumentFormat.OpenXml.Presentation.ShapeProperties(),
                new DocumentFormat.OpenXml.Presentation.TextBody(
                    new A.BodyProperties(),
                    new A.Paragraph(new A.Field(new A.Text("7")) { Id = "{F7021451-1387-4CA6-816F-3879F97B5CBC}", Type = "slidenum" }))));
            slide.Slide.Save();
        }

        var markdown = PptxReader.Read(path, includeHidden: false).Single().Markdown;

        Assert.Contains("body text", markdown);
        Assert.DoesNotContain("7", markdown);
    }
}
