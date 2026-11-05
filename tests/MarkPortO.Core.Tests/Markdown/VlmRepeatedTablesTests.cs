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
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Tests.Markdown;

public class VlmRepeatedTablesTests
{
    private const string Mark = "> N";

    private const string Big = "| | t1 | t2 |\n|---|---|---|\n| a | 1 | 2 |\n| b | 3 | 4 |\n| c | 5 | 6 |";

    // The same cells, a cell more or less in a row, and no empty cell in front: what a small model writes when it goes on.
    private const string BigAgain = "| t1 | t2 |\n|---|---|\n| a | 1 | 2 |\n| b | 3 | 4 |\n| c | 5 | 6 |";

    private const string Small = "| x | y |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";

    [Fact]
    public void ATableThatCameBefore_WithTheSameCells_IsLeftOut()
    {
        var text = Big + "\n\n" + Small + "\n\n" + BigAgain;

        var result = VlmRepeatedTables.Remove(text, Mark);

        Assert.Equal(Big + "\n\n" + Small + "\n\n" + Mark, result);
    }

    [Fact]
    public void TheTitleLineBeforeARepeatedTable_IsLeftOutWhenTheSameLineCameBefore()
    {
        var text = Big + "\n\n# TITLE\n\n" + Small + "\n\n" + BigAgain + "\n\n# TITLE\n\n" + BigAgain;

        var result = VlmRepeatedTables.Remove(text, Mark);

        Assert.Equal(Big + "\n\n# TITLE\n\n" + Small + "\n\n" + Mark, result);
    }

    [Fact]
    public void ATitleThatIsNew_IsKept()
    {
        var text = Big + "\n\n# OTHER\n\n" + BigAgain;

        var result = VlmRepeatedTables.Remove(text, Mark);

        Assert.Equal(Big + "\n\n# OTHER\n\n" + Mark, result);
    }

    [Fact]
    public void ATableOfDifferentCells_IsKept()
    {
        var text = Big + "\n\n" + Big.Replace("| 5 |", "| 9 |", StringComparison.Ordinal);

        Assert.Equal(text, VlmRepeatedTables.Remove(text, Mark));
    }

    [Fact]
    public void ASmallTableWritenTwice_IsKept()
    {
        var text = "| x | y |\n|---|---|\n| 1 | 2 |\n\n| x | y |\n|---|---|\n| 1 | 2 |";

        Assert.Equal(text, VlmRepeatedTables.Remove(text, Mark));
    }

    [Fact]
    public void ATextWithoutRepeats_IsNotChanged()
    {
        var text = "Intro\n\n" + Big + "\n\nEnd";

        Assert.Equal(text, VlmRepeatedTables.Remove(text, Mark));
    }

    [Fact]
    public void Render_DropsTheRepeats_OnlyWhenItIsSwitchedOn()
    {
        var page = new PageRecord { Index = 1, Step3 = PageStep3Status.Done, VlmText = Big + "\n\n" + BigAgain };
        var document = new DocumentRecord { Pages = [page], VlmSeparate = false, DropRepeatedTables = true };

        var on = VlmRepeatedTables.Apply(document, new Dictionary<int, string>(), "en");

        Assert.Equal(Big + "\n\n> A table that repeats an earlier table of this page is left out here.", on[1]);

        document.DropRepeatedTables = false;
        var rendered = MarkdownRenderer.RenderVlm(document);
        Assert.Contains("| t1 | t2 |", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("left out here", rendered, StringComparison.Ordinal);
    }
}
