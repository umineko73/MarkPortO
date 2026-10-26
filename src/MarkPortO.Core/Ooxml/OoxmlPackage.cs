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

using MarkPortO.Core.Localization;
using System.IO.Packaging;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Ooxml;

/// <summary>The file cannot be read as the document it claims to be (damaged, or not an Office file at all).</summary>
public sealed class InvalidDocumentException : Exception
{
    public InvalidDocumentException(string message)
        : base(message)
    {
    }

    public InvalidDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class OoxmlPackage
{
    /// <summary>The kind of document for an extension (<c>.docx</c>), or <c>null</c> if it is not one we convert.</summary>
    public static DocumentKind? KindOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => DocumentKind.Docx,
            ".xlsx" => DocumentKind.Xlsx,
            ".pptx" => DocumentKind.Pptx,
            _ => null,
        };

    /// <summary>
    /// Checks that the file opens as an Office Open XML package with the main part of its kind.
    /// This is done before an engine is used, because LibreOffice converts a damaged file as plain text
    /// and reports success (research: LibreOffice finding).
    /// </summary>
    /// <exception cref="InvalidDocumentException">The file is not a usable document.</exception>
    public static void EnsureOpens(string path, DocumentKind kind)
    {
        try
        {
            using var package = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var expectedPart = kind switch
            {
                DocumentKind.Docx => "/word/document.xml",
                DocumentKind.Xlsx => "/xl/workbook.xml",
                DocumentKind.Pptx => "/ppt/presentation.xml",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            if (!package.PartExists(new Uri(expectedPart, UriKind.Relative)))
            {
                throw new InvalidDocumentException(
                    Loc.T("The file is not a valid .{0} document (missing {1}).", kind.ToString().ToLowerInvariant(), expectedPart));
            }
        }
        catch (InvalidDocumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileFormatException or IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
        {
            // An encrypted (password protected) document is an OLE file, not a zip package.
            var message = LooksEncrypted(path)
                ? Loc.T("The file cannot be opened as a .{0} document. It looks like a password-protected document.", kind.ToString().ToLowerInvariant())
                : Loc.T("The file cannot be opened as a .{0} document.", kind.ToString().ToLowerInvariant());
            throw new InvalidDocumentException(message, ex);
        }
    }

    private static bool LooksEncrypted(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var read = stream.Read(header);
            // Compound File Binary signature (used by encrypted Office files and the old .doc/.xls/.ppt formats).
            ReadOnlySpan<byte> cfb = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
            return read == 8 && header.SequenceEqual(cfb);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
