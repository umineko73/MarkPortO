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

using System.Text.RegularExpressions;
using MarkPortO.Core.Logging;

namespace MarkPortO.Core.Vlm.Command;

/// <summary>
/// Tells the kind of a failure from the words of the command (FR-049). The words change with the versions of the
/// commands, so this is a short table; what it does not know is <see cref="VlmFailureKind.Unknown"/>.
/// </summary>
public static partial class FailureClassifier
{
    public const int MaxSummaryLength = 200;

    public static VlmFailureKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return VlmFailureKind.Unknown;
        }

        if (UsageLimitPattern().IsMatch(message))
        {
            return VlmFailureKind.UsageLimit;
        }

        if (NotLoggedInPattern().IsMatch(message))
        {
            return VlmFailureKind.NotLoggedIn;
        }

        if (RateLimitPattern().IsMatch(message))
        {
            return VlmFailureKind.RateLimit;
        }

        return ImageTooLargePattern().IsMatch(message) ? VlmFailureKind.ImageTooLarge : VlmFailureKind.Unknown;
    }

    /// <summary>The first line of a message, short, with the known secrets hidden: what is shown and logged about a failure.</summary>
    public static string Summarize(string? message)
    {
        var line = (message ?? string.Empty).Split('\n', 2)[0].Trim();
        line = SecretRegistry.Shared.Redact(line);
        return line.Length <= MaxSummaryLength ? line : line[..(MaxSummaryLength - 1)] + "…";
    }

    [GeneratedRegex(@"usage limit|limit reached|reached your|quota|credit balance|out of credits|weekly limit|insufficient_quota", RegexOptions.IgnoreCase)]
    private static partial Regex UsageLimitPattern();

    [GeneratedRegex(@"not logged in|log ?in\b|login|unauthori[sz]ed|authentication|invalid api key|\b401\b|no credentials", RegexOptions.IgnoreCase)]
    private static partial Regex NotLoggedInPattern();

    [GeneratedRegex(@"rate[ _-]?limit|too many requests|overloaded|\b429\b|\b529\b", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(@"too large|exceeds the maximum|payload too large|\b413\b|image dimensions", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTooLargePattern();
}
