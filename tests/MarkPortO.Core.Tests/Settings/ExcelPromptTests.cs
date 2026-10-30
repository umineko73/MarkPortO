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

namespace MarkPortO.Core.Tests.Settings;

/// <summary>The default prompt of Excel asks for a table whose rows all start and end with a pipe.</summary>
public class ExcelPromptTests
{
    [Fact]
    public void TheDefaultPromptOfExcel_AsksForSeparateTablesWhoseRowsStartAndEndWithAPipe()
    {
        var ja = PromptSettings.DefaultFor(DocumentKind.Xlsx, "ja");
        Assert.Contains("「|」で始めて「|」で終えます", ja);
        Assert.Contains("|---|---|", ja);
        Assert.Contains("まとまり", ja);
        var en = PromptSettings.DefaultFor(DocumentKind.Xlsx, "en");
        Assert.Contains("Start every row with", en);
        Assert.Contains("separate table for each group", en);
        Assert.DoesNotContain("Start every row with", PromptSettings.DefaultFor(DocumentKind.Docx, "en"));
    }

    [Fact]
    public void ThePromptsOfEarlierVersions_AreStillTheBuiltInOne_AndAnEditedOneIsKept()
    {
        var ja = PromptSettings.DefaultFor(DocumentKind.Xlsx, "ja");
        var cut = ja.IndexOf("\n\n表は、次の決まり", StringComparison.Ordinal);
        var basis = ja[..cut];
        var tail = "\n\n抽出テキスト:\n{pagetext}";
        var beforeTheRule = basis + tail; // 0.1.4: no rule for the table
        var pipeRule = basis + "表の行は、すべて「|」で始めて「|」で終え、列の数を見出し行と同じにしてください（空のセルも省かず、「|」と「|」の間を空けて書き、列をずらさないでください）。" + tail; // 0.1.5

        foreach (var earlier in new[] { beforeTheRule, pipeRule })
        {
            Assert.NotEqual(ja, earlier);
            Assert.True(PromptSettings.IsDefault(DocumentKind.Xlsx, earlier));
            Assert.False(PromptSettings.IsDefault(DocumentKind.Xlsx, earlier + "my own line"));
            Assert.Equal(ja, new PromptSettings { Xlsx = earlier }.For(DocumentKind.Xlsx, "ja"));
        }
    }
}
