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
    /// is not kept (a deleted page) is not saved. Returns the lines for the log of the document.
    /// </summary>
    public static IReadOnlyList<string> Write(string outputFolder, IReadOnlyList<Block> blocks, int pageCount, PictureCollector collector)
    {
        var log = new List<string>();
        var effective = DocxPageAssigner.EffectivePages(blocks, pageCount);
        var onPage = new Dictionary<int, int>();
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var byMark = new Dictionary<int, string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Markdown.IndexOf(PictureCollector.MarkStart, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            var page = effective[i];
            block.Markdown = Mark().Replace(block.Markdown, match =>
            {
                var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (number >= collector.Pictures.Count)
                {
                    return string.Empty;
                }

                if (byMark.TryGetValue(number, out var known))
                {
                    return known;
                }

                var picture = collector.Pictures[number];
                var hash = System.Convert.ToHexString(SHA256.HashData(picture.Data));
                if (!byHash.TryGetValue(hash, out var name))
                {
                    var count = onPage[page] = onPage.GetValueOrDefault(page) + 1;
                    name = MarkdownRenderer.PictureFileName(page, pageCount, count, picture.Extension);
                    var path = Path.Combine(outputFolder, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    AtomicFile.WriteAllBytes(path, picture.Data);
                    byHash[hash] = name;
                    log.Add($"picture: {name} ({picture.Data.Length.ToString(CultureInfo.InvariantCulture)} bytes)");
                }

                byMark[number] = name;
                return name;
            });
        }

        return log;
    }

    /// <summary>
    /// The text without the files of the pictures, for the text of the page that is sent to the VLM: <c>![text](pictures/…)</c> is <c>![text]</c>,
    /// and a picture without a text is left out.
    /// </summary>
    public static string WithoutFiles(string markdown) =>
        markdown.Contains("](" + Folder + "/", StringComparison.Ordinal)
            ? Reference().Replace(markdown, match => match.Groups[1].Value.Length == 0 ? string.Empty : "![" + match.Groups[1].Value + "]")
            : markdown;

    [GeneratedRegex("(\\d+)")]
    private static partial Regex Mark();

    [GeneratedRegex(@"!\[((?:\\.|[^\]\\])*)\]\(pictures/[^)\s]*\)")]
    private static partial Regex Reference();
}
