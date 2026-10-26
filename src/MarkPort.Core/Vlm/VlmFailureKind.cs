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

namespace MarkPort.Core.Vlm;

/// <summary>Why a call of an external command failed (FR-049).</summary>
public enum VlmFailureKind
{
    /// <summary>Not told apart from the others.</summary>
    Unknown,

    /// <summary>The command is not found or cannot be started.</summary>
    NotFound,

    NotLoggedIn,

    /// <summary>The usage limit of the account is reached.</summary>
    UsageLimit,

    RateLimit,

    Timeout,

    /// <summary>The output is not what the command prints, or it is empty.</summary>
    BadOutput,

    ImageTooLarge,

    Cancelled,
}

public static class VlmFailureKindExtensions
{
    /// <summary>A login or a limit that every other page would meet too: no new command is started (FR-049).</summary>
    public static bool StopsTheRun(this VlmFailureKind kind) => kind is VlmFailureKind.NotLoggedIn or VlmFailureKind.UsageLimit;

    /// <summary>Failures of which three in a row stop the run: the cause is probably not the page (FR-049).</summary>
    public static bool CountsTowardsStopping(this VlmFailureKind kind) =>
        kind is VlmFailureKind.Timeout or VlmFailureKind.BadOutput or VlmFailureKind.Unknown;
}
