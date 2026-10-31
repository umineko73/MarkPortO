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

using System.Text.Json;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-105: the setting is off, replace or omit; a file from the time of the switch has true or false.</summary>
public class Step1TablesModeTests
{
    private sealed record Holder(Step1TablesMode Mode);

    [Theory]
    [InlineData("\"off\"", Step1TablesMode.Off)]
    [InlineData("\"replace\"", Step1TablesMode.Replace)]
    [InlineData("\"Omit\"", Step1TablesMode.Omit)]
    [InlineData("true", Step1TablesMode.Omit)]
    [InlineData("false", Step1TablesMode.Off)]
    public void TheValueIsRead(string json, Step1TablesMode expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<Holder>("{\"Mode\":" + json + "}")!.Mode);

    [Fact]
    public void TheValueIsWrittenInLowerCase() =>
        Assert.Equal("{\"Mode\":\"replace\"}", JsonSerializer.Serialize(new Holder(Step1TablesMode.Replace)));

    [Fact]
    public void AnUnknownWord_IsAnError() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("{\"Mode\":\"half\"}"));
}
