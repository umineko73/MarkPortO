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

using System.Text.Json;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-152: the chunks for retrieval (contracts/chunks-format.md).</summary>
public class ChunkBuilderTests
{
    private static DocumentRecord Doc(DocumentKind kind, int pages, params Block[] blocks)
    {
        var name = kind switch { DocumentKind.Xlsx => "表.xlsx", DocumentKind.Pptx => "発表.pptx", DocumentKind.Pdf => "資料.pdf", _ => "設計書.docx" };
        return new DocumentRecord
        {
            MarkdownLanguage = "ja",
            DocId = "d0001",
            SourcePath = @"D:\in\秘密のフォルダ\" + name,
            Kind = kind,
            OutputFolder = @"D:\out\設計書",
            Step1 = StepStatus.Done,
            Step3 = StepStatus.NotApplicable,
            Chunks = true,
            Fingerprint = new Fingerprint(10, new DateTime(2026, 11, 15, 7, 38, 12, DateTimeKind.Utc).Ticks),
            Pages = [.. Enumerable.Range(1, pages).Select(i => new PageRecord { Index = i, ImageFile = $"images/page-{i:D3}.png" })],
            Blocks = [.. blocks],
        };
    }

    private static Block H(string markdown, int page) => new() { Kind = BlockKind.Heading, Markdown = markdown, PageIndex = page };

    private static Block P(string markdown, int? page = null) => new() { Kind = BlockKind.Paragraph, Markdown = markdown, PageIndex = page };

    private static Block T(string markdown, int page, int? joinedTo = null) => new() { Kind = BlockKind.Table, Markdown = markdown, PageIndex = page, JoinedToPage = joinedTo };

    [Fact]
    public void ANewChunkStartsAtEachHeading_WithTheHeadingsItIsUnder_AndItsPages()
    {
        var doc = Doc(DocumentKind.Docx, 3,
            H("# 文書の題名", 1),
            H("# 1\\. 実装計画", 1),
            P("計画の本文。"),
            H("## 1\\.1 概要", 2),
            P("概要の本文。"),
            P("次のページに続く本文。", 3),
            H("# 2\\. データモデル", 3),
            P("データモデルの本文。"));

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(["1. 実装計画"], chunks[0].Headings); // the title has no text of its own: no chunk
        Assert.Equal(["1. 実装計画", "1.1 概要"], chunks[1].Headings);
        Assert.Equal("概要の本文。\n\n次のページに続く本文。", chunks[1].Text);
        Assert.Equal((2, 3), (chunks[1].FirstPage, chunks[1].LastPage));
        Assert.Equal(["2. データモデル"], chunks[2].Headings);
        Assert.Equal(["c0001", "c0002", "c0003"], chunks.Select(c => c.Id));
    }

    [Fact]
    public void Paragraphs_ArePutTogetherUpToTheLimit_AndNotCutInside()
    {
        var a = new string('あ', 900);
        var b = new string('い', 900);
        var c = new string('う', 2000);
        var doc = Doc(DocumentKind.Docx, 1, H("# 節", 1), P(a), P(b), P(c), P("短い。"));

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal([a, b, c, "短い。"], chunks.Select(x => x.Text));
        Assert.All(chunks, x => Assert.Equal(ChunkKind.Text, x.Kind));
    }

    [Fact]
    public void ATableAndACodeBlock_AreChunksOfTheirOwn()
    {
        var table = "| 項目 | 型 |\n|---|---|\n| jobId | GUID |";
        var code = "```\nvar x = 1;\nvar y = 2;\n```";
        var doc = Doc(DocumentKind.Docx, 1, H("# 節", 1), P("前の段落。"), T(table, 1), P("間の段落。"), P(code), P("後の段落。"));

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal([ChunkKind.Text, ChunkKind.Table, ChunkKind.Text, ChunkKind.Code, ChunkKind.Text], chunks.Select(x => x.Kind));
        Assert.Equal(table, chunks[1].Text);
        Assert.Equal(code, chunks[3].Text);
    }

