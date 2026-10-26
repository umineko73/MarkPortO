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
using MarkPortO.Core.Vlm.Command;

namespace MarkPortO.Core.Tests.Vlm.Command;

/// <summary>FR-049: the kinds of failure of a command.</summary>
public class ErrorClassifierTests
{
    [Theory]
    [InlineData("Not logged in. Please run /login", VlmFailureKind.NotLoggedIn)]
    [InlineData("Invalid API key", VlmFailureKind.NotLoggedIn)]
    [InlineData("401 Unauthorized", VlmFailureKind.NotLoggedIn)]
    [InlineData("You must log in to continue", VlmFailureKind.NotLoggedIn)]
    [InlineData("You have reached your usage limit. Try again at 5pm.", VlmFailureKind.UsageLimit)]
    [InlineData("Claude usage limit reached|1760000000", VlmFailureKind.UsageLimit)]
    [InlineData("insufficient_quota: You exceeded your current quota", VlmFailureKind.UsageLimit)]
    [InlineData("Your credit balance is too low", VlmFailureKind.UsageLimit)]
    [InlineData("Rate limit exceeded (429 Too Many Requests).", VlmFailureKind.RateLimit)]
    [InlineData("rate_limit_error: slow down", VlmFailureKind.RateLimit)]
    [InlineData("The server is overloaded", VlmFailureKind.RateLimit)]
    [InlineData("The image is too large: it exceeds the maximum size.", VlmFailureKind.ImageTooLarge)]
    [InlineData("413 Payload Too Large", VlmFailureKind.ImageTooLarge)]
    [InlineData("Something unexpected went wrong.", VlmFailureKind.Unknown)]
    [InlineData("The 'gpt-4o' model is not supported when using Codex with a ChatGPT account.", VlmFailureKind.Unknown)]
    [InlineData("", VlmFailureKind.Unknown)]
    public void TheKind_IsDecidedByTheMessage(string message, VlmFailureKind kind)
    {
        Assert.Equal(kind, FailureClassifier.Classify(message));
    }

    [Fact]
    public void AUsageLimit_IsNotMistakenForARateLimit()
    {
        Assert.Equal(VlmFailureKind.UsageLimit, FailureClassifier.Classify("You hit your usage limit (rate limit: 5 hours)"));
    }

    [Fact]
    public void ANumberThatIsPartOfAnotherNumber_IsNotAStatusCode()
    {
        Assert.Equal(VlmFailureKind.Unknown, FailureClassifier.Classify("processed 14012 tokens in 4290 ms"));
    }

    [Theory]
    [InlineData(VlmFailureKind.NotLoggedIn, true)]
    [InlineData(VlmFailureKind.UsageLimit, true)]
    [InlineData(VlmFailureKind.RateLimit, false)]
    [InlineData(VlmFailureKind.Timeout, false)]
    [InlineData(VlmFailureKind.Unknown, false)]
    [InlineData(VlmFailureKind.BadOutput, false)]
    public void OnlyALoginOrALimit_StopsTheRunAtOnce(VlmFailureKind kind, bool stops)
    {
        Assert.Equal(stops, kind.StopsTheRun());
    }

    [Theory]
    [InlineData(VlmFailureKind.Timeout, true)]
    [InlineData(VlmFailureKind.BadOutput, true)]
    [InlineData(VlmFailureKind.Unknown, true)]
    [InlineData(VlmFailureKind.RateLimit, false)]
    [InlineData(VlmFailureKind.UsageLimit, false)]
    [InlineData(VlmFailureKind.ImageTooLarge, false)]
    public void TimeOutsBadOutputAndUnknownFailures_AreCountedForThreeInARow(VlmFailureKind kind, bool counted)
    {
        Assert.Equal(counted, kind.CountsTowardsStopping());
    }

    [Fact]
    public void TheMessageOfAFailure_IsShortAndHasNoSecrets()
    {
        var text = FailureClassifier.Summarize("first line with a key sk-ABCDEF1234567890ABCDEF and a lot more " + new string('x', 500) + "\nsecond line");

        Assert.DoesNotContain("second line", text);
        Assert.True(text.Length <= 200);
    }
}
