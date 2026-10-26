// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPort.Core.Pdf;
using MarkPort.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MarkPort.Core.Tests.Pdf;

/// <summary>FR-041: the lines of a page, from the positions of the letters in the PDF.</summary>
public class PdfLineExtractorTests
{
    private static string MakePdf(string path, int lines, bool twoColumns = false)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        for (var i = 0; i < lines; i++)
        {
            var y = 800 - (i * 20);
            page.AddText($"Line {i + 1}", 12, new PdfPoint(40, y), font);
            if (twoColumns)
            {
                page.AddText("cell", 12, new PdfPoint(300, y), font);
            }
        }

        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public void EveryLineOfText_IsOneSpan_FromTopToBottom()
    {
        using var dir = new TempDir();
        var pdf = MakePdf(dir.Combine("a.pdf"), 30);

        var lines = PdfLineExtractor.GetLines(pdf, 0);

        Assert.Equal(30, lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            Assert.InRange(lines[i].Top, 0.0, 1.0);
            Assert.True(lines[i].Bottom > lines[i].Top);
            if (i > 0)
            {
                Assert.True(lines[i].Top >= lines[i - 1].Bottom, "lines are in order and do not overlap");
            }
        }
    }

    [Fact]
    public void TextInTwoColumnsOnTheSameLine_IsOneLine()
    {
        using var dir = new TempDir();
        var pdf = MakePdf(dir.Combine("a.pdf"), 10, twoColumns: true);

        Assert.Equal(10, PdfLineExtractor.GetLines(pdf, 0).Count);
    }

    [Fact]
    public void TheFirstLine_IsNearTheTopOfThePage()
    {
        using var dir = new TempDir();
        var pdf = MakePdf(dir.Combine("a.pdf"), 3);

        var first = PdfLineExtractor.GetLines(pdf, 0)[0];

        // y = 800 of 842 points: about 5% from the top.
        Assert.InRange(first.Top, 0.03, 0.07);
    }

    [Fact]
    public void APageWithoutText_HasNoLines()
    {
        using var dir = new TempDir();
        var pdf = MakePdf(dir.Combine("a.pdf"), 0);

        Assert.Empty(PdfLineExtractor.GetLines(pdf, 0));
    }

    [Fact]
    public void AnUnreadableFile_GivesNull_SoThePageIsReadWhole()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("bad.pdf"), "not a pdf");

        Assert.Null(PdfLineExtractor.TryGetLines(dir.Combine("bad.pdf"), 0));
    }
}
