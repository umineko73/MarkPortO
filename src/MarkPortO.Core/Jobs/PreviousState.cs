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

namespace MarkPortO.Core.Jobs;

/// <summary>"The first" of the inputs (FR-156): the input the output folder and the conditions follow.</summary>
/// <param name="Index">Its place in the list of inputs.</param>
/// <param name="Input">The input.</param>
/// <param name="Output">The output folder that has its records: the one set now, or the one remembered for it.</param>
/// <param name="Remembered">Found by the remembered output folder (the screen switches to it).</param>
public sealed record FirstInput(int Index, string Input, string Output, bool Remembered);

/// <summary>The conditions of the newest conversion of an input, and how many of its files were converted with other conditions.</summary>
public sealed record RecordedConditions(ConditionSet Conditions, int OthersDiffer);

/// <summary>Finds the state to put back when inputs are chosen (FR-156, FR-158). Reads only.</summary>
public static class PreviousState
{
    /// <summary>
    /// The first input (FR-156): the first that has records in the output folder set now; else the first whose remembered output folder has
    /// its records. <paramref name="missing"/> is the remembered folder that is gone, for the status, when no first was found by it.
    /// </summary>
    public static FirstInput? FindFirst(string output, IReadOnlyList<string> inputs, HistorySettings history, out string? missing)
    {
        missing = null;
        var here = FileStates.LastRecords(output);
        for (var i = 0; i < inputs.Count; i++)
        {
            if (FileStates.RecordsUnder(here, [inputs[i]]).Count > 0)
            {
                return new FirstInput(i, inputs[i], output, Remembered: false);
            }
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            if (history.OutputOf(inputs[i]) is not { } remembered || SameFolder(remembered, output))
            {
                continue;
            }

            var there = FileStates.LastRecords(remembered);
            if (FileStates.RecordsUnder(there, [inputs[i]]).Count > 0)
            {
                return new FirstInput(i, inputs[i], remembered, Remembered: true);
            }

            if (there.Count == 0)
            {
                missing ??= remembered; // gone, or no ".markporto" there any more
            }
        }

        return null;
    }

    /// <summary>The conditions of the newest conversion of the files under the input (FR-158); <c>null</c> when it has no record there.</summary>
    public static RecordedConditions? ConditionsOf(string output, string input) =>
        ConditionsOf(FileStates.RecordsUnder(output, [input]));

    public static RecordedConditions? ConditionsOf(IReadOnlyList<(JobState Job, DocumentRecord Document)> records)
    {
        if (records.Count == 0)
        {
            return null;
        }

        var newest = records.MaxBy(r => r.Job.CreatedAt);
        var conditions = ConditionSet.FromRecord(newest.Job, newest.Document);
        var others = records.Count(r => !ConditionSet.FromRecord(r.Job, r.Document).SameAs(conditions));
        return new RecordedConditions(conditions, others);
    }

    /// <summary>The output folder and the conditions an input brings when it is added (FR-160); <c>null</c> when it has no record anywhere.</summary>
    /// <param name="firstOutput">The output folder of the first (its records are used when it has some).</param>
    public static (string Output, ConditionSet? Conditions)? OfAdded(string input, string firstOutput, HistorySettings history)
    {
        var remembered = history.OutputOf(input);
        var inFirst = ConditionsOf(firstOutput, input);
        if (inFirst is not null)
        {
            return (remembered ?? firstOutput, inFirst.Conditions);
        }

        if (remembered is null)
        {
            return null;
        }

        return (remembered, ConditionsOf(remembered, input)?.Conditions);
    }

    public static bool SameFolder(string a, string b) =>
        string.Equals(HistorySettings.Normalize(a), HistorySettings.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
