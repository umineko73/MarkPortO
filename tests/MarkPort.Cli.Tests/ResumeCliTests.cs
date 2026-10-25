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

using MarkPort.Cli;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Cli.Tests;

[Trait("Engine", "LibreOffice")]
public class ResumeCliTests
{
    private static string[] Convert(params string[] more) =>
        [.. new[] { "convert" }, .. more, "--engine", "libreoffice", "--dpi", "72"];

    private static string MakeDocx(TempDir dir, string name, string text)
    {
        var path = dir.Combine("in", name);
        DocxFactory.CreatePaged(path, [text]);
        return path;
    }

    [LibreOfficeFact]
    public async Task AnInterruptedRun_IsListed_AndResumedToTheEnd()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir, "a.docx", "resume me");
        var outFolder = dir.Combine("out");

        // Ctrl+C before anything finished
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var interrupted = await Cli.RunAsync(cts.Token, Convert(source, "--out", outFolder));
        Assert.Equal(ExitCodes.Interrupted, interrupted.ExitCode);
        Assert.False(File.Exists(dir.Combine("out", "a", "a.md")));

        var listed = await Cli.RunAsync("list-jobs", "--out", outFolder);
        Assert.Equal(ExitCodes.Success, listed.ExitCode);
        Assert.Contains("Interrupted", listed.Output);
        Assert.Contains(source, listed.Output);

        var resumed = await Cli.RunAsync(Convert("--resume", "--out", outFolder));
        Assert.Equal(ExitCodes.Success, resumed.ExitCode);
        Assert.Contains("Resumed job", resumed.Output);
        Assert.Contains("a.docx  step1  done", resumed.Output);
        Assert.Contains("resume me", File.ReadAllText(dir.Combine("out", "a", "a.md")));

        // Nothing is left to resume.
        var again = await Cli.RunAsync(Convert("--resume", "--out", outFolder));
        Assert.Equal(ExitCodes.InvalidArguments, again.ExitCode);
        Assert.Contains("no job to resume", again.Error, StringComparison.OrdinalIgnoreCase);
    }

    [LibreOfficeFact]
    public async Task Resume_CanBeAimedAtAJob_ByIdOrByInputs()
    {
        using var dir = new TempDir();
        var a = MakeDocx(dir, "a.docx", "text of a");
        var b = MakeDocx(dir, "b.docx", "text of b");
        var outFolder = dir.Combine("out");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Cli.RunAsync(cts.Token, Convert(a, "--out", outFolder));
        await Cli.RunAsync(cts.Token, Convert(b, "--out", outFolder));

        var listed = await Cli.RunAsync("list-jobs", "--out", outFolder);
        var ids = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]).ToArray();
        Assert.Equal(2, ids.Length);

        // By the inputs named now: only b's job
        var byInput = await Cli.RunAsync(Convert("--resume", "--out", outFolder, b));
        Assert.Equal(ExitCodes.Success, byInput.ExitCode);
        Assert.True(File.Exists(dir.Combine("out", "b", "b.md")));
        Assert.False(File.Exists(dir.Combine("out", "a", "a.md")));

        // By the id: the remaining job
        var remaining = ids.Single(id => !byInput.Output.Contains(id));
        var byId = await Cli.RunAsync(Convert("--resume", "--out", outFolder, "--job", remaining));
        Assert.Equal(ExitCodes.Success, byId.ExitCode);
        Assert.True(File.Exists(dir.Combine("out", "a", "a.md")));
    }

    [LibreOfficeFact]
    public async Task RetryFailed_RunsTheFailedFileAgain()
    {
        using var dir = new TempDir();
        var good = MakeDocx(dir, "good.docx", "good text");
        var broken = dir.Combine("in", "broken.docx");
        DocxFactory.CreateCorrupt(broken);
        var outFolder = dir.Combine("out");
        var first = await Cli.RunAsync(Convert(good, broken, "--out", outFolder));
        Assert.Equal(ExitCodes.SomeFailed, first.ExitCode);

        // Still broken: it fails again.
        var stillBroken = await Cli.RunAsync(Convert("--resume", "--retry-failed", "--out", outFolder));
        Assert.Equal(ExitCodes.SomeFailed, stillBroken.ExitCode);
        Assert.Contains("good.docx  skipped (done)", stillBroken.Output);

        DocxFactory.CreatePaged(broken, ["repaired text"]);
        var repaired = await Cli.RunAsync(Convert("--resume", "--retry-failed", "--out", outFolder));
        Assert.Equal(ExitCodes.Success, repaired.ExitCode);
        Assert.Contains("repaired text", File.ReadAllText(dir.Combine("out", "broken", "broken.md")));
    }

    [Fact]
    public async Task ResumeOptions_AreChecked()
    {
        using var dir = new TempDir();
        var outFolder = dir.Combine("out");
        Directory.CreateDirectory(outFolder);

        var jobWithoutResume = await Cli.RunAsync(Convert("--job", Guid.NewGuid().ToString(), "--out", outFolder));
        var retryWithoutResume = await Cli.RunAsync(Convert("--retry-failed", "--out", outFolder));
        var badJobId = await Cli.RunAsync(Convert("--resume", "--job", "not-a-guid", "--out", outFolder));
        var unknownJob = await Cli.RunAsync(Convert("--resume", "--job", Guid.NewGuid().ToString(), "--out", outFolder));
        var noOutput = await Cli.RunAsync("convert", "--resume", "--settings", dir.Combine("none.json"));

        Assert.All(new[] { jobWithoutResume, retryWithoutResume, badJobId, unknownJob, noOutput }, r => Assert.Equal(ExitCodes.InvalidArguments, r.ExitCode));
        Assert.Contains("only be used together with --resume", jobWithoutResume.Error);
        Assert.Contains("only be used together with --resume", retryWithoutResume.Error);
        Assert.Contains("not a job id", badJobId.Error);
        Assert.Contains("was not found", unknownJob.Error);
    }

    [Fact]
    public async Task ListJobs_ReportsAMissingFolder_AndAnEmptyOne()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("empty"));

        var missing = await Cli.RunAsync("list-jobs", "--out", dir.Combine("none"));
        var noOut = await Cli.RunAsync("list-jobs");
        var empty = await Cli.RunAsync("list-jobs", "--out", dir.Combine("empty"));

        Assert.Equal(ExitCodes.InvalidArguments, missing.ExitCode);
        Assert.Equal(ExitCodes.InvalidArguments, noOut.ExitCode);
        Assert.Equal(ExitCodes.Success, empty.ExitCode);
        Assert.Contains("No jobs", empty.Output);
    }
}
