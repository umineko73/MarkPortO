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

using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Finds out whether a docx has fields that count (<c>SEQ</c>: the numbers of figures and tables) or take a number from a heading
/// (<c>STYLEREF</c>). Word keeps only the result of the last count as text in the file, and it can be old (a figure was deleted and the
/// fields were not updated); Word counts again when it prints or exports a PDF, so the page images and the text can disagree.
/// </summary>
public static partial class DocxFieldProbe
{
    [GeneratedRegex(@"<w:instrText[^>]*>\s*(?:SEQ|STYLEREF)\b|w:instr=""\s*(?:SEQ|STYLEREF)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountingField();

    public static bool HasCountingFields(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml");
            if (entry is null)
            {
                return false;
            }

            using var reader = new StreamReader(entry.Open());
            return CountingField().IsMatch(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false; // a file that cannot be read here is reported by the reader that opens it
        }
    }
}