    [Fact]
    public void ALongTable_IsCutBetweenItsRows_AndEachPartStartsWithTheHeaderRow()
    {
        var header = "| 名前 | 説明 |\n|---|---|";
        var rows = Enumerable.Range(1, 60).Select(i => $"| 項目{i} | {new string('説', 40)} |").ToList();
        var doc = Doc(DocumentKind.Docx, 1, H("# 表", 1), T(header + "\n" + string.Join('\n', rows), 1));

        var chunks = ChunkBuilder.Build(doc);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, x =>
        {
            Assert.Equal(ChunkKind.Table, x.Kind);
            Assert.StartsWith(header + "\n", x.Text, StringComparison.Ordinal);
            Assert.True(x.Text.Length <= ChunkBuilder.MaxChars, x.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        Assert.Equal(rows, chunks.SelectMany(x => x.Text.Split('\n').Skip(2)));
    }

    [Fact]
    public void ATableOverTwoPages_IsOneChunk_WithBothPages_AndTheNoteOfTheJoinIsLeftOut()
    {
        var first = "| a | b |\n|---|---|\n| 1 | 2 |";
        var second = "| a | b |\n|---|---|\n| 3 | 4 |";
        var doc = Doc(DocumentKind.Docx, 2, H("# 表", 1), T(first, 1), P("同じページの後の段落。", 1), T(second, 2, joinedTo: 1), P("次のページの段落。", 2));

        var chunks = ChunkBuilder.Build(doc);

        var table = Assert.Single(chunks, x => x.Kind == ChunkKind.Table);
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |", table.Text);
        Assert.Equal((1, 2), (table.FirstPage, table.LastPage));
        Assert.DoesNotContain(chunks, x => x.Text.Contains("結合", StringComparison.Ordinal));
    }

    [Fact]
    public void ACodeBlockOverTwoPages_IsOneChunk()
    {
        var doc = Doc(DocumentKind.Docx, 2, H("# コード", 1), P("```\nline1\n```", 1),
            new Block { Kind = BlockKind.Paragraph, Markdown = "```\nline2\n```", PageIndex = 2, JoinedToPage = 1 });

        var chunks = ChunkBuilder.Build(doc);

        var code = Assert.Single(chunks);
        Assert.Equal(ChunkKind.Code, code.Kind);
        Assert.Contains("line1", code.Text, StringComparison.Ordinal);
        Assert.Contains("line2", code.Text, StringComparison.Ordinal);
        Assert.Equal((1, 2), (code.FirstPage, code.LastPage));
    }

    [Fact]
    public void TheTableOfContents_AndADeletedPage_AreLeftOut()
    {
        var doc = Doc(DocumentKind.Docx, 3,
            new Block { Kind = BlockKind.Paragraph, Markdown = "1\\. 実装計画　1", PageIndex = 1, IsToc = true },
            H("# 1\\. 実装計画", 2),
            P("本文。"),
            P("削除したページの文字。", 3));
        doc.Pages[2].IsDeleted = true;

        var chunks = ChunkBuilder.Build(doc);

        var chunk = Assert.Single(chunks);
        Assert.Equal("本文。", chunk.Text);
        Assert.Equal((2, 2), (chunk.FirstPage, chunk.LastPage));
    }

    [Fact]
    public void WhatTheVlmReadInAFigure_IsInTheChunkOfTheFigure()
    {
        var doc = Doc(DocumentKind.Docx, 1, H("# 図", 1), P("売上の推移。\n\n![グラフ](pictures/page-001-1.png)"), P("次の段落。"));
        doc.Step3 = StepStatus.Done;
        doc.Pages[0].Step3 = PageStep3Status.Done;
        doc.Pages[0].ReadAs = PageReading.Pictures;
        doc.Pages[0].Figures = [new FigureReading("pictures/page-001-1.png", "1月 100、2月 120")];

        var chunk = Assert.Single(ChunkBuilder.Build(doc));

        Assert.Contains("![グラフ](pictures/page-001-1.png)\n\n> **", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("> 1月 100、2月 120", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APageWithoutTextThatTheVlmReadWhole_IsAPageChunk_ButStep1TextIsKeptWhereThereIsSome()
    {
        var doc = Doc(DocumentKind.Pdf, 2, P("文字の層のあるページ。", 1));
        doc.Step3 = StepStatus.Done;
        foreach (var page in doc.Pages)
        {
            page.Step3 = PageStep3Status.Done;
            page.ReadAs = PageReading.Whole;
        }

        doc.Pages[0].VlmText = "VLM が書き写した1ページ目（使わない）";
        doc.Pages[1].VlmText = "スキャンのページを VLM が読んだ文字";

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(("文字の層のあるページ。", ChunkKind.Text), (chunks[0].Text, chunks[0].Kind));
        Assert.Equal(("スキャンのページを VLM が読んだ文字", ChunkKind.Page), (chunks[1].Text, chunks[1].Kind));
        Assert.Equal((2, 2), (chunks[1].FirstPage, chunks[1].LastPage));
    }

    [Fact]
    public void ASlide_IsAChunkUnderItsTitle_AndItsTableIsAChunkOfItsOwn()
    {
        var doc = Doc(DocumentKind.Pptx, 2,
            new Block { Kind = BlockKind.SlideText, Markdown = "## 製品の紹介\n\n特長は3つ。\n\n| 版 | 日付 |\n|---|---|\n| 1.0 | 11/1 |", PageIndex = 1 },
            new Block { Kind = BlockKind.SlideText, Markdown = "## まとめ\n\nありがとうございました。", PageIndex = 2 });

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal([ChunkKind.Slide, ChunkKind.Table, ChunkKind.Slide], chunks.Select(x => x.Kind));
        Assert.Equal(["製品の紹介"], chunks[0].Headings);
        Assert.Equal(["製品の紹介"], chunks[1].Headings);
        Assert.Equal(["まとめ"], chunks[2].Headings);
        Assert.Equal((2, 2), (chunks[2].FirstPage, chunks[2].LastPage));
    }

    [Fact]
    public void WhatTheVlmReadInAPictureOfASlide_IsInTheChunkOfTheSlide()
    {
        var doc = Doc(DocumentKind.Pptx, 1,
            new Block { Kind = BlockKind.SlideText, Markdown = "## 画面\n\nメインの画面。\n\n![](pictures/page-001-1.png)", PageIndex = 1 });
        doc.Step3 = StepStatus.Done;
        doc.Pages[0].Step3 = PageStep3Status.Done;
        doc.Pages[0].ReadAs = PageReading.Pictures;
        doc.Pages[0].Figures = [new FigureReading("pictures/page-001-1.png", "開始のボタン")];

        var chunk = Assert.Single(ChunkBuilder.Build(doc));

        Assert.Equal(ChunkKind.Slide, chunk.Kind);
        Assert.Contains("![](pictures/page-001-1.png)\n\n> **", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("> 開始のボタン", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASheet_IsTheHeadingOfItsChunks()
    {
        var doc = Doc(DocumentKind.Xlsx, 2,
            new Block { Kind = BlockKind.SheetTable, Markdown = "| A | B |\n|---|---|\n| 1 | 2 |", PageIndex = 1 },
            new Block { Kind = BlockKind.SheetTable, Markdown = "| C |\n|---|\n| 3 |", PageIndex = 2 });
        doc.Pages[0].Sheet = new SheetInfo("売上", "A:B", 1, 1);
        doc.Pages[1].Sheet = new SheetInfo("費用", "A:A", 1, 1);

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["売上"], chunks[0].Headings);
        Assert.Equal("売上", chunks[0].Sheet);
        Assert.Equal(["費用"], chunks[1].Headings);
        Assert.Equal("費用", chunks[1].Sheet);
    }

    [Fact]
    public void TheMarkdownOfAnExtractor_IsCutByItsHeadings_WithoutPages()
    {
        using var dir = new TempDir();
        var doc = Doc(DocumentKind.Pdf, 2);
        doc.OutputFolder = dir.Path;
        doc.Extractor = "markitdown";
        File.WriteAllText(Path.Combine(dir.Path, "資料.md"),
            "> この Markdown は外部コマンドが書いた。\n\n# 概要\n\n本文の段落。\n\n<!-- a comment -->\n\n## 表\n\n| a |\n|---|\n| 1 |\n");

        var chunks = ChunkBuilder.Build(doc);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(["概要"], chunks[0].Headings);
        Assert.Equal("本文の段落。", chunks[0].Text);
        Assert.Equal(ChunkKind.Table, chunks[1].Kind);
        Assert.Equal(["概要", "表"], chunks[1].Headings);
        Assert.All(chunks, x => Assert.Null(x.FirstPage));
    }

    [Fact]
    public void Write_WritesJsonLines_InTheFormatOfTheContract_AndRemovesTheFileWhenItIsOff()
    {
        using var dir = new TempDir();
        var doc = Doc(DocumentKind.Docx, 2, H("# 1\\. 節", 1), P("一つ目。"), T("| a |\n|---|\n| 1 |", 2));
        doc.OutputFolder = dir.Path;
        doc.DisplayName = @"v1.2\基本設計\設計書.docx"; // as the list of files shows it: from the folder that was given
        var path = Path.Combine(dir.Path, "設計書_chunks.jsonl");

        ChunksWriter.Write(doc);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("一つ目。", text, StringComparison.Ordinal); // not escaped
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        Assert.Equal(["schemaVersion", "id", "prev", "next", "source", "path", "sourceModified", "markdown", "headings", "kind", "pages", "pageImages", "text"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("v1.2/基本設計/設計書.docx", root.GetProperty("path").GetString()); // the folders of the versions, with /
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("c0001", root.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("prev").ValueKind);
        Assert.Equal("c0002", root.GetProperty("next").GetString());
        Assert.Equal("設計書.docx", root.GetProperty("source").GetString()); // the name only: not the folders of the user
        Assert.Equal("2026-11-15T07:38:12Z", root.GetProperty("sourceModified").GetString());
        Assert.Equal("設計書.md", root.GetProperty("markdown").GetString());
        Assert.Equal(["1. 節"], root.GetProperty("headings").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([1, 1], root.GetProperty("pages").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(["images/page-001.png"], root.GetProperty("pageImages").EnumerateArray().Select(e => e.GetString()));
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("c0001", second.RootElement.GetProperty("prev").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next").ValueKind);

        doc.Chunks = false;
        ChunksWriter.Write(doc);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(@"v1.2\設計書.docx", "v1.2/設計書.docx")]
    [InlineData("設計書.docx", "設計書.docx")]
    [InlineData("", "設計書.docx")] // a document from before the name was kept: its file name
    [InlineData(@"C:\Users\someone\設計書.docx", "設計書.docx")] // never the folders from the root of a drive
    [InlineData(@"..\..\設計書.docx", "設計書.docx")]
    public void ThePath_IsFromTheFolderThatWasGiven_AndNeverFromTheRootOfADrive(string displayName, string expected)
    {
        var doc = Doc(DocumentKind.Docx, 1);
        doc.DisplayName = displayName;

        Assert.Equal(expected, ChunksWriter.RelativePath(doc));
    }

    [Fact]
    public void OddCharacters_GoThroughTheJsonUnchanged()
    {
        using var dir = new TempDir();
        var odd = "引用 \"x\" と \\ の C:\\path、タブ\tと制御文字\u0001、絵文字😀、<!-- ではない -- 記号 & < >";
        var doc = Doc(DocumentKind.Docx, 1, H("# 見出し \"引用\" \\ 😀", 1), P(odd));
        doc.OutputFolder = dir.Path;

        ChunksWriter.Write(doc);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, "設計書_chunks.jsonl")));
        Assert.DoesNotContain('\u0001', line); // escaped: one chunk stays on one line, and readers get a valid JSON
        using var json = JsonDocument.Parse(line);
        Assert.Equal(odd, json.RootElement.GetProperty("text").GetString());
        Assert.Equal("見出し \"引用\" \\ 😀", json.RootElement.GetProperty("headings")[0].GetString());
    }

    [Fact]
    public void TheMarkdownWritesTheChunks_OnlyWhenTheDocumentAsksForThem()
    {
        using var dir = new TempDir();
        var doc = Doc(DocumentKind.Docx, 1, H("# 節", 1), P("本文。"));
        doc.OutputFolder = dir.Path;

        MarkdownRenderer.Write(doc);
        Assert.True(File.Exists(Path.Combine(dir.Path, "設計書_chunks.jsonl")));

        doc.Chunks = false;
        MarkdownRenderer.Write(doc);
        Assert.False(File.Exists(Path.Combine(dir.Path, "設計書_chunks.jsonl")));
    }

    [Fact]
    public void ADocumentWithoutText_GivesAnEmptyFile()
    {
        using var dir = new TempDir();
        var doc = Doc(DocumentKind.Docx, 1);
        doc.OutputFolder = dir.Path;

        ChunksWriter.Write(doc);

        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(dir.Path, "設計書_chunks.jsonl")));
    }

    [Theory]
    [InlineData("2\\. データモデル", "2. データモデル")]
    [InlineData("`markporto convert <入力\\.\\.\\.>`", "`markporto convert <入力...>`")]
    [InlineData("A\\_B \\| C", "A_B | C")]
    public void TheHeadings_AreWithoutTheEscapesOfMarkdown(string heading, string expected) =>
        Assert.Equal(expected, ChunkBuilder.Unescape(heading));
}
