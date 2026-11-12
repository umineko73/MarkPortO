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
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;

namespace MarkPortO.Core.Ooxml;

/// <summary>A picture taken out of a document (FR-133): its bytes and the extension of its file (<c>.png</c>).</summary>
/// <param name="Hash">The SHA-256 of the picture as the document has it (before it is turned into a PNG, FR-134): the same picture is one file.</param>
/// <param name="ConvertedFrom">The content type that it was turned into a PNG from; null when it is saved as it is.</param>
public sealed record CollectedPicture(byte[] Data, string Extension, string Hash, string? ConvertedFrom = null);

/// <summary>
/// Collects the pictures of a docx, pptx or xlsx while it is read (FR-133 to FR-135). The page of a picture is not known yet (a docx is put on its pages later),
/// so a picture is written in the Markdown as <c>![text](mark)</c>, and the mark is replaced by the name of its file once the pages are known
/// (<see cref="Pipeline.PictureFiles"/>).
/// </summary>
/// <param name="dpi">The resolution at which a metafile (EMF, WMF) is drawn into a PNG (FR-134): the DPI of the page images.</param>
public sealed class PictureCollector(int dpi = 150)
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

    // The same picture (in two parts of a document, or twice in one) is collected and turned into a PNG once.
    private readonly Dictionary<string, int> _byHash = new(StringComparer.Ordinal);

    private static string Mark(int number) => MarkStart + number.ToString(CultureInfo.InvariantCulture) + MarkEnd;

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

        var saved = Extensions.TryGetValue(image.ContentType, out var extension);
        if (!saved && !PictureConverter.CanConvert(image.ContentType))
        {
            Skipped.Add($"{what} is not saved: {image.ContentType} is not a format that is saved (PNG, JPEG, GIF) or turned into a PNG (EMF, WMF, BMP, TIFF)");
            return null;
        }

        byte[] data;
        using (var stream = image.GetStream(FileMode.Open, FileAccess.Read))
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            data = copy.ToArray();
        }

        var hash = System.Convert.ToHexString(SHA256.HashData(data));
        if (_byHash.TryGetValue(hash, out var known))
        {
            return Mark(known);
        }

        CollectedPicture picture;
        if (saved)
        {
            picture = new CollectedPicture(data, extension!, hash);
        }
        else
        {
            try
            {
                picture = new CollectedPicture(PictureConverter.ToPng(data, image.ContentType, dpi), ".png", hash, image.ContentType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // GDI+ says "out of memory" (or ArgumentException) for a file that it cannot read.
                Skipped.Add($"{what} is not saved: the {image.ContentType} could not be turned into a PNG ({ex.GetType().Name}: {ex.Message})");
                return null;
            }
        }

        _pictures.Add(picture);
        _byHash[hash] = _pictures.Count - 1;
        return Mark(_pictures.Count - 1);
    }

    /// <summary>A picture in Markdown: <c>![text](mark)</c>, or, without a mark, <c>![text]</c> (nothing without a text, FR-071).</summary>
    public static string Markdown(string? description, string? mark)
    {
        var text = (description ?? string.Empty).Trim().Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return mark is null ? (text.Length == 0 ? string.Empty : "![" + text + "]") : "![" + text + "](" + mark + ")";
    }
}
