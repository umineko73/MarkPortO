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

using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>FR-049, FR-057, SC-026, SC-028, SC-029: when Step 3 stops starting new readings.</summary>
public class Step3GovernorTests
{
    [Fact]
    public void WithoutLimits_ReadingNeverStops()
    {
        var governor = new Step3Governor();

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(governor.TryBegin());
            governor.Succeeded(0.5);
        }

        Assert.Equal(Step3Stop.None, governor.Stop);
        Assert.Null(governor.Reason);
    }

    [Fact]
    public void ThePageLimit_StopsNewReadings_AfterThatManyPages()
    {
        var governor = new Step3Governor(maxPages: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(governor.TryBegin());
            governor.Succeeded(null);
        }

        Assert.False(governor.TryBegin());
        Assert.Equal(Step3Stop.PageLimit, governor.Stop);
        Assert.Contains("3", governor.Reason);
    }

    [Fact]
    public void ThePagesAlreadyReadBeforeAResume_CountToo()
    {
        var governor = new Step3Governor(maxPages: 10, pagesRead: 10);

        Assert.False(governor.TryBegin());
    }

    [Fact]
    public void RaisingTheLimit_AllowsTheResumeToGoOn()
    {
        Assert.False(new Step3Governor(maxPages: 10, pagesRead: 10).TryBegin());
        Assert.True(new Step3Governor(maxPages: 20, pagesRead: 10).TryBegin());
        Assert.True(new Step3Governor(maxPages: null, pagesRead: 10).TryBegin());
    }

    [Fact]
    public void TheCostLimit_StopsNewReadings_OnceTheCostIsReached()
    {
        var governor = new Step3Governor(maxCostUsd: 0.10);

        governor.Succeeded(0.04);
        governor.Succeeded(0.04);
        Assert.True(governor.TryBegin()); // 0.08 is below the limit
        governor.Succeeded(0.04);

        Assert.False(governor.TryBegin());
        Assert.Equal(Step3Stop.CostLimit, governor.Stop);
    }

    [Fact]
    public void ACostThatIsNotReported_IsNotCounted()
    {
        var governor = new Step3Governor(maxCostUsd: 0.01);

        for (var i = 0; i < 100; i++)
        {
            governor.Succeeded(null);
        }

        Assert.True(governor.TryBegin());
    }

    [Theory]
    [InlineData(VlmFailureKind.UsageLimit, Step3Stop.UsageLimit)]
    [InlineData(VlmFailureKind.NotLoggedIn, Step3Stop.NotLoggedIn)]
    public void AUsageLimitOrALogin_StopsAtOnce(VlmFailureKind kind, Step3Stop stop)
    {
        var governor = new Step3Governor();

        governor.Failed(kind, "the message");

        Assert.False(governor.TryBegin());
        Assert.Equal(stop, governor.Stop);
        Assert.Contains("the message", governor.Reason);
    }

    [Theory]
    [InlineData(VlmFailureKind.Timeout)]
    [InlineData(VlmFailureKind.BadOutput)]
    [InlineData(VlmFailureKind.Unknown)]
    public void ThreeFailuresOfTheSameKindInARow_Stop_SC028(VlmFailureKind kind)
    {
        var governor = new Step3Governor();

        governor.Failed(kind, "x");
        governor.Failed(kind, "x");
        Assert.True(governor.TryBegin());
        governor.Failed(kind, "x");

        Assert.False(governor.TryBegin());
        Assert.Equal(Step3Stop.RepeatedFailures, governor.Stop);
    }

    [Fact]
    public void ASuccessInBetween_StartsTheCountAgain()
    {
        var governor = new Step3Governor();

        governor.Failed(VlmFailureKind.Unknown, "x");
        governor.Failed(VlmFailureKind.Unknown, "x");
        governor.Succeeded(null);
        governor.Failed(VlmFailureKind.Unknown, "x");
        governor.Failed(VlmFailureKind.Unknown, "x");

        Assert.True(governor.TryBegin());
    }

    [Fact]
    public void FailuresOfDifferentKinds_DoNotAddUp()
    {
        var governor = new Step3Governor();

        governor.Failed(VlmFailureKind.Timeout, "x");
        governor.Failed(VlmFailureKind.BadOutput, "x");
        governor.Failed(VlmFailureKind.Unknown, "x");

        Assert.True(governor.TryBegin());
    }

    [Fact]
    public void AFailureThatIsNotCounted_DoesNotStop_AndDoesNotResetTheCount()
    {
        var governor = new Step3Governor();

        governor.Failed(VlmFailureKind.Unknown, "x");
        governor.Failed(VlmFailureKind.Unknown, "x");
        governor.Failed(VlmFailureKind.ImageTooLarge, "big");
        Assert.True(governor.TryBegin());
        governor.Failed(VlmFailureKind.Unknown, "x");

        Assert.False(governor.TryBegin());
    }

    [Fact]
    public void OnceStopped_ItStaysStopped()
    {
        var governor = new Step3Governor();
        governor.Failed(VlmFailureKind.UsageLimit, "limit");

        governor.Succeeded(null);

        Assert.False(governor.TryBegin());
        Assert.Equal(Step3Stop.UsageLimit, governor.Stop);
    }
}
