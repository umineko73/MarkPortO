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
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Makes a copy of a presentation in which hidden slides are shown, so an engine that skips hidden slides
/// prints them too (FR-010a). The original is never changed.
/// </summary>
public static class PptxTempCopy
{
    public static void CreateWithHiddenSlidesShown(string sourcePath, string copyPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(copyPath))!);
        File.Copy(sourcePath, copyPath, overwrite: true);
        File.SetAttributes(copyPath, FileAttributes.Normal);

        using var document = PresentationDocument.Open(copyPath, isEditable: true);
        var presentationPart = document.PresentationPart ?? throw new InvalidDocumentException(Loc.T("The presentation has no main part."));
        foreach (var slidePart in presentationPart.SlideParts)
        {
            if (slidePart.Slide is { } slide && slide.Show is not null)
            {
                slide.Show = null;
                slide.Save();
            }
        }
    }
}
