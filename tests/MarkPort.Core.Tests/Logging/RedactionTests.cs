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

using Microsoft.Extensions.Logging;
using MarkPort.Core.Logging;

namespace MarkPort.Core.Tests.Logging;

public class RedactionTests
{
    [Fact]
    public void Redact_ReplacesRegisteredSecret()
    {
        var registry = new SecretRegistry();
        registry.Register("sk-secret-123456");

        var result = registry.Redact("Authorization: Bearer sk-secret-123456 failed");

        Assert.DoesNotContain("sk-secret-123456", result);
        Assert.Contains(SecretRegistry.Mask, result);
    }

    [Fact]
    public void Redact_ReplacesLongerSecretAsAWhole()
    {
        var registry = new SecretRegistry();
        registry.Register("abcd");
        registry.Register("abcd-efgh");

        Assert.Equal("***", registry.Redact("abcd-efgh"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    public void Register_IgnoresEmptyOrTooShortValues(string? secret)
    {
        var registry = new SecretRegistry();
        registry.Register(secret);

        Assert.Equal("a b ab", registry.Redact("a b ab"));
    }

    [Fact]
    public void RedactingLogger_RemovesSecretFromMessageAndException()
    {
        var registry = new SecretRegistry();
        registry.Register("sk-secret-123456");
        var sink = new ListLogger();
        var logger = new RedactingLogger(sink, registry);

        logger.LogError(new InvalidOperationException("bad key sk-secret-123456"), "call failed with sk-secret-123456");

        var entry = Assert.Single(sink.Messages);
        Assert.DoesNotContain("sk-secret-123456", entry);
        Assert.Contains("call failed", entry);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
    }
}
