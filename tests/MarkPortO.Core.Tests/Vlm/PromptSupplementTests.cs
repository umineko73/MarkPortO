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

using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

/// <summary>FR-042: the fixed words that follow the prompt of the user.</summary>
public class PromptSupplementTests
{
    [Fact]
    public void TheSupplement_FollowsThePromptOfTheUser()
    {
        var text = PromptSupplement.Build("USER-PROMPT", "en");

        Assert.StartsWith("USER-PROMPT ", text);
        Assert.Contains("header row", text);
    }

    [Fact]
    public void TheSupplement_IsWrittenInTheLanguageOfTheConversion()
    {
        Assert.Contains("見出し行", PromptSupplement.Build("p", "ja"));
        Assert.DoesNotContain("見出し行", PromptSupplement.Build("p", "en"));
    }

    [Fact]
    public void ABand_SaysWhichPartOfThePageItIs()
    {
        var text = PromptSupplement.Build("p", "en", band: 2, bands: 3);

        Assert.Contains("part 2 of 3", text);
    }

    [Fact]
    public void AWholePage_DoesNotTalkAboutParts()
    {
        Assert.DoesNotContain("part", PromptSupplement.Build("p", "en"));
        Assert.DoesNotContain("part", PromptSupplement.Build("p", "en", band: 1, bands: 1));
    }
}
