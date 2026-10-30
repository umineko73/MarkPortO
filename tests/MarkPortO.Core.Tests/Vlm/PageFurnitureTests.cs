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
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

/// <summary>The header, the footer and the page number are not in the result of a page; a page written twice is cut (FR-100).</summary>
public class PageFurnitureTests
{
    private static readonly string[] Furniture = [PageFurniture.Normalise("機器一覧（社外秘）"), PageFurniture.Normalise("ページ 1")];

    [Fact]
    public void TheHeaderAndTheFooter_AreTakenOutOfTheEnds()
    {
        const string text = "機器一覧（社外秘）\n\n| 番号 | 名前 |\n|---|---|\n| 16 | A |\n| 17 | B |\n\nページ 2";

        Assert.Equal("| 番号 | 名前 |\n|---|---|\n| 16 | A |\n| 17 | B |", PageFurniture.Strip(text, Furniture));
    }

    [Fact]
    public void AWrongCharacterInTheHeader_DoesNotMatter()
    {
        Assert.Equal("本文", PageFurniture.Strip("機器一覧（仕外税）\n本文", Furniture));
    }

    [Theory]
    [InlineData("- 12 -")]
    [InlineData("ページ12")]
    [InlineData("Page 3 of 9")]
    [InlineData("3 / 9")]
    public void APageNumber_IsTakenOut(string number)
    {
        Assert.Equal("本文", PageFurniture.Strip("本文\n\n" + number, Furniture));
    }

    [Fact]
    public void ALineOfATable_IsNeverTaken()
    {
        const string text = "| 機器一覧（社外秘） | 2 |\n|---|---|\n| a | b |";

        Assert.Equal(text, PageFurniture.Strip(text, Furniture));
    }

    [Fact]
    public void ATextInTheMiddle_IsKept()
    {
        const string text = "一\n二\n三\n四\n五\n機器一覧（社外秘）\n六\n七\n八\n九\n十";

        Assert.Equal(text, PageFurniture.Strip(text, Furniture));
    }

    [Fact]
    public void WithoutAHeaderOrFooter_NothingIsTaken()
    {
        Assert.Equal("ページ 5\n本文", PageFurniture.Strip("ページ 5\n本文", []));
    }

    [Fact]
    public void AResultOfNothingButTheHeader_IsLeftAsItIs()
    {
        Assert.Equal("機器一覧（社外秘）", PageFurniture.Strip("機器一覧（社外秘）", Furniture));
    }

    [Fact]
    public void ThePageWrittenTwice_IsCutAtTheSecondCopy_AndTheHeaderBetweenIsTakenOut()
    {
        const string text = "# 機器一覧\n\n表 1 設置機器の一覧\n\n| 番号 | 名前 |\n|---|---|\n| 1 | A |\n\n---\n\n機器一覧（社外秘）\n\n# 機器一覧\n\n表 1 設置機器の一覧\n\n| 番号 | 名前 |\n|---|---|\n| 1 | A（もう一度） |";

        var result = PageFurniture.Strip(RepeatedStart.Trim(PageFurniture.Strip(text, Furniture)), Furniture);

        Assert.Equal("# 機器一覧\n\n表 1 設置機器の一覧\n\n| 番号 | 名前 |\n|---|---|\n| 1 | A |", result);
    }

    [Fact]
    public void ATableThatStartsAPage_IsNotTakenForARepeat()
    {
        const string text = "| 番号 | 名前 |\n|---|---|\n| 1 | A |\n\n| 番号 | 名前 |\n|---|---|\n| 2 | B |";

        Assert.Equal(text, RepeatedStart.Trim(text));
    }

    [Fact]
    public void ThePromptOfWord_AsksForOneCopy_AndTheEarlierOnesAreStillTheBuiltInOne()
    {
        var ja = PromptSettings.DefaultFor(DocumentKind.Docx, "ja");
        Assert.Contains("1回だけ書きます", ja);
        Assert.Contains("ヘッダー、フッター、ページ番号", ja);
        Assert.Contains("write only the rows that are on this page", PromptSettings.DefaultFor(DocumentKind.Docx, "en"));
        Assert.DoesNotContain("1回だけ書きます", PromptSettings.DefaultFor(DocumentKind.Pptx, "ja"));

        var earlier = ja[..ja.IndexOf("\n\n次の決まりを守ってください。", StringComparison.Ordinal)] + "\n\n抽出テキスト:\n{pagetext}";
        Assert.NotEqual(ja, earlier);
        Assert.True(PromptSettings.IsDefault(DocumentKind.Docx, earlier));
        Assert.Equal(ja, new PromptSettings { Docx = earlier }.For(DocumentKind.Docx, "ja"));
        Assert.False(PromptSettings.IsDefault(DocumentKind.Docx, earlier + "x"));
    }
}
