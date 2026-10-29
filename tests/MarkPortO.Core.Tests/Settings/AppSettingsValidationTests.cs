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

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Tests.Settings;

public class AppSettingsValidationTests
{
    private static ConnectionProfile Connection(string name = "local") => new()
    {
        Name = name,
        BaseUrl = "http://localhost:1234/v1",
        Model = "vision-model",
    };

    [Fact]
    public void Defaults_MatchTheDataModel()
    {
        var s = AppSettings.CreateDefault();

        Assert.Equal(150, s.Images.Dpi);
        Assert.True(s.Steps.Step2);
        Assert.False(s.Steps.Step3);
        Assert.False(s.Input.IncludeHiddenSheets);
        Assert.False(s.Input.IncludeHiddenSlides);
        Assert.False(s.Input.Recurse);
        Assert.Equal(EngineKind.Office, s.Engine.Kind);
        Assert.Equal([DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx], s.Input.Types);
        Assert.Empty(s.Validate());
    }

    [Theory]
    [InlineData(72, true)]
    [InlineData(150, true)]
    [InlineData(600, true)]
    [InlineData(71, false)]
    [InlineData(601, false)]
    [InlineData(0, false)]
    public void Dpi_MustBeBetween72And600(int dpi, bool valid)
    {
        var s = AppSettings.CreateDefault();
        s.Images.Dpi = dpi;

        Assert.Equal(valid, s.Validate().Count == 0);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(3600, true)]
    [InlineData(0, false)]
    [InlineData(3601, false)]
    public void TimeoutSeconds_MustBeBetween1And3600(int seconds, bool valid)
    {
        var s = AppSettings.CreateDefault();
        var c = Connection();
        c.TimeoutSeconds = seconds;
        s.Vlm.Connections.Add(c);

        Assert.Equal(valid, s.Validate().Count == 0);
    }

    [Theory]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("localhost:1234", false)]
    [InlineData("", false)]
    public void BaseUrl_MustBeHttpOrHttps(string url, bool valid)
    {
        var s = AppSettings.CreateDefault();
        var c = Connection();
        c.BaseUrl = url;
        s.Vlm.Connections.Add(c);

        Assert.Equal(valid, s.Validate().Count == 0);
    }

    [Fact]
    public void ConnectionNames_MustBeUnique()
    {
        var s = AppSettings.CreateDefault();
        s.Vlm.Connections.Add(Connection("local"));
        s.Vlm.Connections.Add(Connection("LOCAL"));

        Assert.Contains(s.Validate(), e => e.Contains("more than once"));
    }

    [Fact]
    public void Model_MustNotBeEmpty()
    {
        var s = AppSettings.CreateDefault();
        var c = Connection();
        c.Model = " ";
        s.Vlm.Connections.Add(c);

        Assert.Contains(s.Validate(), e => e.Contains(".model"));
    }

    [Fact]
    public void Types_MustNotBeEmptyOrDuplicated()
    {
        var s = AppSettings.CreateDefault();
        s.Input.Types = [];
        Assert.NotEmpty(s.Validate());

        s.Input.Types = [DocumentKind.Docx, DocumentKind.Docx];
        Assert.NotEmpty(s.Validate());

        s.Input.Types = [DocumentKind.Pptx];
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Prompts_MustNotBeEmpty()
    {
        var s = AppSettings.CreateDefault();
        s.Vlm.Prompts.Xlsx = "";

        Assert.Contains(s.Validate(), e => e.Contains("vlm.prompts.xlsx"));
    }

    [Fact]
    public void ValidateForRun_RequiresOutputFolder()
    {
        var s = AppSettings.CreateDefault();
        Assert.Contains(s.ValidateForRun(), e => e.Contains("output.folder"));

        s.Output.Folder = @"D:\out";
        Assert.Empty(s.ValidateForRun());
    }

    [Fact]
    public void ValidateForRun_WithStep3_RequiresASelectedConnection()
    {
        var s = AppSettings.CreateDefault();
        s.Output.Folder = @"D:\out";
        s.Steps.Step3 = true;
        Assert.Contains(s.ValidateForRun(), e => e.Contains("vlm.selected"));

        s.Vlm.Connections.Add(Connection("local"));
        s.Vlm.Selected = "local";
        Assert.Empty(s.ValidateForRun());
    }

    [Fact]
    public void PromptFor_ReturnsThePromptOfTheKind()
    {
        var p = new PromptSettings { Docx = "d", Xlsx = "x", Pptx = "p" };

        Assert.Equal("d", p.For(DocumentKind.Docx));
        Assert.Equal("x", p.For(DocumentKind.Xlsx));
        Assert.Equal("p", p.For(DocumentKind.Pptx));
    }
}
