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
using MarkPortO.Core.Extraction;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Extraction;

/// <summary>FR-110 and FR-112 (experimental): the extractor is run without a shell, and what it gives is checked.</summary>
public class ExtractorRunnerTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static ExtractorProfile Profile(ExtractorOutput output, params string[] args) =>
        new() { Name = "t", Command = Cmd, Args = [.. args], Output = output, TimeoutSeconds = 20 };

    [Fact]
    public void ThePlaceholders_AreReplaced_AlsoInsideAnArgument()
    {
        var profile = new ExtractorProfile { Name = "t", Command = "x", Args = ["{input}", "--out={output}", "-d", "{outdir}"] };

        var args = ExtractorRunner.ExpandArguments(profile, @"D:\in\a b.pdf", @"D:\w", @"D:\w\out.md");

        Assert.Equal([@"D:\in\a b.pdf", @"--out=D:\w\out.md", "-d", @"D:\w"], args);
    }

    [Theory]
    [InlineData("", "x", "{input}", null, "no name")]
    [InlineData("t", "", "{input}", null, "no command")]
    [InlineData("t", "x", "--flag", null, "no {input}")]
    [InlineData("t", "x", "{input}", null, "no {output} or {outdir}")]
    public void AnIncompleteProfile_IsRefused(string name, string command, string arg, string? extra, string reason)
    {
        var profile = new ExtractorProfile { Name = name, Command = command, Args = [arg], Output = ExtractorOutput.File };

        var problem = ExtractorRunner.Validate(profile);

        Assert.NotNull(problem);
        Assert.True(reason.Length > 0 && extra is null);
    }

    [Fact]
    public void AProfileThatWritesToTheOutputOrToTheOutdir_IsComplete()
    {
        Assert.Null(ExtractorRunner.Validate(new ExtractorProfile { Name = "t", Command = "x", Args = ["{input}", "-o", "{output}"] }));
        Assert.Null(ExtractorRunner.Validate(new ExtractorProfile { Name = "t", Command = "x", Args = ["{input}", "--output", "{outdir}"] }));
        Assert.Null(ExtractorRunner.Validate(new ExtractorProfile { Name = "t", Command = "x", Args = ["{input}"], Output = ExtractorOutput.Stdout }));
    }

    [Fact]
    public async Task TheStandardOutput_IsTheMarkdown()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, "hello");

        var result = await ExtractorRunner.RunAsync(Profile(ExtractorOutput.Stdout, "/c", "type", "{input}"), input, dir.Combine("w"), CancellationToken.None);

        Assert.Equal("hello", result.Markdown.Trim());
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.MarkdownFile);
    }

    [Fact]
    public async Task TheFileOfTheOutputPlaceholder_IsTheMarkdown()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, "# Title");

        var result = await ExtractorRunner.RunAsync(Profile(ExtractorOutput.File, "/c", "copy", "/y", "{input}", "{output}"), input, dir.Combine("w"), CancellationToken.None);

        Assert.Equal("# Title", result.Markdown.Trim());
        Assert.NotNull(result.MarkdownFile);
    }

    [Fact]
    public async Task TheMarkdownFileThatTheCommandNamedItself_IsFoundInTheOutdir()
    {
        using var dir = new TempDir();
        var input = dir.Combine("report.txt");
        await File.WriteAllTextAsync(input, "text");

        // The command is given the folder only, and writes <name>.md there (like Docling).
        var result = await ExtractorRunner.RunAsync(Profile(ExtractorOutput.File, "/c", "copy", "/y", "{input}", @"{outdir}\report.md"), input, dir.Combine("w"), CancellationToken.None);

        Assert.Equal("text", result.Markdown.Trim());
    }

    [Fact]
    public async Task ACommandThatFails_GivesAnErrorWithTheExitCode()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, "x");

        var ex = await Assert.ThrowsAsync<ExtractorException>(() =>
            ExtractorRunner.RunAsync(Profile(ExtractorOutput.Stdout, "/c", "exit", "3", "&", "rem", "{input}"), input, dir.Combine("w"), CancellationToken.None));

        Assert.Contains("exit code 3", ex.Message);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsAFailure()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, string.Empty);

        var ex = await Assert.ThrowsAsync<ExtractorException>(() =>
            ExtractorRunner.RunAsync(Profile(ExtractorOutput.Stdout, "/c", "type", "{input}"), input, dir.Combine("w"), CancellationToken.None));

        Assert.Contains("no text", ex.Message);
    }

    [Fact]
    public async Task ACommandThatTakesTooLong_IsStopped()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, "x");
        var profile = Profile(ExtractorOutput.Stdout, "/c", "ping", "-n", "30", "127.0.0.1", ">", "nul", "&", "rem", "{input}");
        profile.TimeoutSeconds = 1;
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ExtractorException>(() => ExtractorRunner.RunAsync(profile, input, dir.Combine("w"), CancellationToken.None));

        Assert.Contains("did not finish", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AProgramThatIsNotThere_IsReportedAsNotFound()
    {
        using var dir = new TempDir();
        var profile = new ExtractorProfile { Name = "t", Command = "no-such-program-markporto", Args = ["{input}"], Output = ExtractorOutput.Stdout };

        var ex = await Assert.ThrowsAsync<ExtractorException>(() => ExtractorRunner.RunAsync(profile, dir.Combine("a.txt"), dir.Combine("w"), CancellationToken.None));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public async Task TheValueOfAnEnvironmentVariable_IsNotInTheErrorOfTheCommand()
    {
        using var dir = new TempDir();
        var input = dir.Combine("a.txt");
        await File.WriteAllTextAsync(input, "x");
        var profile = Profile(ExtractorOutput.Stdout, "/c", "echo", "%SECRET_FOR_TEST%", "1>&2", "&", "exit", "1", "&", "rem", "{input}");
        profile.Env["SECRET_FOR_TEST"] = "sk-top-secret-value";

        var ex = await Assert.ThrowsAsync<ExtractorException>(() => ExtractorRunner.RunAsync(profile, input, dir.Combine("w"), CancellationToken.None));

        Assert.DoesNotContain("sk-top-secret-value", ex.Message);
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public void TheChoiceForAKind_IsEmptyForTheBuiltInOne()
    {
        var choice = new ExtractorChoice { Pdf = " Docling " };

        Assert.Equal("Docling", choice.For(DocumentKind.Pdf));
        Assert.Equal(string.Empty, choice.For(DocumentKind.Docx));
        Assert.True(choice.IsAny);
        Assert.False(new ExtractorChoice().IsAny);
    }
}
