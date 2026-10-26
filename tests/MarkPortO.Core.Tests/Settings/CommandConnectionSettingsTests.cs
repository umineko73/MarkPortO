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

/// <summary>FR-043, FR-044, FR-053, FR-057: the items of a connection of the kind "command", and the limits.</summary>
public class CommandConnectionSettingsTests
{
    private static ConnectionProfile Command(CommandKind kind = CommandKind.Claude) => new()
    {
        Name = "cloud",
        Kind = ConnectionKind.Command,
        Command = kind,
    };

    private static IReadOnlyList<string> Validate(ConnectionProfile connection)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(connection);
        return settings.Validate();
    }

    [Fact]
    public void ANewConnection_IsOpenAiCompatibleAndUnlimited()
    {
        var c = new ConnectionProfile();

        Assert.Equal(ConnectionKind.OpenAi, c.Kind);
        Assert.Null(c.Command);
        Assert.Equal("low", c.Effort);
        Assert.Equal(1, c.Parallel);
        Assert.Null(c.MaxPages);
        Assert.Null(c.MaxCostUsd);
        Assert.Null(c.ExternalConsent);
        Assert.Equal(0, c.MaxLinesPerRead);
    }

    [Fact]
    public void ACommandConnection_NeedsNeitherUrlNorModel()
    {
        Assert.Empty(Validate(Command()));
    }

    [Fact]
    public void ACommandConnection_NeedsTheKindOfTheCommand()
    {
        var c = Command();
        c.Command = null;

        Assert.Single(Validate(c));
    }

    [Fact]
    public void AnOpenAiConnection_StillNeedsUrlAndModel()
    {
        var c = new ConnectionProfile { Name = "x" };

        Assert.Equal(2, Validate(c).Count);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("low", true)]
    [InlineData("medium", true)]
    [InlineData("high", true)]
    [InlineData("max", false)]
    [InlineData("LOW", false)]
    public void Effort_IsEmptyOrLowMediumHigh(string effort, bool valid)
    {
        var c = Command();
        c.Effort = effort;

        Assert.Equal(valid, Validate(c).Count == 0);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(SettingsLimits.MaxParallel, true)]
    [InlineData(0, false)]
    [InlineData(SettingsLimits.MaxParallel + 1, false)]
    public void Parallel_IsAtLeastOneAndAtMostTheLimit(int parallel, bool valid)
    {
        var c = Command();
        c.Parallel = parallel;

        Assert.Equal(valid, Validate(c).Count == 0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    public void MaxPages_IsEmptyOrAtLeastOne_ForBothKinds(int? pages, bool valid)
    {
        var command = Command();
        command.MaxPages = pages;
        var openAi = new ConnectionProfile { Name = "o", BaseUrl = "http://localhost:1234/v1", Model = "m", MaxPages = pages };

        Assert.Equal(valid, Validate(command).Count == 0);
        Assert.Equal(valid, Validate(openAi).Count == 0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0.01, true)]
    [InlineData(25.0, true)]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void MaxCostUsd_IsEmptyOrMoreThanZero(double? cost, bool valid)
    {
        var c = Command();
        c.MaxCostUsd = cost;

        Assert.Equal(valid, Validate(c).Count == 0);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(15, true)]
    [InlineData(-1, false)]
    public void MaxLinesPerRead_IsZeroOrMore(int lines, bool valid)
    {
        var c = Command();
        c.MaxLinesPerRead = lines;

        Assert.Equal(valid, Validate(c).Count == 0);
    }

    [Theory]
    [InlineData(CommandKind.Claude, "haiku")]
    [InlineData(CommandKind.Codex, "gpt-6-luna")]
    [InlineData(CommandKind.OpenCode, "")]
    public void TheInitialModel_DependsOnTheCommand(CommandKind kind, string model)
    {
        Assert.Equal(model, CommandDefaults.InitialModel(kind));
    }

    [Fact]
    public void ASettingsFileOfTheEarlierVersion_StillLoads()
    {
        const string json = """
        { "schemaVersion": 1, "vlm": { "selected": "local", "connections": [
          { "name": "local", "baseUrl": "http://192.168.11.220:1234/v1", "model": "m", "timeoutSeconds": 300 } ] } }
        """;

        var settings = JsonDefaults.Deserialize<AppSettings>(json)!;

        var c = settings.Vlm.Connections.Single();
        Assert.Equal(ConnectionKind.OpenAi, c.Kind);
        Assert.Equal(300, c.TimeoutSeconds);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void TheKindsAreWrittenInLowerCase()
    {
        var c = Command(CommandKind.OpenCode);

        var json = JsonDefaults.Serialize(c);

        Assert.Contains("\"kind\": \"command\"", json);
        Assert.Contains("\"command\": \"opencode\"", json);
    }
}
