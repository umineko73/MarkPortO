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

using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Tests.Localization;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

public class ConnectionCheckerTests : JapaneseUiTests
{
    private static ConnectionProfile Profile(FakeOpenAiServer server, string model = "vision-model") => new()
    {
        Name = "fake",
        BaseUrl = server.BaseUrl,
        Model = model,
        TimeoutSeconds = 10,
    };

    private static ConnectionChecker Checker(FakeOpenAiServer server, string? key = null, string model = "vision-model", string code = "AB23")
    {
        var profile = Profile(server, model);
        return new ConnectionChecker(new VlmClient(profile, key), profile, () => code);
    }

    // ---- connection check ----

    [Fact]
    public async Task Connection_Succeeds_WhenTheServerHasTheModel()
    {
        using var server = new FakeOpenAiServer();

        var result = await Checker(server).CheckConnectionAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("vision-model", result.Message);
    }

    [Fact]
    public async Task Connection_Fails_WhenTheModelIsMissing_AndNamesSomeThatExist()
    {
        using var server = new FakeOpenAiServer { Models = ["alpha", "beta"] };

        var result = await Checker(server, model: "no-such-model").CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no-such-model", result.Message);
        Assert.Contains("alpha", result.Message);
    }

    [Fact]
    public async Task Connection_Fails_WhenTheKeyIsRefused()
    {
        using var server = new FakeOpenAiServer { RequiredKey = "right-key-123456" };

        var result = await Checker(server, key: "wrong-key-123456").CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("認証", result.Message);
    }

    [Fact]
    public async Task Connection_Fails_WhenTheServerCannotBeReached()
    {
        var profile = new ConnectionProfile { Name = "x", BaseUrl = "http://localhost:1/v1", Model = "m", TimeoutSeconds = 5 };
        var checker = new ConnectionChecker(new VlmClient(profile, null), profile);

        var result = await checker.CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("到達できません", result.Message);
    }

    [Fact]
    public async Task Connection_Fails_WithATimeout_ForASlowServer()
    {
        using var server = new FakeOpenAiServer { Delay = TimeSpan.FromSeconds(4) };
        var profile = Profile(server);
        profile.TimeoutSeconds = 1;
        var checker = new ConnectionChecker(new VlmClient(profile, null), profile);

        var result = await checker.CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("時間切れ", result.Message);
    }

    [Fact]
    public async Task Connection_FallsBackToATextRequest_WhenThereIsNoModelList()
    {
        using var server = new FakeOpenAiServer { SupportsModelList = false };

        var result = await Checker(server).CheckConnectionAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("確認できません", result.Message);
        Assert.Contains(server.Requests, r => r.Method == "POST" && !r.HasImage);
    }

    [Fact]
    public async Task Connection_WithoutAModelList_StillFailsWhenTheKeyIsRefused()
    {
        using var server = new FakeOpenAiServer { SupportsModelList = false, RequiredKey = "right-key-123456" };

        var result = await Checker(server, key: "wrong-key-123456").CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Connection_DoesNotSendAnImage()
    {
        using var server = new FakeOpenAiServer();

        await Checker(server).CheckConnectionAsync(CancellationToken.None);

        Assert.DoesNotContain(server.Requests, r => r.HasImage);
    }

    // ---- image check ----

    [Fact]
    public async Task Image_Succeeds_ForAModelThatReadsThePicture()
    {
        using var server = new FakeOpenAiServer { ImageCode = "AB23" };

        var result = await Checker(server, code: "AB23").CheckImageAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Contains(server.Requests, r => r.HasImage);
    }

    [Fact]
    public async Task Image_Succeeds_ForAModelThatThinksBeforeItAnswers()
    {
        // The real model (Gemma 4 in LM Studio) spends about 150 tokens thinking: a limit of 32 left the answer empty.
        using var server = new FakeOpenAiServer { ImageCode = "AB23", ThinkingTokens = 150 };

        var result = await Checker(server, code: "AB23").CheckImageAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task Image_Fails_WithTheRealReason_WhenThinkingUsesTheWholeLimit()
    {
        using var server = new FakeOpenAiServer { ImageCode = "AB23", ThinkingTokens = 100_000 };

        var result = await Checker(server, code: "AB23").CheckImageAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("トークンの上限", result.Message);
        Assert.DoesNotContain("画像を解析できないモデル", result.Message);
    }

    [Fact]
    public async Task Image_Fails_ForAModelThatCannotSeeImages_SC013()
    {
        using var server = new FakeOpenAiServer { SupportsImages = false };

        var result = await Checker(server).CheckImageAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("対応していません", result.Message);
    }

    [Fact]
    public async Task Image_Fails_ForAModelThatAnswersSomethingElse()
    {
        using var server = new FakeOpenAiServer { ImageCode = "ZZZZ" }; // reads a different code than the one drawn

        var result = await Checker(server, code: "AB23").CheckImageAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("読み取れませんでした", result.Message);
    }

    [Fact]
    public async Task Image_IgnoresSpacesAndCase_InTheAnswer()
    {
        using var server = new FakeOpenAiServer { ImageCode = "a b 2 3" };

        Assert.True((await Checker(server, code: "AB23").CheckImageAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Image_UsesAFreshCodeEveryTime()
    {
        using var server = new FakeOpenAiServer();
        var profile = Profile(server);
        var checker = new ConnectionChecker(new VlmClient(profile, null), profile); // the real random codes

        // The fake always answers AB23; a fresh random code is almost never that, so the check fails: it is not guessable.
        var results = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            results.Add((await checker.CheckImageAsync(CancellationToken.None)).Ok);
        }

        Assert.Contains(false, results);
    }

    [Fact]
    public void TestImage_IsAPng_AndTheCodeAlphabetAvoidsConfusingCharacters()
    {
        var png = TestImageFactory.Create("AB23");
        var code = TestImageFactory.RandomCode();

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Equal(TestImageFactory.CodeLength, code.Length);
        Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I');
    }
}
