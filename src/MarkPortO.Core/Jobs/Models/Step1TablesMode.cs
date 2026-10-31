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
using System.Text.Json.Serialization;

namespace MarkPortO.Core.Jobs.Models;

/// <summary>What is done with the tables of the VLM result where the text of the page (Step1) has a table (FR-105).</summary>
[JsonConverter(typeof(Step1TablesModeConverter))]
public enum Step1TablesMode
{
    /// <summary>The tables of the VLM result stay.</summary>
    Off,

    /// <summary>The tables of the VLM result are replaced by the tables of the text of the page.</summary>
    Replace,

    /// <summary>The tables of the VLM result are left out; a quote tells that the table of the document is the one to use.</summary>
    Omit,
}

/// <summary>
/// <c>off</c>, <c>replace</c> or <c>omit</c>. A file from the time when the setting was a switch has <c>true</c> (the tables were left out: <c>omit</c>)
/// or <c>false</c>.
/// </summary>
public sealed class Step1TablesModeConverter : JsonConverter<Step1TablesMode>
{
    public override Step1TablesMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return Step1TablesMode.Omit;
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return Step1TablesMode.Off;
        }

        var text = reader.GetString();
        if (text is not null && Enum.TryParse<Step1TablesMode>(text, ignoreCase: true, out var value))
        {
            return value;
        }

        throw new JsonException($"'{text}' is not a valid {nameof(Step1TablesMode)}.");
    }

    public override void Write(Utf8JsonWriter writer, Step1TablesMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
