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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Makes a copy of a workbook that prints exactly one part of one sheet on a page width, whichever engine
/// prints it (research R4). The original is never changed: the copy is made first and only the copy is edited.
/// </summary>
public static class ExcelTempCopy
{
    /// <summary>The paper size code of A3 in the OOXML page setup.</summary>
    private const uint PaperSizeA3 = 8;

    /// <summary>Page margin in inches.</summary>
    private const double MarginInches = 0.25;

    /// <param name="sourcePath">The workbook (read only).</param>
    /// <param name="copyPath">Where the copy is written.</param>
    /// <param name="sheet">The sheet to print.</param>
    /// <param name="unit">The columns of the sheet to print.</param>
    public static void Create(string sourcePath, string copyPath, SheetContent sheet, ExcelUnit unit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(copyPath))!);
        File.Copy(sourcePath, copyPath, overwrite: true);
        File.SetAttributes(copyPath, FileAttributes.Normal);

        using var document = SpreadsheetDocument.Open(copyPath, isEditable: true);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDocumentException(Loc.T("The workbook has no main part."));
        var workbook = workbookPart.Workbook ?? throw new InvalidDocumentException(Loc.T("The workbook has no content."));
        var sheets = workbook.Sheets ?? throw new InvalidDocumentException(Loc.T("The workbook has no sheets."));

        // Only the target sheet is visible, so only it is printed. A hidden target is made visible in the copy.
        var targetIndex = -1;
        var all = sheets.Elements<Sheet>().ToList();
        for (var i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i].Name?.Value, sheet.Name, StringComparison.Ordinal))
            {
                targetIndex = i;
                all[i].State = null; // visible
            }
            else
            {
                all[i].State = SheetStateValues.Hidden;
            }
        }

        if (targetIndex < 0)
        {
            throw new InvalidDocumentException(Loc.T("The sheet '{0}' was not found.", sheet.Name));
        }

        // LibreOffice prints the FIRST sheet even when it is hidden (found by the xlsx end-to-end test), so the
        // target sheet is moved to the front. Names that belong to one sheet are re-pointed to the new positions.
        var target = all[targetIndex];
        var newPosition = new Dictionary<uint, uint> { [(uint)targetIndex] = 0 };
        uint next = 1;
        for (var i = 0; i < all.Count; i++)
        {
            if (i != targetIndex)
            {
                newPosition[(uint)i] = next++;
            }
        }

        foreach (var s in all)
        {
            s.Remove();
        }

        sheets.AppendChild(target);
        foreach (var other in all.Where(s => !ReferenceEquals(s, target)))
        {
            sheets.AppendChild(other);
        }

        foreach (var name in workbook.DefinedNames?.Elements<DefinedName>() ?? [])
        {
            if (name.LocalSheetId?.Value is { } old && newPosition.TryGetValue(old, out var moved))
            {
                name.LocalSheetId = moved;
            }
        }

        targetIndex = 0;
        var view = workbook.BookViews?.Elements<WorkbookView>().FirstOrDefault();
        if (view is not null)
        {
            view.ActiveTab = 0;
            view.FirstSheet = 0;
        }

        SetPrintArea(workbook, sheet, unit, targetIndex);

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(target.Id!.Value!);
        FitToOnePageWide(worksheetPart.Worksheet ?? throw new InvalidDocumentException(Loc.T("The sheet has no content.")));

        workbook.Save();
        worksheetPart.Worksheet.Save();
    }

    private static void SetPrintArea(Workbook workbook, SheetContent sheet, ExcelUnit unit, int sheetIndex)
    {
        var names = workbook.DefinedNames;
        if (names is null)
        {
            names = new DefinedNames();
            workbook.DefinedNames = names;
        }

        // Replace the existing print area (ignored: FR-010b) and the repeated title rows (they would change the layout).
        // The print areas of the OTHER sheets are removed too: LibreOffice prints a hidden sheet that has a print
        // area, which would put pages of other sheets into this sheet's PDF (found by the xlsx end-to-end test).
        foreach (var existing in names.Elements<DefinedName>().ToList())
        {
            if (existing.Name?.Value is "_xlnm.Print_Area" or "_xlnm.Print_Titles")
            {
                existing.Remove();
            }
        }

        var firstColumn = sheet.FirstColumn + unit.FromColumnIndex;
        var lastColumn = sheet.FirstColumn + unit.ToColumnIndex;
        var range = $"${ExcelColumn.Name(firstColumn)}${sheet.FirstRow}:${ExcelColumn.Name(lastColumn)}${sheet.LastRow}";
        names.AppendChild(new DefinedName($"'{sheet.Name.Replace("'", "''", StringComparison.Ordinal)}'!{range}")
        {
            Name = "_xlnm.Print_Area",
            LocalSheetId = (uint)sheetIndex,
        });
    }

    private static void FitToOnePageWide(Worksheet worksheet)
    {
        // "Fit to page" with one page wide and any number of pages tall; the page is only shrunk, never enlarged.
        var properties = worksheet.SheetProperties ??= new SheetProperties();
        properties.PageSetupProperties = new PageSetupProperties { FitToPage = true };

        // Manual page breaks and headers/footers would change what is on each image.
        worksheet.RemoveAllChildren<RowBreaks>();
        worksheet.RemoveAllChildren<ColumnBreaks>();
        worksheet.RemoveAllChildren<HeaderFooter>();
        worksheet.RemoveAllChildren<PageMargins>();
        worksheet.RemoveAllChildren<PageSetup>();

        worksheet.AddChild(new PageMargins
        {
            Left = MarginInches,
            Right = MarginInches,
            Top = MarginInches,
            Bottom = MarginInches,
            Header = 0.1,
            Footer = 0.1,
        });
        worksheet.AddChild(new PageSetup
        {
            PaperSize = PaperSizeA3,
            Orientation = OrientationValues.Landscape,
            FitToWidth = 1,
            FitToHeight = 0,
        });
    }
}
