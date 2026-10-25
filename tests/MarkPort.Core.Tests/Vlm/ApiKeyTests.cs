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

using MarkPort.Core.Tests.Localization;
using MarkPort.Core.Logging;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Vlm;

public class ApiKeyTests : JapaneseUiTests
{
    private static ConnectionProfile Profile(string? env = null, string? key = null) => new()
    {
        Name = "local",
        BaseUrl = "http://localhost:1234/v1",
        Model = "m",
        ApiKeyEnv = env,
        ApiKey = key,
    };

    private static Func<string, string?> Env(params (string Name, string? Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void TheEnvironmentVariable_WinsOverTheStoredKey()
    {
        var resolved = ApiKeyResolver.Resolve(Profile(env: "MY_KEY", key: "stored-key-1234"), Env(("MY_KEY", "from-env-key-5678")));

        Assert.Equal("from-env-key-5678", resolved.Key);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void TheEnvironmentVariable_IsReadAgainAtEveryCall()
    {
        var current = "first-value-1111";
        var profile = Profile(env: "MY_KEY");

        var first = ApiKeyResolver.Resolve(profile, _ => current);
        current = "second-value-2222";
        var second = ApiKeyResolver.Resolve(profile, _ => current);

        Assert.Equal("first-value-1111", first.Key);
        Assert.Equal("second-value-2222", second.Key);
    }

    [Fact]
    public void AMissingEnvironmentVariable_IsExplained_AndDoesNotFallBackToTheStoredKey()
    {
        var resolved = ApiKeyResolver.Resolve(Profile(env: "NOT_SET", key: "stored-key-1234"), Env());

        Assert.Null(resolved.Key);
        Assert.Contains("NOT_SET", resolved.Error);
        Assert.Contains("設定されていません", resolved.Error);
    }

    [Fact]
    public void AKeyEnteredOnTheScreen_IsStoredEncrypted_AndReadBack()
    {
        var protectedValue = DpapiProtector.Protect("my-secret-key-9999");

        Assert.StartsWith("dpapi:", protectedValue);
        Assert.DoesNotContain("my-secret-key-9999", protectedValue);
        Assert.True(DpapiProtector.IsProtected(protectedValue));
        Assert.Equal("my-secret-key-9999", ApiKeyResolver.Resolve(Profile(key: protectedValue)).Key);
    }

    [Fact]
    public void AHandWrittenPlainKey_IsAccepted()
    {
        Assert.Equal("plain-key-0000", ApiKeyResolver.Resolve(Profile(key: "plain-key-0000")).Key);
    }

    [Fact]
    public void ADamagedOrForeignEncryptedKey_IsExplained()
    {
        var resolved = ApiKeyResolver.Resolve(Profile(key: "dpapi:AAAAAAAA"));

        Assert.Null(resolved.Key);
        Assert.Contains("読み取れません", resolved.Error);
    }

    [Fact]
    public void NoKeyAtAll_IsFine_ForAServerThatNeedsNone()
    {
        var resolved = ApiKeyResolver.Resolve(Profile());

        Assert.Null(resolved.Key);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void AKeyThatWasResolved_IsRemovedFromText()
    {
        ApiKeyResolver.Resolve(Profile(env: "K"), Env(("K", "registered-secret-4321")));

        Assert.DoesNotContain("registered-secret-4321", SecretRegistry.Shared.Redact("header: Bearer registered-secret-4321"));
    }

    [Fact]
    public void Describe_NeverShowsTheKey()
    {
        Assert.Equal("環境変数 MY_KEY", ApiKeyResolver.Describe(Profile(env: "MY_KEY", key: "x-secret-value-1")));
        Assert.Equal("保存済み", ApiKeyResolver.Describe(Profile(key: "x-secret-value-1")));
        Assert.Equal("なし", ApiKeyResolver.Describe(Profile()));
    }

    [Fact]
    public void WhatIsSavedInSettings_ForAnEnvironmentKey_IsOnlyTheName()
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(Profile(env: "MY_KEY"));

        var json = MarkPort.Core.Jobs.Models.JsonDefaults.Serialize(settings);

        Assert.Contains("\"apiKeyEnv\": \"MY_KEY\"", json);
        Assert.DoesNotContain("\"apiKey\":", json);
    }
}
