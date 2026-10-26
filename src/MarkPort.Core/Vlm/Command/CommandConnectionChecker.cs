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

namespace MarkPort.Core.Vlm.Command;

/// <summary>
/// The two checks of a connection to an external command (FR-045): the command can be found and started (this does not
/// call the model and costs nothing), and the command can read a picture (this calls the model and may cost money, so
/// the user asks for it). The consent (FR-046) is checked before, by the caller.
/// </summary>
public sealed class CommandConnectionChecker(ConnectionProfile connection, IVlmReader reader, Func<string>? codeFactory = null)
{
    private const string ImagePrompt = "画像に書かれている文字だけを、そのまま答えてください。";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string> _codeFactory = codeFactory ?? TestImageFactory.RandomCode;

    /// <summary>The version the command printed, once <see cref="CheckConnectionAsync"/> has found it (FR-055).</summary>
    public string? Version { get; private set; }

    public string Executable =>
        string.IsNullOrWhiteSpace(connection.Executable)
            ? CommandAdapters.For(connection.Command ?? CommandKind.Claude).DefaultExecutable
            : connection.Executable.Trim();

    public async Task<CheckResult> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var path = ExecutableResolver.Resolve(Executable);
        if (path is null)
        {
            return new CheckResult(false, Loc.T("The command '{0}' was not found. Install it, or set its path in the connection.", Executable));
        }

        try
        {
            var result = await CommandRunner.RunAsync(
                new CommandRequest(path, ["--version"], Path.GetTempPath(), null, new Dictionary<string, string?>(), VersionTimeout),
                cancellationToken).ConfigureAwait(false);
            var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (result.ExitCode != 0 || line is null)
            {
                return new CheckResult(false, Loc.T("The command '{0}' did not answer to --version.", Executable));
            }

            Version = FailureClassifier.Summarize(line);
            return new CheckResult(true, Loc.T("The command is available ({0}).", Version));
        }
        catch (CommandStartException ex)
        {
            return new CheckResult(false, ex.Message);
        }
        catch (CommandTimeoutException)
        {
            return new CheckResult(false, Loc.T("The command '{0}' did not answer to --version in time.", Executable));
        }
    }

    public async Task<CheckResult> CheckImageAsync(CancellationToken cancellationToken)
    {
        var code = _codeFactory();
        try
        {
            var answer = await reader.ReadAsync(ImagePrompt, TestImageFactory.Create(code), cancellationToken).ConfigureAwait(false);
            var read = new string(answer.Text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return read.Contains(code, StringComparison.Ordinal)
                ? new CheckResult(true, Loc.T("The image was read. The command '{0}' can analyze images.", Executable))
                : new CheckResult(false, Loc.T("The content of the image could not be read (answer: '{0}'). The model may not be able to analyze images.", FailureClassifier.Summarize(answer.Text)));
        }
        catch (VlmException ex)
        {
            return new CheckResult(false, Describe(ex));
        }
    }

    private static string Describe(VlmException ex) => ex.Kind switch
    {
        VlmFailureKind.NotLoggedIn => Loc.T("The command is not logged in. {0}", ex.Message),
        VlmFailureKind.UsageLimit => Loc.T("The usage limit of the command is reached. {0}", ex.Message),
        VlmFailureKind.RateLimit => Loc.T("The command is limited by the rate of requests. {0}", ex.Message),
        VlmFailureKind.Timeout => Loc.T("Timed out. {0}", ex.Message),
        _ => Loc.T("The command could not read the image. {0}", ex.Message),
    };
}
