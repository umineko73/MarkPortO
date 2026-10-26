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

using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Vlm.Command;

/// <summary>FR-046, FR-052, SC-023: which connections need the consent to send images outside, and when it is valid.</summary>
public class ConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ConnectionProfile OpenAi(string url) => new() { Name = "o", BaseUrl = url, Model = "m" };

    private static ConnectionProfile Command() => new() { Name = "c", Kind = ConnectionKind.Command, Command = CommandKind.Claude };

    [Fact]
    public void ALocalOpenAiConnection_NeedsNoConsent()
    {
        var c = OpenAi("http://192.168.11.220:1234/v1");

        Assert.False(ExternalConsent.IsRequired(c));
        Assert.Equal(ConsentState.NotRequired, ExternalConsent.StateOf(c));
    }

    [Fact]
    public void ARemoteOpenAiConnection_NeedsConsent()
    {
        var c = OpenAi("https://api.openai.com/v1");

        Assert.True(ExternalConsent.IsRequired(c));
        Assert.Equal(ConsentState.Missing, ExternalConsent.StateOf(c));
    }

    [Fact]
    public void ACommandConnection_AlwaysNeedsConsent_EvenWithoutAUrl()
    {
        Assert.True(ExternalConsent.IsRequired(Command()));
        Assert.Equal(ConsentState.Missing, ExternalConsent.StateOf(Command()));
    }

    [Fact]
    public void AfterGranting_TheConnectionMayBeUsed_AndTheDateIsKept()
    {
        var c = Command();

        ExternalConsent.Grant(c, Now);

        Assert.Equal(ConsentState.Granted, ExternalConsent.StateOf(c));
        Assert.Equal(Now, c.ExternalConsent!.At);
        Assert.Equal(ExternalConsent.CurrentNoticeVersion, c.ExternalConsent.NoticeVersion);
        Assert.True(ExternalConsent.MayUse(c));
    }

    [Fact]
    public void WithoutConsent_ARequiredConnectionMayNotBeUsed()
    {
        Assert.False(ExternalConsent.MayUse(Command()));
        Assert.False(ExternalConsent.MayUse(OpenAi("https://api.openai.com/v1")));
    }

    [Fact]
    public void ALocalConnection_MayAlwaysBeUsed()
    {
        Assert.True(ExternalConsent.MayUse(OpenAi("http://localhost:1234/v1")));
    }

    [Fact]
    public void WhenTheNoticeChanges_TheConsentMustBeGivenAgain()
    {
        var c = Command();
        c.ExternalConsent = new ConsentRecord { At = Now, NoticeVersion = ExternalConsent.CurrentNoticeVersion - 1 };

        Assert.Equal(ConsentState.Outdated, ExternalConsent.StateOf(c));
        Assert.False(ExternalConsent.MayUse(c));
    }

    [Fact]
    public void Revoking_RemovesTheConsent()
    {
        var c = Command();
        ExternalConsent.Grant(c, Now);

        ExternalConsent.Revoke(c);

        Assert.Null(c.ExternalConsent);
        Assert.Equal(ConsentState.Missing, ExternalConsent.StateOf(c));
    }

    [Fact]
    public void WhenAConnectionMovesFromLocalToRemote_ItNeedsConsentFromThen()
    {
        var c = OpenAi("http://localhost:1234/v1");
        Assert.True(ExternalConsent.MayUse(c));

        c.BaseUrl = "https://api.openai.com/v1";

        Assert.False(ExternalConsent.MayUse(c));
    }

    [Fact]
    public void GrantingToALocalConnection_IsRejected()
    {
        var c = OpenAi("http://localhost:1234/v1");

        Assert.Throws<InvalidOperationException>(() => ExternalConsent.Grant(c, Now));
    }
}
