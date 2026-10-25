// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using MarkPort.Core.Jobs;

namespace MarkPort.Core.Convert;

/// <summary>What to resume (the input of <see cref="ConvertService.ResumeAsync"/>).</summary>
public sealed class ResumeRequest
{
    /// <summary>The output folder of the earlier run (<c>--out</c>); the job's state is kept inside it.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>The job to resume. If not given, the newest resumable job whose inputs match is used.</summary>
    public Guid? JobId { get; init; }

    /// <summary>The inputs the user names now; used to pick the job (empty: any job in the output folder).</summary>
    public IReadOnlyList<string> GivenInputs { get; init; } = [];

    public ResumeMode Mode { get; init; } = ResumeMode.Unfinished;

    /// <summary>The step selection of this run. <c>null</c>: as in the earlier run.</summary>
    public bool? Step2 { get; init; }

    public bool? Step3 { get; init; }

    public string? Connection { get; init; }
}
