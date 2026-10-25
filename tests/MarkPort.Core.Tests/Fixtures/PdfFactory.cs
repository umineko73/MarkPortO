// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>Builds small PDFs for the renderer tests (Latin text only: PDF standard fonts).</summary>
public static class PdfFactory
{
    public static string Create(string path, PageSize size, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(size);
            page.AddText(text, 14, new PdfPoint(50, 700), font);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>Reads width and height from the IHDR chunk of a PNG.</summary>
    public static (int Width, int Height) ReadPngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            throw new InvalidDataException($"'{path}' is not a PNG file.");
        }

        static int ReadInt(byte[] b, int offset) =>
            (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

        return (ReadInt(bytes, 16), ReadInt(bytes, 20));
    }
}
