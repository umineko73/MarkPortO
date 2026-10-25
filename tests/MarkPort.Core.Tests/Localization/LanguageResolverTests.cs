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

using System.Globalization;
using MarkPort.Core.Localization;

namespace MarkPort.Core.Tests.Localization;

/// <summary>FR-033, SC-018: the language follows the operating system, and anything without a translation is English.</summary>
public class LanguageResolverTests
{
    [Theory]
    [InlineData("ja-JP", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("zh-CN", "en")]
    public void System_FollowsTheOperatingSystem_AndFallsBackToEnglish(string operatingSystem, string expected)
    {
        Assert.Equal(expected, LanguageResolver.Resolve("system", CultureInfo.GetCultureInfo(operatingSystem)));
    }

    [Theory]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("en", "ja-JP", "en")]
    public void AChosenLanguage_IsUsedWhateverTheOperatingSystemSays(string setting, string operatingSystem, string expected)
    {
        Assert.Equal(expected, LanguageResolver.Resolve(setting, CultureInfo.GetCultureInfo(operatingSystem)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("klingon")]
    public void AnUnknownSetting_IsTakenAsSystem(string? setting)
    {
        Assert.Equal("ja", LanguageResolver.Resolve(setting, CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("en", LanguageResolver.Resolve(setting, CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void Loc_UsesTheTranslation_ForTheLanguageOfTheThread_AndEnglishOtherwise()
    {
        using (TestCulture.Use("ja"))
        {
            Assert.Equal("完了", Loc.T("Done"));
        }

        using (TestCulture.Use("en"))
        {
            Assert.Equal("Done", Loc.T("Done"));
        }

        using (TestCulture.Use("fr"))
        {
            Assert.Equal("Done", Loc.T("Done")); // no French: English
        }
    }

    [Fact]
    public void LocM_UsesTheLanguageItIsGiven_NotTheOneOfTheThread()
    {
        using var english = TestCulture.Use("en");

        Assert.Equal("ページ 3", Loc.M("alt|Page {0}", "ja", 3));
        Assert.Equal("Page 3", Loc.M("alt|Page {0}", "en", 3)); // the context before the bar is not shown
    }

    [Fact]
    public void ThePageLabel_IsSavedAsACode_AndShownInAnyLanguage()
    {
        Assert.Equal("3ページ目", PageLabel.Text(PageLabel.Page(3), "ja"));
        Assert.Equal("Page 3", PageLabel.Text(PageLabel.Page(3), "en"));
        Assert.Equal("3枚目のスライド", PageLabel.Text(PageLabel.Slide(3), "ja"));
        Assert.Equal("Slide 3", PageLabel.Text(PageLabel.Slide(3), "en"));
        Assert.Equal("シート「売上」", PageLabel.Text(PageLabel.Sheet("売上"), "ja"));
        Assert.Equal("Sheet \"Sales\"", PageLabel.Text(PageLabel.Sheet("Sales"), "en"));
    }
}
