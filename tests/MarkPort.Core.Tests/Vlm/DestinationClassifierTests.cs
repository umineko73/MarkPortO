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

using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Vlm;

/// <summary>FR-046: whether a destination is inside the local network (no consent needed) or outside it.</summary>
public class DestinationClassifierTests
{
    [Theory]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://LOCALHOST/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://127.1.2.3/v1")]
    [InlineData("http://[::1]:1234/v1")]
    [InlineData("http://10.0.0.5/v1")]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://172.31.255.254/v1")]
    [InlineData("http://192.168.11.220:1234/v1")]
    [InlineData("http://169.254.10.10/v1")]
    [InlineData("http://[fe80::1]/v1")]
    [InlineData("http://[fd12:3456::1]/v1")]
    [InlineData("http://lmstudio:1234/v1")]
    [InlineData("http://gpu-box.local:1234/v1")]
    [InlineData("http://gpu-box.lan/v1")]
    [InlineData("http://llm.corp.internal/v1")]
    [InlineData("https://GPU-BOX.LOCAL./v1")]
    public void LocalDestinations_NeedNoConsent(string url)
    {
        Assert.True(DestinationClassifier.IsLocal(url), url);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.anthropic.com")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://172.15.0.1/v1")]
    [InlineData("http://172.32.0.1/v1")]
    [InlineData("http://192.169.0.1/v1")]
    [InlineData("http://11.0.0.1/v1")]
    [InlineData("http://[2001:db8::1]/v1")]
    [InlineData("https://llm.corp.example.com/v1")]
    [InlineData("https://notlocal.example/v1")]
    [InlineData("https://evil-localhost.com/v1")]
    [InlineData("https://localhost.example.com/v1")]
    public void RemoteDestinations_NeedConsent(string url)
    {
        Assert.False(DestinationClassifier.IsLocal(url), url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://localhost/")]
    public void AnUnreadableDestination_IsTreatedAsRemote(string url)
    {
        Assert.False(DestinationClassifier.IsLocal(url));
    }
}
