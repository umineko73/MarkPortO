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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Security.Cryptography;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// Makes the picture for the image check: four random letters or digits on white. If the VLM can read them back,
/// it really sees the picture (a model that ignores the image cannot guess a fresh code).
/// </summary>
public static class TestImageFactory
{
    /// <summary>Letters and digits that are not easily mistaken for each other (no 0/O, 1/I).</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int CodeLength = 4;

    public static string RandomCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>A PNG of about 256 x 96 pixels showing <paramref name="code"/>.</summary>
    public static byte[] Create(string code)
    {
        using var bitmap = new Bitmap(256, 96);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Arial", 44, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Black);
            var size = graphics.MeasureString(code, font);
            graphics.DrawString(code, font, brush, (bitmap.Width - size.Width) / 2, (bitmap.Height - size.Height) / 2);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
