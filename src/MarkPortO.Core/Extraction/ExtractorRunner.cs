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

using System.Diagnostics;
using System.Text;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Extraction;

/// <summary>The command did not give a Markdown: it could not start, failed, took too long, or wrote nothing (FR-112).</summary>
public sealed class ExtractorException(string message) : Exception(message);

/// <param name="Markdown">The Markdown of the document.</param>
/// <param name="Seconds">How long the command ran.</param>
/// <param name="ExitCode">The exit code of the command.</param>
/// <param name="ErrorTail">The end of the standard error (the key and the environment values are hidden).</param>
/// <param name="OutputFolder">The folder of the command (<c>{outdir}</c>); the files it wrote besides the Markdown are in it.</param>
/// <param name="MarkdownFile">The file that the Markdown was read from; <c>null</c> when it came from the standard output.</param>
public sealed record ExtractorResult(string Markdown, double Seconds, int ExitCode, string ErrorTail, string OutputFolder, string? MarkdownFile);

/// <summary>
/// Runs an extractor on one file (FR-110, FR-112; experimental): no shell, each argument on its own, no window; the whole process tree is stopped at a time-out
/// or when the run is cancelled. The standard output and the standard error are read up to <see cref="MaximumOutput"/> characters.
/// </summary>
public static class ExtractorRunner
{
    public const int MaximumOutput = 1024 * 1024 * 64;

    private const int MaximumError = 1024 * 1024;

    private const int ErrorTailLength = 2000;

    /// <summary>The arguments after the placeholders <c>{input}</c>, <c>{outdir}</c> and <c>{output}</c> are replaced.</summary>
    public static IReadOnlyList<string> ExpandArguments(ExtractorProfile profile, string input, string outputFolder, string outputFile) =>
        [.. profile.Args.Select(a => a
            .Replace("{input}", input, StringComparison.Ordinal)
            .Replace("{outdir}", outputFolder, StringComparison.Ordinal)
            .Replace("{output}", outputFile, StringComparison.Ordinal))];

    /// <summary>What is wrong with the profile; <c>null</c> when it can be used (not whether the program exists).</summary>
    public static string? Validate(ExtractorProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return Loc.T("The extractor has no name.");
        }

        if (string.IsNullOrWhiteSpace(profile.Command))
        {
            return Loc.T("The extractor '{0}' has no command.", profile.Name);
        }

        if (!profile.Args.Any(a => a.Contains("{input}", StringComparison.Ordinal)))
        {
            return Loc.T("The arguments of the extractor '{0}' have no {{input}}.", profile.Name);
        }

        if (profile.Output == ExtractorOutput.File && !profile.Args.Any(a => a.Contains("{output}", StringComparison.Ordinal) || a.Contains("{outdir}", StringComparison.Ordinal)))
        {
            return Loc.T("The arguments of the extractor '{0}' have no {{output}} or {{outdir}}, but the command is to write a file.", profile.Name);
        }

        if (profile.TimeoutSeconds < 1)
        {
            return Loc.T("The time-out of the extractor '{0}' must be 1 second or more.", profile.Name);
        }

        return null;
    }

    /// <summary>The full path of the program, or <c>null</c> when it is not found (a path, or a name in the PATH).</summary>
    public static string? FindProgram(string command)
    {
        var name = command.Trim().Trim('"');
        if (name.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in new[] { name }.Concat(extensions.Select(e => name + e)))
            {
                try
                {
                    var path = Path.Combine(folder.Trim('"'), candidate);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return null;
    }

    /// <summary>Runs the command on <paramref name="input"/>; the folder <paramref name="workFolder"/> is made if it is not there.</summary>
    /// <exception cref="ExtractorException">The command could not give a Markdown.</exception>
    public static async Task<ExtractorResult> RunAsync(ExtractorProfile profile, string input, string workFolder, CancellationToken cancellationToken)
    {
        if (Validate(profile) is { } problem)
        {
            throw new ExtractorException(problem);
        }

        var program = FindProgram(profile.Command)
            ?? throw new ExtractorException($"The command '{profile.Command}' of the extractor '{profile.Name}' was not found.");
        var outputFolder = Path.Combine(workFolder, "out");
        Directory.CreateDirectory(outputFolder);
        var outputFile = Path.Combine(outputFolder, "out.md");

        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? workFolder : profile.WorkingDirectory,
        };
        foreach (var argument in ExpandArguments(profile, input, outputFolder, outputFile))
        {
            info.ArgumentList.Add(argument);
        }

        var secrets = new List<string>();
        foreach (var (key, value) in profile.Env)
        {
            var plain = Resolve(value);
            info.Environment[key] = plain;
            secrets.Add(plain);
        }

        var started = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ExtractorException($"The command '{profile.Command}' could not be started: {ex.Message}");
        }

        process.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var readOutput = ReadAsync(process.StandardOutput, output, MaximumOutput);
        var readError = ReadAsync(process.StandardError, error, MaximumError);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ExtractorException($"The extractor '{profile.Name}' did not finish in {profile.TimeoutSeconds} seconds.");
        }

        var tail = Tail(error.ToString(), secrets);
        if (process.ExitCode != 0)
        {
            throw new ExtractorException($"The extractor '{profile.Name}' failed (exit code {process.ExitCode}). {tail}".Trim());
        }

        string markdown;
        string? file = null;
        if (profile.Output == ExtractorOutput.Stdout)
        {
            markdown = output.ToString();
        }
        else if (FindMarkdownFile(outputFile, outputFolder, input) is { } found)
        {
            markdown = await File.ReadAllTextAsync(found, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            file = found;
        }
        else
        {
            throw new ExtractorException($"The extractor '{profile.Name}' did not write the file. {tail}".Trim());
        }

        markdown = markdown.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new ExtractorException($"The extractor '{profile.Name}' gave no text. {tail}".Trim());
        }

        return new ExtractorResult(markdown, Math.Round(started.Elapsed.TotalSeconds, 1), process.ExitCode, tail, outputFolder, file);
    }

    /// <summary>
    /// The Markdown that the command wrote: <c>{output}</c>; or, when the command chose the name itself (it was given <c>{outdir}</c> only), the
    /// <c>.md</c> file of the folder: the one named like the input, or the only one, or the largest.
    /// </summary>
    private static string? FindMarkdownFile(string outputFile, string outputFolder, string input)
    {
        if (File.Exists(outputFile))
        {
            return outputFile;
        }

        var files = Directory.EnumerateFiles(outputFolder, "*.md", SearchOption.TopDirectoryOnly).ToList();
        var stem = Path.GetFileNameWithoutExtension(input);
        return files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
            ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    private static string Resolve(string value)
    {
        if (!DpapiProtector.IsProtected(value))
        {
            return value;
        }

        try
        {
            return DpapiProtector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new ExtractorException("An environment value of the extractor cannot be read on this PC or by this user; enter it again.");
        }
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder into, int maximum)
    {
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (into.Length < maximum)
            {
                into.Append(buffer, 0, Math.Min(count, maximum - into.Length));
            }
        }
    }

    /// <summary>The end of the standard error, with the values of the environment variables (they may be keys) hidden.</summary>
    private static string Tail(string error, IReadOnlyList<string> secrets)
    {
        var text = error.Trim();
        foreach (var secret in secrets.Where(s => s.Length >= 4))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text.Length <= ErrorTailLength ? text : text[^ErrorTailLength..];
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
