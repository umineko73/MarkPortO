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

using Microsoft.Extensions.Logging;

namespace MarkPortO.Core.Logging;

/// <summary>Wraps a logger and removes registered secrets from messages and exception text.</summary>
public sealed class RedactingLogger(ILogger inner, SecretRegistry registry) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel))
        {
            return;
        }

        var message = registry.Redact(formatter(state, null));
        if (exception is not null)
        {
            message += Environment.NewLine + registry.Redact(exception.ToString());
        }

        // The exception object is not passed on: its message could contain a secret.
        inner.Log(logLevel, eventId, message, null, static (m, _) => m);
    }
}
