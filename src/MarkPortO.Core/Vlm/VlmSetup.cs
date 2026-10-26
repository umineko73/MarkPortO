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

using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// What a run needs when step 3 (the VLM reading) is on: the reader, the prompts, and the check that runs before step 1.
/// </summary>
public sealed class VlmSetup : IDisposable
{
    private readonly IDisposable? _owned;

    public VlmSetup(IVlmReader reader, PromptSettings prompts, string connectionName, Func<CancellationToken, Task<CheckResult>> preflight, IDisposable? owned = null, int maxLinesPerRead = 0)
    {
        MaxLinesPerRead = maxLinesPerRead;
        Reader = reader;
        Prompts = prompts;
        ConnectionName = connectionName;
        Preflight = preflight;
        _owned = owned;
    }

    public IVlmReader Reader { get; }

    public PromptSettings Prompts { get; }

    public string ConnectionName { get; }

    /// <summary>The most lines read in one request; 0 never splits a page (FR-041).</summary>
    public int MaxLinesPerRead { get; }

    /// <summary>The connection this setup was made from (a copy of the settings; no key).</summary>
    public Settings.ConnectionProfile? Connection { get; init; }

    /// <summary>How many pages are read at the same time (FR-044).</summary>
    public int Parallel { get; init; } = 1;

    /// <summary>Stop after this many pages have been read in a job; <c>null</c> is no limit (FR-057).</summary>
    public int? MaxPages { get; init; }

    /// <summary>Stop after this cost in US dollars; <c>null</c> is no limit (FR-057).</summary>
    public double? MaxCostUsd { get; init; }

    /// <summary>The version of the command, once the connection check has asked it (FR-055).</summary>
    public string? CommandVersion { get; set; }

    /// <summary>The connection check done before step 1 starts (FR-019c).</summary>
    public Func<CancellationToken, Task<CheckResult>> Preflight { get; }

    public void Dispose() => _owned?.Dispose();
}

/// <summary>Builds a <see cref="VlmSetup"/> from the settings. The screen and the command line both use it.</summary>
public static class VlmSetupFactory
{
    /// <summary>
    /// The failed check that stands for a missing consent to send page images outside the local network (FR-046): nothing is
    /// started and nothing is sent before it is given (SC-023). <c>null</c> when the connection may be used.
    /// </summary>
    public static CheckResult? ConsentMissing(ConnectionProfile connection) =>
        ExternalConsent.MayUse(connection)
            ? null
            : new CheckResult(false, Loc.T("Sending page images outside the local network needs your consent for the connection '{0}'. Agree in the settings, or run: markporto consent {0}", connection.Name));

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

        VlmSetup? created = null;

        if (connection.Kind == ConnectionKind.Command)
        {
            var reader = new Command.CommandVlmReader(connection);
            var commandChecker = new Command.CommandConnectionChecker(connection, reader);
            async Task<CheckResult> CheckCommand(CancellationToken cancellationToken)
            {
                if (ConsentMissing(connection) is { } missing)
                {
                    return missing;
                }

                // Before every run only the command is checked (no model call, no cost); the full check is the user's own request.
                var result = await commandChecker.CheckCommandAsync(cancellationToken).ConfigureAwait(false);
                if (created is not null)
                {
                    created.CommandVersion = commandChecker.Version;
                }

                return result;
            }

            created = new VlmSetup(reader, settings.Vlm.Prompts, connection.Name, CheckCommand, null, connection.MaxLinesPerRead)
            {
                Connection = connection,
                Parallel = connection.Parallel,
                MaxPages = connection.MaxPages,
                MaxCostUsd = connection.MaxCostUsd,
            };
            return (created, null);
        }

        var key = ApiKeyResolver.Resolve(connection, readEnvironment);
        if (key.Error is not null)
        {
            return (null, key.Error);
        }

        var client = new VlmClient(connection, key.Key, handler);
        var checker = new ConnectionChecker(client, connection);
        Task<CheckResult> CheckOpenAi(CancellationToken cancellationToken) =>
            ConsentMissing(connection) is { } missing ? Task.FromResult(missing) : checker.CheckConnectionAsync(cancellationToken);

        created = new VlmSetup(client, settings.Vlm.Prompts, connection.Name, CheckOpenAi, client, connection.MaxLinesPerRead)
        {
            Connection = connection,
            MaxPages = connection.MaxPages,
            MaxCostUsd = connection.MaxCostUsd,
        };
        return (created, null);
    }
}
