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
using MarkPort.Core.Settings;

namespace MarkPort.Cli;

/// <summary>The values given on the command line to <c>convert</c>. <c>null</c> means "not given": the settings decide.</summary>
public sealed record ConvertArgs
{
    public IReadOnlyList<string> Inputs { get; init; } = [];

    public string? SettingsPath { get; init; }

    public string? Out { get; init; }

    public string? Types { get; init; }

    public bool? Recurse { get; init; }

    public bool? IncludeHidden { get; init; }

    public string? Engine { get; init; }

    public int? Dpi { get; init; }

    public string? Step2 { get; init; }

    public string? Step3 { get; init; }

    public string? Connection { get; init; }

    public bool Resume { get; init; }

    public string? Job { get; init; }

    public bool RetryFailed { get; init; }

    public string? ResultPath { get; init; }

    public bool Quiet { get; init; }
}

/// <summary>Combines the command line with the settings file: the command line wins (FR-025). Nothing is written back.</summary>
public static class ConvertArgsResolver
{
    /// <summary>
    /// Returns the settings to use for this run, or the problems found. The settings passed in are copied, not changed.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="fromFile">The settings read from settings.json.</param>
    /// <param name="requireInputs">False when resuming: the inputs then only help to pick the job.</param>
    public static (AppSettings? Settings, IReadOnlyList<string> Errors) Resolve(ConvertArgs args, AppSettings fromFile, bool requireInputs = true)
    {
        var errors = new List<string>();

        // Work on a copy so the caller's settings stay as they were read.
        var settings = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(fromFile))!;

        if (args.Inputs.Count > 0)
        {
            settings.Input.Paths = [.. args.Inputs];
        }

        if (args.Out is not null)
        {
            settings.Output.Folder = args.Out;
        }

        if (args.Types is not null)
        {
            var kinds = new List<DocumentKind>();
            foreach (var part in args.Types.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<DocumentKind>(part, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
                {
                    if (!kinds.Contains(kind))
                    {
                        kinds.Add(kind);
                    }
                }
                else
                {
                    errors.Add($"--types: '{part}' is not one of docx, xlsx, pptx.");
                }
            }

            settings.Input.Types = kinds;
        }

        if (args.Recurse is { } recurse)
        {
            settings.Input.Recurse = recurse;
        }

        if (args.IncludeHidden is { } includeHidden)
        {
            settings.Input.IncludeHidden = includeHidden;
        }

        if (args.Engine is not null)
        {
            if (Enum.TryParse<EngineKind>(args.Engine, ignoreCase: true, out var engine) && Enum.IsDefined(engine))
            {
                settings.Engine.Kind = engine;
            }
            else
            {
                errors.Add($"--engine: '{args.Engine}' is not one of office, libreoffice.");
            }
        }

        if (args.Dpi is { } dpi)
        {
            settings.Images.Dpi = dpi;
        }

        ApplyOnOff("--step2", args.Step2, value => settings.Steps.Step2 = value, errors);
        ApplyOnOff("--step3", args.Step3, value => settings.Steps.Step3 = value, errors);

        if (args.Connection is not null)
        {
            settings.Vlm.Selected = args.Connection;
        }

        if (requireInputs && settings.Input.Paths.Count == 0)
        {
            errors.Add("No input was given. Name files or folders on the command line, or set input.paths in settings.json.");
        }

        errors.AddRange(settings.ValidateForRun());
        return errors.Count == 0 ? (settings, errors) : (null, errors);
    }

    private static void ApplyOnOff(string option, string? value, Action<bool> apply, List<string> errors)
    {
        switch (value?.ToLowerInvariant())
        {
            case null:
                break;
            case "on":
                apply(true);
                break;
            case "off":
                apply(false);
                break;
            default:
                errors.Add($"{option}: '{value}' is not 'on' or 'off'.");
                break;
        }
    }
}
