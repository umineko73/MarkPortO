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

using System.CommandLine;
using MarkPort.Core.Convert;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Localization;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

namespace MarkPort.Cli;

/// <summary>Exit codes of the command line (contracts/cli.md).</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Some files or pages failed; the output of the others is kept.</summary>
    public const int SomeFailed = 1;

    /// <summary>The command line or settings.json is not valid.</summary>
    public const int InvalidArguments = 2;

    /// <summary>A check before the start failed (the engine cannot be used ...).</summary>
    public const int StartFailed = 3;

    /// <summary>Interrupted (Ctrl+C); can be resumed.</summary>
    public const int Interrupted = 130;
}

/// <summary>The command line. It only parses arguments and calls <see cref="ConvertService"/>, like the screen does (constitution II).</summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // The messages of the command line are English whatever the language of the computer (FR-037). The language of the
        // Markdown comes from ui.language in settings.json.
        LanguageResolver.Apply(LanguageResolver.English);

        var root = BuildRoot(stdout, stderr, cancellationToken);
        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                await stderr.WriteLineAsync(error.Message);
            }

            await stderr.WriteLineAsync("Run 'markport --help' for usage.");
            return ExitCodes.InvalidArguments;
        }

        return await parse.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, cancellationToken);
    }

    private static RootCommand BuildRoot(TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var root = new RootCommand("MarkPort: converts docx, xlsx and pptx files to Markdown and page images.");

        var inputs = new Argument<string[]>("inputs") { Description = "Files and/or folders to convert.", Arity = ArgumentArity.ZeroOrMore };
        var settings = new Option<string?>("--settings") { Description = "Path of settings.json (default: %APPDATA%\\MarkPort\\settings.json)." };
        var output = new Option<string?>("--out") { Description = "Output folder." };
        var types = new Option<string?>("--types") { Description = "Types to convert in a folder: docx,xlsx,pptx (comma separated)." };
        var recurse = new Option<bool>("--recurse") { Description = "Also convert documents in sub-folders." };
        var noRecurse = new Option<bool>("--no-recurse") { Description = "Do not search sub-folders." };
        var includeHidden = new Option<bool>("--include-hidden") { Description = "Include hidden sheets and slides." };
        var noIncludeHidden = new Option<bool>("--no-include-hidden") { Description = "Leave out hidden sheets and slides." };
        var engine = new Option<string?>("--engine") { Description = "office or libreoffice." };
        var dpi = new Option<int?>("--dpi") { Description = "Resolution of the page images (72-600)." };
        var step2 = new Option<string?>("--step2") { Description = "on or off: put image references in the Markdown." };
        var step3 = new Option<string?>("--step3") { Description = "on or off: read the page images with the VLM." };
        var connection = new Option<string?>("--connection") { Description = "Name of the VLM connection in settings.json." };
        var resume = new Option<bool>("--resume") { Description = "Resume an interrupted run." };
        var job = new Option<string?>("--job") { Description = "With --resume: the job to resume." };
        var retryFailed = new Option<bool>("--retry-failed") { Description = "With --resume: run the failed files again." };
        var result = new Option<string?>("--result") { Description = "Write the result as JSON to this file." };
        var quiet = new Option<bool>("--quiet") { Description = "Show less progress." };

        var convert = new Command("convert", "Convert documents.");
        convert.Arguments.Add(inputs);
        foreach (var option in new Option[]
        {
            settings, output, types, recurse, noRecurse, includeHidden, noIncludeHidden, engine, dpi, step2, step3,
            connection, resume, job, retryFailed, result, quiet,
        })
        {
            convert.Options.Add(option);
        }

        convert.SetAction((parse, token) =>
        {
            static bool? Flag(bool on, bool off) => on ? true : off ? false : null;
            var bound = new ConvertArgs
            {
                Inputs = parse.GetValue(inputs) ?? [],
                SettingsPath = parse.GetValue(settings),
                Out = parse.GetValue(output),
                Types = parse.GetValue(types),
                Recurse = Flag(parse.GetValue(recurse), parse.GetValue(noRecurse)),
                IncludeHidden = Flag(parse.GetValue(includeHidden), parse.GetValue(noIncludeHidden)),
                Engine = parse.GetValue(engine),
                Dpi = parse.GetValue(dpi),
                Step2 = parse.GetValue(step2),
                Step3 = parse.GetValue(step3),
                Connection = parse.GetValue(connection),
                Resume = parse.GetValue(resume),
                Job = parse.GetValue(job),
                RetryFailed = parse.GetValue(retryFailed),
                ResultPath = parse.GetValue(result),
                Quiet = parse.GetValue(quiet),
            };
            return ConvertAsync(bound, stdout, stderr, token == CancellationToken.None ? cancellationToken : token);
        });

        var listJobs = new Command("list-jobs", "List the jobs kept in an output folder (to choose one to resume).");
        var listOut = new Option<string?>("--out") { Description = "Output folder." };
        listJobs.Options.Add(listOut);
        listJobs.SetAction((parse, _) => ListJobsAsync(parse.GetValue(listOut), stdout, stderr));

        var checkConnection = new Command("check-connection", "Check an VLM connection: it can be reached and has the model; with --image, that the model can read a picture.");
        var checkName = new Option<string?>("--connection") { Description = "Name of the connection (default: the selected one in settings.json)." };
        var checkImage = new Option<bool>("--image") { Description = "Also check that the model can read an image." };
        var checkSettings = new Option<string?>("--settings") { Description = "Path of settings.json." };
        checkConnection.Options.Add(checkName);
        checkConnection.Options.Add(checkImage);
        checkConnection.Options.Add(checkSettings);
        checkConnection.SetAction((parse, token) =>
            CheckConnectionAsync(parse.GetValue(checkSettings), parse.GetValue(checkName), parse.GetValue(checkImage), stdout, stderr, token == CancellationToken.None ? cancellationToken : token));

        var listConnections = new Command("list-connections", "List the VLM connections in settings.json (the access key is never shown).");
        var listSettings = new Option<string?>("--settings") { Description = "Path of settings.json." };
        listConnections.Options.Add(listSettings);
        listConnections.SetAction((parse, _) => ListConnectionsAsync(parse.GetValue(listSettings), stdout, stderr));

        root.Subcommands.Add(convert);
        root.Subcommands.Add(listJobs);
        root.Subcommands.Add(checkConnection);
        root.Subcommands.Add(listConnections);
        return root;
    }

    /// <summary>The <c>convert</c> command.</summary>
    internal static async Task<int> ConvertAsync(ConvertArgs args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // The parser takes an unknown option for an input; a misspelt option must not turn into a file name.
        var stray = args.Inputs.FirstOrDefault(i => i.StartsWith('-') && !File.Exists(i) && !Directory.Exists(i));
        if (stray is not null)
        {
            await stderr.WriteLineAsync($"error: unrecognized option or argument '{stray}'.");
            await stderr.WriteLineAsync("Run 'markport convert --help' for usage.");
            return ExitCodes.InvalidArguments;
        }

        // 1. settings.json (an explicit path must exist; the default may be missing)
        var settingsPath = args.SettingsPath ?? SettingsStore.DefaultPath;
        if (args.SettingsPath is not null && !File.Exists(settingsPath))
        {
            await stderr.WriteLineAsync($"settings.json was not found: {settingsPath}");
            return ExitCodes.InvalidArguments;
        }

        AppSettings fromFile;
        using (var store = new SettingsStore(settingsPath))
        {
            var loaded = store.Load();
            foreach (var warning in loaded.Warnings)
            {
                await stderr.WriteLineAsync("warning: " + warning);
            }

            if (loaded.ValidationErrors.Count > 0)
            {
                foreach (var error in loaded.ValidationErrors)
                {
                    await stderr.WriteLineAsync($"error: settings.json: {error}");
                }

                return ExitCodes.InvalidArguments;
            }

            fromFile = loaded.Settings;
        }

        var resuming = args.Resume;
        if (!resuming && (args.Job is not null || args.RetryFailed))
        {
            await stderr.WriteLineAsync("error: --job and --retry-failed can only be used together with --resume.");
            return ExitCodes.InvalidArguments;
        }

        // 2. the command line wins over the file (FR-025); nothing is written back
        var (settings, errors) = ConvertArgsResolver.Resolve(args, fromFile, requireInputs: !resuming);
        if (settings is null)
        {
            foreach (var error in errors)
            {
                await stderr.WriteLineAsync("error: " + error);
            }

            return ExitCodes.InvalidArguments;
        }

        if (resuming)
        {
            return await ResumeAsync(args, settings, stdout, stderr, cancellationToken);
        }

        // 3. the documents
        var found = InputEnumerator.Enumerate(settings.Input.Paths, settings.Input.Types, settings.Input.Recurse);
        foreach (var warning in found.Warnings)
        {
            await stderr.WriteLineAsync("warning: " + warning);
        }

        if (found.Inputs.Count == 0)
        {
            await stderr.WriteLineAsync("error: no documents to convert were found.");
            return ExitCodes.InvalidArguments;
        }

        var request = new ConvertRequest
        {
            Inputs = found.Inputs,
            GivenInputs = settings.Input.Paths,
            InputFolder = found.InputFolder,
            OutputFolder = settings.Output.Folder,
            Options = ConvertRequest.OptionsFrom(settings),
        };

        // 4. run
        VlmSetup? vlm = null;
        if (settings.Steps.Step3)
        {
            var (setup, setupError) = VlmSetupFactory.Create(settings);
            if (setup is null)
            {
                await stderr.WriteLineAsync("error: " + setupError);
                return ExitCodes.InvalidArguments;
            }

            vlm = setup;
        }

        using var vlmLifetime = vlm;
        var engine = EngineFactory.Create(settings.Engine);
        using var engineLifetime = engine as IDisposable;
        var service = new ConvertService(engine, vlm: vlm);
        var progress = new ConsoleProgress(stdout, args.Quiet);
        service.DocumentChanged += progress.Report;

        ConvertResult result;
        try
        {
            result = await service.RunAsync(request, cancellationToken);
        }
        catch (ConvertStartException ex)
        {
            await stderr.WriteLineAsync("error: " + ex.Message);
            return ExitCodes.StartFailed;
        }

        return await FinishAsync(args, result, stdout, stderr);
    }

    /// <summary>The <c>convert --resume</c> command: resume an interrupted job, or retry its failed files.</summary>
    private static async Task<int> ResumeAsync(ConvertArgs args, AppSettings settings, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        Guid? jobId = null;
        if (args.Job is not null)
        {
            if (!Guid.TryParse(args.Job, out var parsed))
            {
                await stderr.WriteLineAsync($"error: --job: '{args.Job}' is not a job id (see 'markport list-jobs').");
                return ExitCodes.InvalidArguments;
            }

            jobId = parsed;
        }

        // Only steps named on the command line change; the rest is as in the earlier run.
        bool? Step(string? value) => value?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null };

        var request = new ResumeRequest
        {
            OutputFolder = settings.Output.Folder,
            JobId = jobId,
            GivenInputs = args.Inputs,
            Mode = args.RetryFailed ? ResumeMode.RetryFailed : ResumeMode.Unfinished,
            Step2 = Step(args.Step2),
            Step3 = Step(args.Step3),
            Connection = args.Connection,
        };

        // The VLM connection is made when one is selected: the earlier run may have used step 3, and a resume then
        // checks the connection again. It is an error only when --step3 on asks for it explicitly.
        VlmSetup? vlm = null;
        if (!string.IsNullOrWhiteSpace(settings.Vlm.Selected))
        {
            var (setup, setupError) = VlmSetupFactory.Create(settings);
            if (setup is null && string.Equals(args.Step3, "on", StringComparison.OrdinalIgnoreCase))
            {
                await stderr.WriteLineAsync("error: " + setupError);
                return ExitCodes.InvalidArguments;
            }

            vlm = setup;
        }

        using var vlmLifetime = vlm;
        var engine = EngineFactory.Create(settings.Engine);
        using var engineLifetime = engine as IDisposable;
        var service = new ConvertService(engine, vlm: vlm);
        var progress = new ConsoleProgress(stdout, args.Quiet);
        service.DocumentChanged += progress.Report;
        service.DocumentSkipped += progress.Skipped;

        ConvertResult result;
        try
        {
            result = await service.ResumeAsync(request, cancellationToken);
        }
        catch (JobNotFoundException ex)
        {
            await stderr.WriteLineAsync("error: " + ex.Message);
            return ExitCodes.InvalidArguments;
        }
        catch (ConvertStartException ex)
        {
            await stderr.WriteLineAsync("error: " + ex.Message);
            return ExitCodes.StartFailed;
        }

        await stdout.WriteLineAsync($"Resumed job {result.Job.JobId}.");
        return await FinishAsync(args, result, stdout, stderr);
    }

    /// <summary>Writes the result file, prints the summary, and gives the exit code.</summary>
    private static async Task<int> FinishAsync(ConvertArgs args, ConvertResult result, TextWriter stdout, TextWriter stderr)
    {
        if (args.ResultPath is not null)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(args.ResultPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(args.ResultPath, ConvertResultJson.Serialize(result), MarkPort.Core.IO.AtomicFile.Utf8NoBom, CancellationToken.None);
        }

        var done = result.Documents.Count(d => d.IsDone());
        var failed = result.Documents.Count(d => d.IsFailed());
        var failedPages = result.Documents.Sum(d => d.Pages.Count(p => p.Step3 == PageStep3Status.Failed || p.Error is not null));
        await stdout.WriteLineAsync($"{done} done, {failed} failed, {result.Documents.Count - done - failed} not finished. Output: {result.Job.OutputRoot}");
        if (failedPages > 0)
        {
            await stdout.WriteLineAsync($"{failedPages} page(s) could not be processed (see the warnings in the result and the Markdown).");
        }

        foreach (var document in result.Documents.Where(d => d.IsFailed()))
        {
            await stderr.WriteLineAsync($"failed: {document.SourcePath}: {document.Error?.Message}");
        }

        if (result.WasInterrupted)
        {
            return ExitCodes.Interrupted;
        }

        return failed > 0 || failedPages > 0 ? ExitCodes.SomeFailed : ExitCodes.Success;
    }

    /// <summary>The <c>check-connection</c> command (FR-019b).</summary>
    private static async Task<int> CheckConnectionAsync(string? settingsPath, string? name, bool image, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (settings, exit) = await LoadSettingsAsync(settingsPath, stderr);
        if (settings is null)
        {
            return exit;
        }

        var connection = settings.FindConnection(name ?? settings.Vlm.Selected);
        if (connection is null)
        {
            await stderr.WriteLineAsync($"error: the connection '{name ?? settings.Vlm.Selected}' was not found in settings.json (see 'markport list-connections').");
            return ExitCodes.InvalidArguments;
        }

        var key = ApiKeyResolver.Resolve(connection);
        if (key.Error is not null)
        {
            await stderr.WriteLineAsync("error: " + key.Error);
            return ExitCodes.InvalidArguments;
        }

        using var client = new VlmClient(connection, key.Key);
        var checker = new ConnectionChecker(client, connection);

        var connectionResult = await checker.CheckConnectionAsync(cancellationToken);
        await stdout.WriteLineAsync((connectionResult.Ok ? "OK  connection: " : "NG  connection: ") + connectionResult.Message);
        var ok = connectionResult.Ok;

        if (image)
        {
            var imageResult = await checker.CheckImageAsync(cancellationToken);
            await stdout.WriteLineAsync((imageResult.Ok ? "OK  image: " : "NG  image: ") + imageResult.Message);
            ok &= imageResult.Ok;
        }

        return ok ? ExitCodes.Success : ExitCodes.StartFailed;
    }

    /// <summary>The <c>list-connections</c> command. The access key is never printed, only where it comes from.</summary>
    private static async Task<int> ListConnectionsAsync(string? settingsPath, TextWriter stdout, TextWriter stderr)
    {
        var (settings, exit) = await LoadSettingsAsync(settingsPath, stderr);
        if (settings is null)
        {
            return exit;
        }

        if (settings.Vlm.Connections.Count == 0)
        {
            await stdout.WriteLineAsync("No connections.");
            return ExitCodes.Success;
        }

        foreach (var connection in settings.Vlm.Connections)
        {
            var selected = string.Equals(connection.Name, settings.Vlm.Selected, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            await stdout.WriteLineAsync(
                $"{selected} {connection.Name}  {connection.BaseUrl}  model={connection.Model}  timeout={connection.TimeoutSeconds}s  key={ApiKeyResolver.Describe(connection)}");
        }

        return ExitCodes.Success;
    }

    private static async Task<(AppSettings? Settings, int Exit)> LoadSettingsAsync(string? settingsPath, TextWriter stderr)
    {
        var path = settingsPath ?? SettingsStore.DefaultPath;
        if (settingsPath is not null && !File.Exists(path))
        {
            await stderr.WriteLineAsync($"settings.json was not found: {path}");
            return (null, ExitCodes.InvalidArguments);
        }

        using var store = new SettingsStore(path);
        var loaded = store.Load();
        foreach (var warning in loaded.Warnings)
        {
            await stderr.WriteLineAsync("warning: " + warning);
        }

        if (loaded.ValidationErrors.Count > 0)
        {
            foreach (var error in loaded.ValidationErrors)
            {
                await stderr.WriteLineAsync($"error: settings.json: {error}");
            }

            return (null, ExitCodes.InvalidArguments);
        }

        return (loaded.Settings, ExitCodes.Success);
    }

    /// <summary>The <c>list-jobs</c> command.</summary>
    private static async Task<int> ListJobsAsync(string? outFolder, TextWriter stdout, TextWriter stderr)
    {
        if (string.IsNullOrWhiteSpace(outFolder) || !Directory.Exists(outFolder))
        {
            await stderr.WriteLineAsync($"error: --out: the output folder was not found: {outFolder}");
            return ExitCodes.InvalidArguments;
        }

        var jobs = new StateStore(outFolder).ListJobs();
        if (jobs.Count == 0)
        {
            await stdout.WriteLineAsync("No jobs.");
            return ExitCodes.Success;
        }

        var store = new StateStore(outFolder);
        foreach (var job in jobs)
        {
            var status = job.Status == JobStatus.Running && !store.IsLocked(job.JobId) ? "Interrupted" : job.Status.ToString();
            await stdout.WriteLineAsync(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{job.JobId}  {status,-20}  {job.UpdatedAt:yyyy-MM-ddTHH:mm:ssZ}  {string.Join(" ", job.Inputs)}"));
        }

        return ExitCodes.Success;
    }
}
