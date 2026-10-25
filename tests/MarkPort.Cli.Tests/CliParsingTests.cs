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
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Cli.Tests;

public class CliParsingTests
{
    private static AppSettings FromFile(Action<AppSettings>? change = null)
    {
        var settings = AppSettings.CreateDefault();
        settings.Output.Folder = @"D:\from-file";
        settings.Input.Paths = [@"D:\file-input"];
        change?.Invoke(settings);
        return settings;
    }

    private static AppSettings Resolved(ConvertArgs args, AppSettings file)
    {
        var (settings, errors) = ConvertArgsResolver.Resolve(args, file);
        Assert.True(settings is not null, string.Join("; ", errors));
        return settings!;
    }

    [Fact]
    public void WhenNothingIsGiven_TheSettingsFileDecides()
    {
        var file = FromFile(s => { s.Images.Dpi = 200; s.Input.Recurse = true; s.Steps.Step2 = false; });

        var settings = Resolved(new ConvertArgs(), file);

        Assert.Equal(200, settings.Images.Dpi);
        Assert.True(settings.Input.Recurse);
        Assert.False(settings.Steps.Step2);
        Assert.Equal(@"D:\from-file", settings.Output.Folder);
        Assert.Equal([@"D:\file-input"], settings.Input.Paths);
    }

    [Fact]
    public void TheCommandLineWinsOverTheSettingsFile()
    {
        var file = FromFile(s => { s.Images.Dpi = 200; s.Input.Recurse = true; s.Steps.Step2 = false; s.Input.IncludeHidden = true; });
        var args = new ConvertArgs
        {
            Inputs = [@"D:\cli-input"],
            Out = @"D:\cli-out",
            Dpi = 100,
            Recurse = false,
            IncludeHidden = false,
            Step2 = "on",
            Types = "xlsx, pptx",
            Engine = "libreoffice",
        };

        var settings = Resolved(args, file);

        Assert.Equal(100, settings.Images.Dpi);
        Assert.False(settings.Input.Recurse);
        Assert.False(settings.Input.IncludeHidden);
        Assert.True(settings.Steps.Step2);
        Assert.Equal(@"D:\cli-out", settings.Output.Folder);
        Assert.Equal([@"D:\cli-input"], settings.Input.Paths);
        Assert.Equal([DocumentKind.Xlsx, DocumentKind.Pptx], settings.Input.Types);
        Assert.Equal(EngineKind.LibreOffice, settings.Engine.Kind);
    }

    [Fact]
    public void ResolvingNeverChangesTheSettingsThatWereRead()
    {
        var file = FromFile(s => s.Images.Dpi = 200);

        Resolved(new ConvertArgs { Dpi = 100, Out = @"D:\other" }, file);

        Assert.Equal(200, file.Images.Dpi);
        Assert.Equal(@"D:\from-file", file.Output.Folder);
    }

    [Theory]
    [InlineData("docx", new[] { DocumentKind.Docx })]
    [InlineData("DOCX,xlsx", new[] { DocumentKind.Docx, DocumentKind.Xlsx })]
    [InlineData("pptx;docx pptx", new[] { DocumentKind.Pptx, DocumentKind.Docx })]
    public void Types_AreReadFromAList(string text, DocumentKind[] expected)
    {
        Assert.Equal(expected, Resolved(new ConvertArgs { Types = text }, FromFile()).Input.Types);
    }

    [Theory]
    [InlineData("--types", "doc")]
    [InlineData("--engine", "word")]
    [InlineData("--step2", "maybe")]
    [InlineData("--step3", "1")]
    public void InvalidValues_AreReportedWithTheOptionName(string option, string value)
    {
        var args = option switch
        {
            "--types" => new ConvertArgs { Types = value },
            "--engine" => new ConvertArgs { Engine = value },
            "--step2" => new ConvertArgs { Step2 = value },
            _ => new ConvertArgs { Step3 = value },
        };

        var (settings, errors) = ConvertArgsResolver.Resolve(args, FromFile());

        Assert.Null(settings);
        Assert.Contains(errors, e => e.StartsWith(option, StringComparison.Ordinal) && e.Contains(value));
    }

