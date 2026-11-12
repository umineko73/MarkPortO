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

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// The files of the pictures of a document (FR-133): <c>pictures/page-003-1.png</c>, the first picture on page 3. The same picture (the same bytes)
/// is saved once, under the name of the page where it is first seen.
/// </summary>
public static partial class PictureFiles
{
    /// <summary>The folder of the pictures in the document folder.</summary>
    public const string Folder = "pictures";

    /// <summary>Removes the pictures of an earlier conversion into the same folder, so that no file is left that the Markdown does not refer to.</summary>
    public static void Clear(string outputFolder)
    {
        var folder = Path.Combine(outputFolder, Folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Puts the names of the files in place of the marks of the pictures in <paramref name="blocks"/>, and saves the files. A picture whose block
    /// is not kept (a deleted page) is not saved. A drawn shape (FR-136) is cut out of the image of its page, found by <paramref name="pageImage"/>;
    /// one that cannot be is written by its text only. Returns the lines for the log of the document.
    /// </summary>
    public static IReadOnlyList<string> Write(string outputFolder, IReadOnlyList<Block> blocks, int pageCount, PictureCollector collector, Func<int, string?>? pageImage = null)
    {
        var log = new List<string>();
        var effective = DocxPageAssigner.EffectivePages(blocks, pageCount);
        var onPage = new Dictionary<int, int>();
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var byMark = new Dictionary<int, string?>();
        var images = new Dictionary<int, Bitmap?>();
        try
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.Markdown.IndexOf(PictureCollector.MarkStart, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var page = effective[i];
                block.Markdown = MarkedReference.Replace(block.Markdown, match =>
                {
                    var text = match.Groups[1].Value;
                    var name = NameOf(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), page);
                    return name is not null ? "![" + text + "](" + name + ")" : text.Length == 0 ? string.Empty : "![" + text + "]";
                });
            }
        }
        finally
        {
            foreach (var image in images.Values)
            {
                image?.Dispose();
            }
        }

        return log;

        string? NameOf(int number, int page)
        {
            if (number >= collector.Pictures.Count)
            {
                return null;
            }

            if (byMark.TryGetValue(number, out var known))
            {
                return known;
            }

            var picture = collector.Pictures[number];
            if (!byHash.TryGetValue(picture.Hash, out var name))
            {
                var data = picture.IsShape ? Cut(picture.Area, page) : picture.Data;
                if (data is null)
                {
                    byMark[number] = null;
                    return null;
                }

                var count = onPage[page] = onPage.GetValueOrDefault(page) + 1;
                name = MarkdownRenderer.PictureFileName(page, pageCount, count, picture.Extension);
                var path = Path.Combine(outputFolder, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllBytes(path, data);
                byHash[picture.Hash] = name;
                log.Add($"picture: {name} ({data.Length.ToString(CultureInfo.InvariantCulture)} bytes"
                    + (picture.IsShape ? ", a drawn shape cut out of the page image)" : picture.ConvertedFrom is null ? ")" : $", turned into a PNG from {picture.ConvertedFrom})"));
            }

            byMark[number] = name;
            return name;
        }

        // The PNG of the part of the page image where a drawn shape is (FR-136); null when it cannot be cut out.
        byte[]? Cut(ShapeArea? area, int blockPage)
        {
            if (area is null)
            {
                log.Add("a drawn shape is not cut out: its place on the page is not known (a sheet that follows its own print setup)");
                return null;
            }

            var page = area.Page ?? blockPage;
            if (!images.TryGetValue(page, out var image))
            {
                try
                {
                    image = pageImage?.Invoke(page) is { } file && File.Exists(file) ? new Bitmap(file) : null;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
                {
                    image = null;
                }

                images[page] = image;
            }

            if (image is null)
            {
                log.Add($"a drawn shape is not cut out: page {page.ToString(CultureInfo.InvariantCulture)} has no image");
                return null;
            }

            var left = (int)Math.Floor(area.Left * image.Width);
            var top = (int)Math.Floor(area.Top * image.Height);
            var right = Math.Min(image.Width, (int)Math.Ceiling((area.Left + area.Width) * image.Width));
            var bottom = Math.Min(image.Height, (int)Math.Ceiling((area.Top + area.Height) * image.Height));
            if (right - left < 2 || bottom - top < 2)
            {
                return null;
            }

            using var piece = image.Clone(new Rectangle(left, top, right - left, bottom - top), image.PixelFormat);
            using var stream = new MemoryStream();
            piece.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// The text without the files of the pictures, for the text of the page that is sent to the VLM: <c>![text](pictures/…)</c> is <c>![text]</c>,
    /// and a picture without a text is left out.
    /// </summary>
    public static string WithoutFiles(string markdown) =>
        markdown.Contains("](" + Folder + "/", StringComparison.Ordinal)
            ? Reference().Replace(markdown, match => match.Groups[1].Value.Length == 0 ? string.Empty : "![" + match.Groups[1].Value + "]")
            : markdown;

    [GeneratedRegex(@"!\[((?:\\.|[^\]\\])*)\]\(pictures/[^)\s]*\)")]
    private static partial Regex Reference();

    // A picture with a mark: ![text](mark). The marks are written only by PictureCollector.Markdown, always in this form.
    private static readonly Regex MarkedReference = new(
        @"!\[((?:\\.|[^\]\\])*)\]\(" + PictureCollector.MarkStart + @"(\d+)" + PictureCollector.MarkEnd + @"\)", RegexOptions.CultureInvariant);
}
