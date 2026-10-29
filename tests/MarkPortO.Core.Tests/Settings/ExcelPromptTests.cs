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
    public void TheDefaultPromptOfExcel_AsksForRowsThatStartAndEndWithAPipe()
    {
        Assert.Contains("「|」で始めて「|」で終え", PromptSettings.DefaultFor(DocumentKind.Xlsx, "ja"));
        Assert.Contains("leading and a trailing", PromptSettings.DefaultFor(DocumentKind.Xlsx, "en"));
        Assert.DoesNotContain("leading and a trailing", PromptSettings.DefaultFor(DocumentKind.Docx, "en"));
    }

    [Fact]
    public void ThePromptOfBeforeTheRule_IsStillTheBuiltInOne_AndAnEditedOneIsKept()
    {
        var ja = PromptSettings.DefaultFor(DocumentKind.Xlsx, "ja");
        var start = ja.IndexOf("表の行は、すべて", StringComparison.Ordinal);
        var end = ja.IndexOf("抽出テキスト:", StringComparison.Ordinal) - 2; // the two line breaks before it stay
        var before = ja.Remove(start, end - start);

        Assert.NotEqual(ja, before);
        Assert.True(PromptSettings.IsDefault(DocumentKind.Xlsx, before));
        Assert.False(PromptSettings.IsDefault(DocumentKind.Xlsx, before + "my own line"));
        Assert.Equal(ja, new PromptSettings { Xlsx = before }.For(DocumentKind.Xlsx, "ja"));
    }
}
