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

using System.Diagnostics;
using System.Text;
using MarkPort.Core.Localization;

namespace MarkPort.Core.Vlm.Command;

/// <summary>What to run (FR-044): the command, its arguments, the standard input and the environment.</summary>
/// <param name="FileName">A name looked up in the PATH, or a path.</param>
/// <param name="Environment">Variables to set; a value of <c>null</c> removes the variable. The others are inherited (the login is used).</param>
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan Timeout);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>The command cannot be started: it is not found, or an argument is not safe to pass.</summary>
public sealed class CommandStartException(string message) : Exception(message);

/// <summary>The command did not finish in time. It and its child processes have been ended.</summary>
public sealed class CommandTimeoutException(string message) : Exception(message);

/// <summary>
/// Runs a command once without a shell, with the prompt on the standard input, and ends it and its child processes
/// when it takes too long or the run is cancelled (FR-044). Output is read as UTF-8.
/// </summary>
public static class CommandRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <exception cref="CommandStartException">The command is not found or cannot be started.</exception>
    /// <exception cref="CommandTimeoutException">The command did not finish within the time.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var path = ExecutableResolver.Resolve(request.FileName)
            ?? throw new CommandStartException(Loc.T("The command '{0}' was not found.", request.FileName));

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = request.WorkingDirectory,
        };

        if (ExecutableResolver.IsBatch(path))
        {
            // A .cmd file is run by the command interpreter, which reads special characters: only plain arguments are passed.
            foreach (var argument in request.Arguments.Prepend(path))
            {
                if (argument.Any(c => c is '"' or '%' or '^' or '&' or '|' or '<' or '>' or '\r' or '\n' || char.IsControl(c)))
                {
                    throw new CommandStartException(Loc.T("An argument of the command contains a character that is not safe to pass: {0}", argument));
                }
            }

            start.FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = "/d /s /c \"" + string.Join(' ', request.Arguments.Prepend(path).Select(a => "\"" + a + "\"")) + "\"";
        }
        else
        {
            start.FileName = path;
            foreach (var argument in request.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new CommandStartException(Loc.T("The command '{0}' could not be started: {1}", request.FileName, ex.Message));
        }

        ProcessJob.Assign(process);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var standardOutput = process.StandardOutput.ReadToEndAsync(linked.Token);
        var standardError = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await WriteInputAsync(process, request.StandardInput, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            return new CommandResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            EndTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new CommandTimeoutException(Loc.T("The command did not finish within {0} seconds.", (int)request.Timeout.TotalSeconds));
        }
        finally
        {
            EndTree(process);
        }
    }

    private static async Task WriteInputAsync(Process process, string? text, CancellationToken cancellationToken)
    {
        try
        {
            if (text is not null)
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command ended before it read everything: its answer or its error tells what happened.
        }
    }

    private static void EndTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended in the meantime.
        }
    }
}
