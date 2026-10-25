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
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Cli.Tests;

[Trait("Engine", "LibreOffice")]
public class ExitCodeTests
{
    private static string[] Common(TempDir dir, params string[] more) =>
        [.. new[] { "convert" }, .. more, "--engine", "libreoffice", "--dpi", "72", "--no-include-hidden"];

    private static string MakeDocx(TempDir dir, string name, params string[] paragraphs)
    {
        var path = dir.Combine("in", name);
        DocxFactory.CreatePaged(path, paragraphs);
        return path;
    }

    [LibreOfficeFact]
    public async Task AllFilesConverted_ExitsWith0_AndPrintsTheProgress()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir, "a.docx", "hello");

        var run = await Cli.RunAsync([.. Common(dir, source), "--out", dir.Combine("out")]);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(dir.Combine("out", "a", "a.md")));
        Assert.Contains("d0001 a.docx  step1  running", run.Output);
        Assert.Contains("d0001 a.docx  step1  done  pages=1", run.Output);
        Assert.Contains("d0001 a.docx  step2  done", run.Output);
        Assert.Contains("1 done, 0 failed", run.Output);
        Assert.Equal(string.Empty, run.Error);
    }

    [LibreOfficeFact]
    public async Task SomeFilesFailed_ExitsWith1_ListsThem_AndKeepsTheOthers()
    {
        using var dir = new TempDir();
        var good = MakeDocx(dir, "good.docx", "good text");
        var broken = dir.Combine("in", "broken.docx");
        DocxFactory.CreateCorrupt(broken);

        var run = await Cli.RunAsync([.. Common(dir, good, broken), "--out", dir.Combine("out")]);

        Assert.Equal(ExitCodes.SomeFailed, run.ExitCode);
        Assert.Contains("1 done, 1 failed", run.Output);
        Assert.Contains("failed: " + broken, run.Error);
        Assert.Contains("step1  failed", run.Output);
        Assert.True(File.Exists(dir.Combine("out", "good", "good.md")));
    }

    [Fact]
    public async Task InvalidArguments_ExitWith2()
    {
        using var dir = new TempDir();

        var badDpi = await Cli.RunAsync("convert", dir.Combine("a.docx"), "--out", dir.Combine("o"), "--dpi", "5");
        var noOutput = await Cli.RunAsync("convert", dir.Combine("a.docx"), "--settings", dir.Combine("none.json"));

        Assert.Equal(ExitCodes.InvalidArguments, badDpi.ExitCode);
        Assert.Contains("images.dpi", badDpi.Error);
        Assert.Equal(ExitCodes.InvalidArguments, noOutput.ExitCode);
    }

    [Fact]
    public async Task AFolderWithNoDocuments_ExitsWith2_AndWritesNothing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("empty"));

        var run = await Cli.RunAsync("convert", dir.Combine("empty"), "--out", dir.Combine("out"), "--engine", "libreoffice");

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("no documents", run.Error);
        Assert.False(Directory.Exists(dir.Combine("out")));
    }

    [Fact]
    public async Task AnEngineThatCannotBeUsed_ExitsWith3_AndExplainsWhy()
    {
        using var dir = new TempDir();
        var source = MakeDocxWithoutEngine(dir);
        var settings = dir.Combine("settings.json");
        File.WriteAllText(settings, "{ \"engine\": { \"kind\": \"libreoffice\", \"libreOfficePath\": \"C:\\\\no\\\\such\\\\soffice.exe\" } }");

        var run = await Cli.RunAsync("convert", source, "--settings", settings, "--out", dir.Combine("out"));

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("soffice.exe", run.Error);
        Assert.False(Directory.Exists(dir.Combine("out")));
    }

    [OfficeFact]
    [Trait("Engine", "Office")]
    public async Task TheOfficeEngine_ConvertsThroughTheCommandLine()
    {
        using var dir = new TempDir();
        var source = MakeDocxWithoutEngine(dir);

        var run = await Cli.RunAsync("convert", source, "--out", dir.Combine("out"), "--engine", "office", "--dpi", "72");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("text", File.ReadAllText(dir.Combine("out", "a", "a.md")));
    }

    [LibreOfficeFact]
    public async Task Cancelled_ExitsWith130()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir, "a.docx", "text");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = await Cli.RunAsync(cts.Token, [.. Common(dir, source), "--out", dir.Combine("out")]);

        Assert.Equal(ExitCodes.Interrupted, run.ExitCode);
    }

    [Fact]
    public async Task ExitCodeConstants_MatchTheContract()
    {
        await Task.CompletedTask;
        Assert.Equal([0, 1, 2, 3, 130], [ExitCodes.Success, ExitCodes.SomeFailed, ExitCodes.InvalidArguments, ExitCodes.StartFailed, ExitCodes.Interrupted]);
    }

    private static string MakeDocxWithoutEngine(TempDir dir)
    {
        var path = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(path, ["text"]);
        return path;
    }
}
