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

using MarkPort.Core.Localization;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm;

/// <summary>
/// What a run needs when step 3 (the VLM reading) is on: the reader, the prompts, and the check that runs before step 1.
/// </summary>
public sealed class VlmSetup : IDisposable
{
    private readonly IDisposable? _owned;

    public VlmSetup(IVlmReader reader, PromptSettings prompts, string connectionName, Func<CancellationToken, Task<CheckResult>> preflight, IDisposable? owned = null)
    {
        Reader = reader;
        Prompts = prompts;
        ConnectionName = connectionName;
        Preflight = preflight;
        _owned = owned;
    }

    public IVlmReader Reader { get; }

    public PromptSettings Prompts { get; }

    public string ConnectionName { get; }

    /// <summary>The connection check done before step 1 starts (FR-019c).</summary>
    public Func<CancellationToken, Task<CheckResult>> Preflight { get; }

    public void Dispose() => _owned?.Dispose();
}

/// <summary>Builds a <see cref="VlmSetup"/> from the settings. The screen and the command line both use it.</summary>
public static class VlmSetupFactory
{
    /// <summary>
    /// Finds the selected connection and its key. Returns the setup, or the reason it cannot be made
    /// (no such connection, the environment variable of the key is not set ...).
    /// </summary>
    public static (VlmSetup? Setup, string? Error) Create(AppSettings settings, Func<string, string?>? readEnvironment = null, HttpMessageHandler? handler = null)
    {
        var connection = settings.FindConnection(settings.Vlm.Selected);
        if (connection is null)
        {
            return (null, Loc.T("No VLM connection is selected. Choose one in the settings."));
        }

        var key = ApiKeyResolver.Resolve(connection, readEnvironment);
        if (key.Error is not null)
        {
            return (null, key.Error);
        }

        var client = new VlmClient(connection, key.Key, handler);
        var checker = new ConnectionChecker(client, connection);
        return (new VlmSetup(client, settings.Vlm.Prompts, connection.Name, checker.CheckConnectionAsync, client), null);
    }
}