    [Theory]
    [InlineData(71)]
    [InlineData(601)]
    public void ADpiOutsideTheRange_IsRejected(int dpi)
    {
        var (settings, errors) = ConvertArgsResolver.Resolve(new ConvertArgs { Dpi = dpi }, FromFile());

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("images.dpi") && e.Contains("72") && e.Contains("600"));
    }

    [Fact]
    public void NoInputAnywhere_IsRejected()
    {
        var file = FromFile(s => s.Input.Paths = []);

        var (settings, errors) = ConvertArgsResolver.Resolve(new ConvertArgs(), file);

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("No input"));
    }

    [Fact]
    public void NoOutputFolderAnywhere_IsRejected()
    {
        var file = FromFile(s => s.Output.Folder = string.Empty);

        var (settings, errors) = ConvertArgsResolver.Resolve(new ConvertArgs(), file);

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("output.folder"));
    }

    [Fact]
    public void Step3_WithoutAConnection_IsRejected()
    {
        var (settings, errors) = ConvertArgsResolver.Resolve(new ConvertArgs { Step3 = "on" }, FromFile());

        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("vlm.selected"));
    }

    // ---- parsing of the command line itself ----

    [Fact]
    public async Task UnknownOption_IsReportedAsInvalidArguments()
    {
        var run = await Cli.RunAsync("convert", "x.docx", "--no-such-option");

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("--no-such-option", run.Error);
    }

    [Fact]
    public async Task DpiThatIsNotANumber_IsReportedAsInvalidArguments()
    {
        var run = await Cli.RunAsync("convert", "x.docx", "--dpi", "high");

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
    }

    [Fact]
    public async Task ASettingsFileThatDoesNotExist_IsReported_WhenNamedExplicitly()
    {
        using var temp = new TempDir();

        var run = await Cli.RunAsync("convert", "x.docx", "--settings", temp.Combine("missing.json"), "--out", temp.Combine("o"));

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("missing.json", run.Error);
    }

    [Fact]
    public async Task ASettingsFileWithInvalidValues_IsReportedWithTheOffendingKey()
    {
        using var temp = new TempDir();
        var settingsPath = temp.Combine("settings.json");
        File.WriteAllText(settingsPath, "{ \"images\": { \"dpi\": 9999 } }");

        var run = await Cli.RunAsync("convert", "x.docx", "--settings", settingsPath, "--out", temp.Combine("o"));

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("images.dpi", run.Error);
    }

    [Fact]
    public async Task TheSettingsFile_IsNeverWrittenBack()
    {
        using var temp = new TempDir();
        var settingsPath = temp.Combine("settings.json");
        File.WriteAllText(settingsPath, "{ \"images\": { \"dpi\": 200 } }");
        var before = File.ReadAllText(settingsPath);

        await Cli.RunAsync("convert", temp.Combine("none.docx"), "--settings", settingsPath, "--out", temp.Combine("o"), "--dpi", "100");

        Assert.Equal(before, File.ReadAllText(settingsPath));
    }

    [Fact]
    public async Task Help_ListsTheConvertCommand()
    {
        var run = await Cli.RunAsync("--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("convert", run.Output);
    }

    [Fact]
    public async Task ConvertHelp_ListsTheDocumentedOptions()
    {
        var run = await Cli.RunAsync("convert", "--help");

        foreach (var option in new[] { "--settings", "--out", "--types", "--recurse", "--include-hidden", "--engine", "--dpi", "--step2", "--step3", "--connection", "--resume", "--job", "--retry-failed", "--result", "--quiet" })
        {
            Assert.Contains(option, run.Output);
        }
    }
}
