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

using System.Text.Json.Serialization;

namespace MarkPortO.Core.Jobs.Models;

/// <summary>How Step3 uses the VLM (FR-150).</summary>
[JsonConverter(typeof(LowercaseEnumConverter<Step3Mode>))]
public enum Step3Mode
{
    /// <summary>The text of Step1 is kept; the VLM reads only what Step1 does not have (the pictures), or a whole page that Step1 has (almost) nothing of.</summary>
    Supplement,

    /// <summary>The VLM reads every page whole and writes it out (what Step3 did before FR-150).</summary>
    Transcribe,
}

/// <summary>How a page was read by Step3 (FR-150).</summary>
[JsonConverter(typeof(LowercaseEnumConverter<PageReading>))]
public enum PageReading
{
    /// <summary>The page image was read whole with the prompt of the kind. A page read before FR-150 was read so.</summary>
    Whole,

    /// <summary>The pictures that Step1 saved were read one by one; the text of the page is that of Step1.</summary>
    Pictures,

    /// <summary>Nothing was sent to the VLM: the text of Step1 is enough.</summary>
    None,
}

/// <summary>What the VLM read in a picture that Step1 saved (FR-150).</summary>
/// <param name="File">The path of the picture, from the document folder (<c>pictures/page-005-1.png</c>).</param>
/// <param name="Text">What the VLM wrote; empty when it had nothing to add.</param>
public sealed record FigureReading(string File, string Text);
