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

using System.Text.Json;
using MarkPort.Core.Logging;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Vlm;

public class VlmClientTests
{
    private static ConnectionProfile Profile(FakeOpenAiServer server, int timeoutSeconds = 30) => new()
    {
        Name = "fake",
        BaseUrl = server.BaseUrl,
        Model = "vision-model",
        TimeoutSeconds = timeoutSeconds,
    };

    private static readonly byte[] Png = TestImageFactory.Create("AB23");

    [Fact]
    public async Task AReadRequest_HasTheDocumentedShape()
    {
        using var server = new FakeOpenAiServer();
        using var client = new VlmClient(Profile(server), "key-for-shape-1234");

        var answer = await client.ReadAsync("Transcribe this page.", Png, CancellationToken.None);

        var request = Assert.Single(server.Requests, r => r.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal("Bearer key-for-shape-1234", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("vision-model", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("max_tokens", out _)); // page reading leaves the length to the server
        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("Transcribe this page.", content[0].GetProperty("text").GetString());
        var url = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(Png, System.Convert.FromBase64String(url["data:image/png;base64,".Length..]));
        Assert.StartsWith("読み取り結果", answer.Text);
    }

    [Fact]
    public async Task Usage_IsRecorded_WhenTheServerReportsIt()
    {
        using var server = new FakeOpenAiServer { PromptTokens = 820, CompletionTokens = 240 };
        using var client = new VlmClient(Profile(server), null);

        var answer = await client.ReadAsync("p", Png, CancellationToken.None);

        Assert.Equal((820, 240, 1060), (answer.Usage.PromptTokens, answer.Usage.CompletionTokens, answer.Usage.TotalTokens));
    }

    [Fact]
    public async Task Usage_IsUnavailable_WhenTheServerDoesNotReportIt_AndTheCallStillSucceeds()
    {
        using var server = new FakeOpenAiServer { ReportsUsage = false };
        using var client = new VlmClient(Profile(server), null);

        var answer = await client.ReadAsync("p", Png, CancellationToken.None);

        Assert.False(answer.Usage.IsAvailable);
        Assert.NotEmpty(answer.Text);
    }

    [Fact]
    public async Task NoKey_SendsNoAuthorizationHeader()
    {
        using var server = new FakeOpenAiServer();
        using var client = new VlmClient(Profile(server), null);

        await client.ReadAsync("p", Png, CancellationToken.None);

        Assert.All(server.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task ABusyServer_IsTriedOnceMore_ThenSucceeds()
    {
        using var server = new FakeOpenAiServer { BusyResponses = 1 };
        using var client = new VlmClient(Profile(server), null) { RetryDelay = TimeSpan.FromMilliseconds(20) };

        var answer = await client.ReadAsync("p", Png, CancellationToken.None);

        Assert.NotEmpty(answer.Text);
        Assert.Equal(2, server.Requests.Count(r => r.Path.EndsWith("/chat/completions", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AServerThatStaysBusy_GivesUpAfterOneRetry()
    {
        using var server = new FakeOpenAiServer { BusyResponses = 5 };
        using var client = new VlmClient(Profile(server), null) { RetryDelay = TimeSpan.FromMilliseconds(20) };

        var error = await Assert.ThrowsAsync<VlmException>(() => client.ReadAsync("p", Png, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, error.Status);
        Assert.Equal(2, server.Requests.Count(r => r.Path.EndsWith("/chat/completions", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASlowServer_FailsWithATimeout_AfterTheConfiguredSeconds()
    {
        using var server = new FakeOpenAiServer { Delay = TimeSpan.FromSeconds(5) };
        using var client = new VlmClient(Profile(server, timeoutSeconds: 1), null);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<VlmException>(() => client.ReadAsync("p", Png, CancellationToken.None));

        Assert.True(error.TimedOut);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 4);
    }

    [Fact]
    public async Task AnUnreachableServer_IsReportedAsUnreachable()
    {
        var profile = new ConnectionProfile { Name = "x", BaseUrl = "http://localhost:1/v1", Model = "m", TimeoutSeconds = 5 };
        using var client = new VlmClient(profile, null);

        var error = await Assert.ThrowsAsync<VlmException>(() => client.ReadAsync("p", Png, CancellationToken.None));

        Assert.True(error.Unreachable);
    }

    [Fact]
    public async Task ARefusedKey_IsReportedAsAnAuthenticationFailure()
    {
        using var server = new FakeOpenAiServer { RequiredKey = "the-right-key-0001" };
        using var client = new VlmClient(Profile(server), "a-wrong-key-99999");

        var error = await Assert.ThrowsAsync<VlmException>(() => client.ReadAsync("p", Png, CancellationToken.None));

        Assert.True(error.IsAuthenticationFailure);
    }

    [Fact]
    public async Task TheKey_NeverAppearsInAnErrorMessage()
    {
        SecretRegistry.Shared.Register("leaky-key-value-7777");
        using var server = new FakeOpenAiServer { RequiredKey = "something-else-12345" };
        using var client = new VlmClient(Profile(server), "leaky-key-value-7777");

        var error = await Assert.ThrowsAsync<VlmException>(() => client.ReadAsync("p", Png, CancellationToken.None));

        Assert.DoesNotContain("leaky-key-value-7777", error.Message);
        Assert.DoesNotContain("leaky-key-value-7777", error.ToString());
    }

    [Fact]
    public async Task ATrailingSlashInTheUrl_IsHarmless()
    {
        using var server = new FakeOpenAiServer();
        var profile = Profile(server);
        profile.BaseUrl += "/";
        using var client = new VlmClient(profile, null);

        Assert.NotEmpty((await client.ReadAsync("p", Png, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task ListModels_ReturnsTheIds()
    {
        using var server = new FakeOpenAiServer { Models = ["a", "b"] };
        using var client = new VlmClient(Profile(server), null);

        Assert.Equal(["a", "b"], await client.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_StopsTheCall_WithoutBeingReportedAsATimeout()
    {
        using var server = new FakeOpenAiServer { Delay = TimeSpan.FromSeconds(5) };
        using var client = new VlmClient(Profile(server), null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync("p", Png, cts.Token));
    }
}
