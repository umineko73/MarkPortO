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

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// A table or a code block that runs over several pages, for the prompts of the VLM. The VLM sees one page image at a time, so the page where
/// it starts is given the rest from the later pages in its text (and writes the whole of it), and a later page is told that the rest is already
/// written. The parts are the blocks of Step1 with <see cref="Block.JoinedToPage"/>.
/// </summary>
internal static class ContinuedTables
{
    /// <param name="Heads">The block where a table or a code block starts (that goes on) → the blocks of its later parts.</param>
    /// <param name="Continuations">A block of a later part → the block where it starts.</param>
    internal sealed record Chains(IReadOnlyDictionary<int, List<int>> Heads, IReadOnlyDictionary<int, int> Continuations);

    /// <summary>
    /// The tables (when the tables of the VLM are used: with those of Step1 the rows are not read from the image) and the code blocks that go on.
    /// <paramref name="startsCarry"/> tells whether the page where one starts takes the rest of it from the later pages.
    /// </summary>
    public static Chains Find(DocumentRecord document, IReadOnlyList<int> effectivePages, Func<int, bool> startsCarry)
    {
        var heads = new Dictionary<int, List<int>>();
        var continuations = new Dictionary<int, int>();
        var tableHead = -1;
        var codeHead = -1;

        void Add(int head, int part)
        {
            if (!heads.TryGetValue(head, out var parts))
            {
                heads[head] = parts = [];
            }

            parts.Add(part);
            continuations[part] = head;
        }

        // A part whose first part is not there (its page was a deleted page) stays one of its own, as in the Markdown of the document.
        bool Goes(int head, Block part) => head >= 0 && effectivePages[head] == part.JoinedToPage && startsCarry(effectivePages[head]);

        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (block.Kind is BlockKind.Table or BlockKind.SheetTable)
            {
                if (block.JoinedToPage is null)
                {
                    tableHead = i;
                }
                else if (document.Step1Tables == Step1TablesMode.Off && Goes(tableHead, block))
                {
                    Add(tableHead, i);
                }
            }
            else if (block.Kind == BlockKind.Paragraph && IsCode(block))
            {
                if (block.JoinedToPage is null)
                {
                    codeHead = i;
                }
                else if (Goes(codeHead, block))
                {
                    Add(codeHead, i);
                }
            }
        }

        return new Chains(heads, continuations);
    }

    public static bool IsCode(Block block) => CodeText.IsBlock(block.Markdown);

    /// <summary>The rows of a part of a table: its lines without the first two (the header row that is written again, or an empty one, and the separator).</summary>
    public static List<string> RowsOf(string markdown) => [.. markdown.Split('\n').Skip(2).Where(l => l.Trim().Length > 0)];

    /// <summary>The rows of a table of Step1, without its header row and the separator.</summary>
    public static int RowCount(string markdown) => RowsOf(markdown).Count;
}
