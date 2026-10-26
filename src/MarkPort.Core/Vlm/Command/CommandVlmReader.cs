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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Localization;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm.Command;

/// <summary>
/// Reads a page image with an external command (<c>claude</c>, <c>codex</c> or <c>opencode</c>): one run of the command
/// per image, in a folder of its own, with nothing kept between the runs (FR-044). A failure is a
/// <see cref="VlmException"/> with the kind of the failure (FR-049). The consent (FR-046) is checked by the caller,
/// before a reader is made.
/// </summary>
public sealed class CommandVlmReader : IVlmReader
{
    /// <summary>How long to wait before asking again after a rate limit; one wait for each new try.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRateLimitWaits = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly ConnectionProfile _connection;
    private readonly ICommandAdapter _adapter;
    private readonly IReadOnlyList<TimeSpan> _rateLimitWaits;
    private readonly string? _workParent;
    private readonly IReadOnlyDictionary<string, string?> _extraEnvironment;

    /// <param name="extraEnvironment">Variables added to those of the adapter; a test uses them to steer a stand-in command.</param>
    public CommandVlmReader(ConnectionProfile connection, IReadOnlyList<TimeSpan>? rateLimitWaits = null, string? workParent = null, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        _extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>();
        _connection = connection;
        _adapter = CommandAdapters.For(connection.Command ?? throw new ArgumentException("The connection has no command.", nameof(connection)));
        _rateLimitWaits = rateLimitWaits ?? DefaultRateLimitWaits;
        _workParent = workParent;
    }

    public string Executable => string.IsNullOrWhiteSpace(_connection.Executable) ? _adapter.DefaultExecutable : _connection.Executable.Trim();

    public async Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadOnceAsync(prompt, png, cancellationToken).ConfigureAwait(false);
            }
            catch (VlmException ex) when (ex.Kind == VlmFailureKind.RateLimit && attempt < _rateLimitWaits.Count)
            {
                await Task.Delay(_rateLimitWaits[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<VlmResponse> ReadOnceAsync(string prompt, byte[] png, CancellationToken cancellationToken)
    {
        using var folder = CommandWorkFolder.Create(png, _workParent);
        var job = new CommandJob(prompt, folder.ImagePath, folder.Path, _connection.Model?.Trim() ?? string.Empty, _connection.Effort);
        var invocation = _adapter.Build(job);
        var request = new CommandRequest(
            Executable,
            invocation.Arguments,
            folder.Path,
            invocation.StandardInput,
            invocation.Environment.Concat(_extraEnvironment).ToDictionary(p => p.Key, p => p.Value),
            TimeSpan.FromSeconds(_connection.TimeoutSeconds));

        CommandResult result;
        try
        {
            result = await CommandRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (CommandStartException ex)
        {
            throw new VlmException(ex.Message, kind: VlmFailureKind.NotFound);
        }
        catch (CommandTimeoutException ex)
        {
            throw new VlmException(ex.Message, timedOut: true);
        }

        var parsed = _adapter.Parse(result, invocation);
        if (parsed.Error is not null)
        {
            var kind = FailureClassifier.Classify(parsed.Error + "\n" + result.StandardError);
            throw new VlmException(FailureClassifier.Summarize(parsed.Error), kind: kind);
        }

        if (parsed.Answer is null || string.IsNullOrWhiteSpace(parsed.Answer.Text))
        {
            throw new VlmException(Loc.T("The output of the command could not be read."), kind: VlmFailureKind.BadOutput);
        }

        return new VlmResponse(parsed.Answer.Text, parsed.Answer.Usage, CostUsd: parsed.Answer.CostUsd);
    }
}
