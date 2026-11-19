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

/// <summary>A part of a page that the model wrote again further down, in another form, is cut.</summary>
public class RepeatedSectionTests
{
    // What a model wrote for a page of a design document: the code block, then the headings again (at another level) and the code as a list.
    private const string Once =
        "| 原則 | 判定 |\n|---|---|\n| 再開できる | 合格 |\n\n# Project Structure\n\n## Documentation (this feature)\n\n```\nspecs/001-office-to-markdown/\n"
        + "├── plan.md              # この文書\n├── research.md          # Part 0: 技術選定と検証が必要な点（スパイク）\n├── data-model.md        # Part 1: 設定・ジョブ・状態のデータ\n"
        + "├── quickstart.md        # Part 1: 動作確認の手順\n└── tasks.md             # Part 2: /speckit-tasks の出力\n```";

    private const string Again =
        "\n\n### Project Structure\n\n#### Documentation (this feature)\n\n* plan.md # この文書\n* research.md # Part 0: 技術選定と検証が必要な点（スパイク）\n"
        + "* data-model.md # Part 1: 設定・ジョブ・状態のデータ\n* quickstart.md # Part 1: 動作確認の手順";

    [Fact]
    public void ThePartWrittenAgainAsAList_IsCut()
    {
        Assert.Equal(Once, RepeatedStart.TrimSection(Once + Again));
    }

    [Fact]
    public void TheSameHeadingTwice_WithNewTextAfterIt_IsKept()
    {
        var text = Once + "\n\n### Project Structure\n\n#### Documentation (this feature)\n\n* 新しい項目の説明です\n* もう一つの新しい項目\n* 三つ目の新しい項目";

        Assert.Equal(text, RepeatedStart.TrimSection(text));
    }

    [Fact]
    public void APageWithoutARepeat_IsKept()
    {
        Assert.Equal(Once, RepeatedStart.TrimSection(Once));
    }
}
