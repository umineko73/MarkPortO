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
using System.Text;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// The log of the conversion of one document: <c>name.log</c> beside <c>name.md</c> in its output folder. It says what was done to each page
/// (the size and the image, or why there is no image, a deleted or a blank page) and the warnings and failures, so that a page that came out
/// wrong can be explained later. Lines are added, never rewritten: a resumed run goes on in the same file. Writing it never stops a conversion.
/// </summary>
public static class DocumentLog
{
    /// <summary>The name of the log of a document, for example <c>report.log</c>.</summary>
    public static string FileName(string sourcePath) =>
        Jobs.OutputNamer.Sanitize(Path.GetFileNameWithoutExtension(sourcePath)) + ".log";

    /// <summary>Adds lines to the log of the document, each with the local time.</summary>
    public static void Write(DocumentRecord document, params string[] lines)
    {
        if (string.IsNullOrEmpty(document.OutputFolder) || lines.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(document.OutputFolder);
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var text = new StringBuilder();
            foreach (var line in lines)
            {
                text.Append(time).Append("  ").Append(line.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append("\r\n");
            }

            File.AppendAllText(Path.Combine(document.OutputFolder, FileName(document.SourcePath)), text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a help; a folder that cannot be written to is reported by the conversion itself.
        }
    }

    /// <summary>The size of a page in millimetres, from its image and the DPI of the image.</summary>
    public static string PageSize(int widthPixels, int heightPixels, int dpi) =>
        dpi <= 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{widthPixels * 25.4 / dpi:0} x {heightPixels * 25.4 / dpi:0} mm");
}
