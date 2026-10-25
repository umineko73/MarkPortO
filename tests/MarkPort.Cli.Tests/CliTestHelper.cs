// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using MarkPort.Cli;

namespace MarkPort.Cli.Tests;

public sealed record CliRun(int ExitCode, string Output, string Error);

public static class Cli
{
    public static async Task<CliRun> RunAsync(CancellationToken cancellationToken = default, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApp.RunAsync(WithoutTheUsersSettings(args), output, error, cancellationToken);
        return new CliRun(code, output.ToString(), error.ToString());
    }

    /// <summary>
    /// A command that is not given a settings file would read the settings of whoever runs the tests
    /// (the settings.json in their AppData folder), so the result would depend on their screen settings.
    /// It gets a minimal file instead: the LibreOffice engine, everything else at its default.
    /// </summary>
    private static string[] WithoutTheUsersSettings(string[] args) =>
        args.Length > 0 && args[0] is "convert" or "check-connection" or "list-connections" && !args.Contains("--settings")
            ? [.. args, "--settings", MinimalSettings.Value]
            : args;

    private static readonly Lazy<string> MinimalSettings = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "markport-tests-minimal-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, "{ \"engine\": { \"kind\": \"libreoffice\" }, \"ui\": { \"language\": \"ja\" } }");
        return path;
    });

    public static Task<CliRun> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);
}
