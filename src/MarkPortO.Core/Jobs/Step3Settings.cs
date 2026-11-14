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

using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Jobs;

/// <summary>
/// The settings of Step 3 that decide what the VLM answers (the connection with its model, and the prompt of the kind of file), in the words in which
/// a reading records them (<c>PageRecord.VlmModel</c>, <c>PageRecord.VlmPrompt</c>). A page that was read with other settings than those of now is out of date (FR-139).
/// </summary>
public static class Step3Settings
{
    /// <summary>The model of a reading, as Step 3 records it: the command and the model, or the model, or the kind of the connection.</summary>
    public static string ModelText(string kind, string? command, string? model) =>
        string.Join(' ', new[] { kind == "command" ? command : null, model }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } text ? text : kind;

    /// <summary>The model that a connection of the settings gives; null without a connection.</summary>
    public static string? ModelText(ConnectionProfile? connection) => connection is null
        ? null
        : ModelText(connection.Kind.ToString().ToLowerInvariant(), connection.Command?.ToString().ToLowerInvariant(), string.IsNullOrWhiteSpace(connection.Model) ? null : connection.Model);

    /// <summary>The prompt of a reading, as Step 3 records it: <c>standard</c>, or the name.</summary>
    public static string PromptText(string? name) => PromptPresets.IsStandardName(name) ? "standard" : name!.Trim();
}
