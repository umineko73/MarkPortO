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

using System.Globalization;
using DocumentFormat.OpenXml.Packaging;

namespace MarkPortO.Core.Ooxml;

/// <summary>A picture taken out of a document (FR-133): its bytes and the extension of its file (<c>.png</c>).</summary>
public sealed record CollectedPicture(byte[] Data, string Extension);

/// <summary>
/// Collects the pictures of a docx or pptx while it is read (FR-133). The page of a picture is not known yet (a docx is put on its pages later),
/// so a picture is written in the Markdown as <c>![text](mark)</c>, and the mark is replaced by the name of its file once the pages are known
/// (<see cref="Pipeline.PictureFiles"/>).
/// </summary>
public sealed class PictureCollector
{
    /// <summary>The characters around the number of a picture in its mark: private, so that no text of a document has them.</summary>
    public const char MarkStart = '';

    public const char MarkEnd = '';

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/gif"] = ".gif",
    };

    private readonly List<CollectedPicture> _pictures = [];

    /// <summary>The pictures, by the number in their mark.</summary>
    public IReadOnlyList<CollectedPicture> Pictures => _pictures;

    /// <summary>The pictures that are not saved, and why, for the log of the document.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>
    /// The mark of the picture in <paramref name="relationshipId"/> of <paramref name="owner"/>, or null when it is not saved (a link only, a format
    /// that is not shown by a browser, such as EMF); <paramref name="name"/> names it in the log.
    /// </summary>
    public string? Add(OpenXmlPart owner, string? relationshipId, string? name)
    {
        var what = string.IsNullOrWhiteSpace(name) ? "a picture" : $"the picture '{name.Trim()}'";
        if (string.IsNullOrEmpty(relationshipId))
        {
            Skipped.Add($"{what} is not saved: it is only a link to a file outside the document");
            return null;
        }

        if (owner.Parts.Where(p => p.RelationshipId == relationshipId).Select(p => p.OpenXmlPart).FirstOrDefault() is not ImagePart image)
        {
            Skipped.Add($"{what} is not saved: its file is not in the document");
            return null;
        }

        if (!Extensions.TryGetValue(image.ContentType, out var extension))
        {
            Skipped.Add($"{what} is not saved: {image.ContentType} is not a format that is saved (PNG, JPEG, GIF)");
            return null;
        }

        using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        _pictures.Add(new CollectedPicture(copy.ToArray(), extension));
        return MarkStart + (_pictures.Count - 1).ToString(CultureInfo.InvariantCulture) + MarkEnd;
    }

    /// <summary>A picture in Markdown: <c>![text](mark)</c>, or, without a mark, <c>![text]</c> (nothing without a text, FR-071).</summary>
    public static string Markdown(string? description, string? mark)
    {
        var text = (description ?? string.Empty).Trim().Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return mark is null ? (text.Length == 0 ? string.Empty : "![" + text + "]") : "![" + text + "](" + mark + ")";
    }
}
