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

using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Tests.Jobs;

/// <summary>FR-157: the files that "Resume" takes.</summary>
public class FileStatesResumeTests
{
    private static FileEntry Entry(string name, FileState state, bool recorded = true, bool finished = true) => new(
        name,
        name,
        DocumentKind.Docx,
        recorded ? new JobState() : null,
        recorded ? new DocumentRecord { Step1 = finished ? StepStatus.Done : StepStatus.Pending, Step2 = StepStatus.NotApplicable, Step3 = StepStatus.NotApplicable } : null,
        state);

    [Fact]
    public void OnlyNotConvertedFiles_AreForStart_NotForResume() =>
        Assert.Empty(FileStates.ToResume([Entry("a", FileState.NotConverted, recorded: false)]));

    [Fact]
    public void NotConvertedFiles_GoWithTheRecordedOnes() =>
        Assert.Equal(["b"], FileStates.ToResume([Entry("a", FileState.Done), Entry("b", FileState.NotConverted, recorded: false)]).Select(e => e.Path));

    [Fact]
    public void AFileRunElsewhere_IsNeverTaken_NorCountsAsARecord()
    {
        Assert.Empty(FileStates.ToResume([Entry("a", FileState.RunningElsewhere, finished: false), Entry("b", FileState.NotConverted, recorded: false)]));
        Assert.Empty(FileStates.ToResume([Entry("a", FileState.Done), Entry("c", FileState.Changed)])); // a finished changed file is for "changed files"
    }

    [Fact]
    public void StepsToAdd_Retries_AndUnfinishedChangedFiles_AreTaken() =>
        Assert.Equal(
            ["s", "p", "u", "c"],
            FileStates.ToResume([
                Entry("s", FileState.StepPending),
                Entry("p", FileState.DonePartlyFailed),
                Entry("u", FileState.Unfinished, finished: false),
                Entry("c", FileState.Changed, finished: false),
                Entry("d", FileState.Done),
            ]).Select(e => e.Path));
}
