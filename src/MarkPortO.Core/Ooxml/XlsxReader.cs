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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Ooxml;

/// <summary>The contents of one worksheet, limited to its used range.</summary>
public sealed class SheetContent
{
    public required string Name { get; init; }

    public bool Hidden { get; init; }

    /// <summary>1-based first and last column and row of the used range (cells with a value or a format).</summary>
    public int FirstColumn { get; init; }

    public int LastColumn { get; init; }

    public int FirstRow { get; init; }

    public int LastRow { get; init; }

    /// <summary>Cell texts of the used range: <c>Rows[r][c]</c> is row <c>FirstRow + r</c>, column <c>FirstColumn + c</c>.</summary>
    public required string[][] Rows { get; init; }

    /// <summary>The cells (0-based row and column in <see cref="Rows"/>) hidden by a merged cell: written as a mark in Markdown (FR-059).</summary>
    public IReadOnlySet<(int Row, int Column)> HiddenByMerge { get; init; } = new HashSet<(int Row, int Column)>();

    /// <summary>The first row is the header row of an Excel table (FR-060). A sheet has no header otherwise.</summary>
    public bool HasHeaderRow { get; init; }

    /// <summary>Width in points of each column of the used range (0 for a hidden column). Index 0 is <see cref="FirstColumn"/>.</summary>
    public required double[] ColumnWidthsPoints { get; init; }

    public bool IsEmpty => Rows.Length == 0;

    public int ColumnCount => LastColumn - FirstColumn + 1;

    public double TotalWidthPoints => ColumnWidthsPoints.Sum();

    /// <summary>For example <c>A:P</c> (column letters of used-range columns <paramref name="fromIndex"/> to <paramref name="toIndex"/>, 0-based).</summary>
    public string ColumnRange(int fromIndex, int toIndex) =>
        $"{ExcelColumn.Name(FirstColumn + fromIndex)}:{ExcelColumn.Name(FirstColumn + toIndex)}";

    /// <summary>The cells of the given used-range columns (0-based, inclusive) as a Markdown table.</summary>
    public string ToMarkdown(int fromIndex, int toIndex)
    {
        var rows = Rows
            .Select((row, r) => (IReadOnlyList<string>)row
                .Select((text, c) => HiddenByMerge.Contains((r, c)) ? MarkdownText.MergedCell : text)
                .Skip(fromIndex).Take(toIndex - fromIndex + 1).ToArray())
            .ToList();
        return MarkdownText.Table(rows, HasHeaderRow);
    }

    public string ToMarkdown() => ToMarkdown(0, Math.Max(0, ColumnCount - 1));
}

/// <summary>Reads the worksheets of an .xlsx file (research R2, R4).</summary>
public static partial class XlsxReader
{
    /// <summary>Default column width of Excel (8.43 characters = 64 pixels = 48 points).</summary>
    private const double DefaultColumnWidthPoints = 48;

    private const double MaxDigitWidthPixels = 7;

    /// <param name="path">The workbook.</param>
    /// <param name="includeHidden">Include hidden sheets (FR-010a).</param>
    public static IReadOnlyList<SheetContent> Read(string path, bool includeHidden)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Xlsx);
        try
        {
            using var document = SpreadsheetDocument.Open(path, isEditable: false);
            var workbookPart = document.WorkbookPart ?? throw new InvalidDocumentException(Loc.T("The workbook has no main part."));
            var sheets = workbookPart.Workbook?.Sheets ?? throw new InvalidDocumentException(Loc.T("The workbook has no sheets."));

            var sharedStrings = ReadSharedStrings(workbookPart);
            var dateStyles = ReadDateStyles(workbookPart);

            var result = new List<SheetContent>();
            foreach (var sheet in sheets.Elements<Sheet>())
            {
                var hidden = sheet.State?.Value is { } state && state != SheetStateValues.Visible;
                if (hidden && !includeHidden)
                {
                    continue;
                }

                if (workbookPart.GetPartById(sheet.Id!.Value!) is not WorksheetPart worksheetPart)
                {
                    continue; // a chart sheet
                }

                result.Add(ReadSheet(sheet.Name?.Value ?? "Sheet", hidden, worksheetPart, sharedStrings, dateStyles));
            }

            return result;
        }
        catch (Exception ex) when (ex is DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidDocumentException(Loc.T("The workbook could not be read."), ex);
        }
    }

    /// <summary>The names of the worksheets and whether each is hidden, in workbook order (chart sheets are skipped).</summary>
    public static IReadOnlyList<(string Name, bool Hidden)> ReadSheetNames(string path)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Xlsx);
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDocumentException(Loc.T("The workbook has no main part."));
        return
        [
            .. (workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
                .Where(s => workbookPart.GetPartById(s.Id!.Value!) is WorksheetPart)
                .Select(s => (s.Name?.Value ?? "Sheet", s.State?.Value is { } st && st != SheetStateValues.Visible)),
        ];
    }

    private static SheetContent ReadSheet(
        string name,
        bool hidden,
        WorksheetPart part,
        IReadOnlyList<string> sharedStrings,
        IReadOnlySet<uint> dateStyles)
    {
        var worksheet = part.Worksheet ?? throw new InvalidDocumentException(Loc.T("The sheet '{0}' has no content.", name));
        var cells = new Dictionary<(int Row, int Column), string>();
        int minRow = int.MaxValue, maxRow = 0, minColumn = int.MaxValue, maxColumn = 0;

        void Extend(int row, int column)
        {
            minRow = Math.Min(minRow, row);
            maxRow = Math.Max(maxRow, row);
            minColumn = Math.Min(minColumn, column);
            maxColumn = Math.Max(maxColumn, column);
        }

        foreach (var cell in worksheet.Descendants<Cell>())
        {
            if (cell.CellReference?.Value is not { } reference)
            {
                continue;
            }

            var (column, row) = ExcelColumn.ParseReference(reference);
            var text = CellText(cell, sharedStrings, dateStyles);
            var hasFormat = cell.StyleIndex?.Value is > 0;
            if (text.Length == 0 && !hasFormat)
            {
                continue; // an empty cell without a format is not part of the used range
            }

            Extend(row, column);
            if (text.Length > 0)
            {
                cells[(row, column)] = text;
            }
        }

        // A merged range extends the used range even if only its top-left cell holds a value.
        foreach (var merge in worksheet.Descendants<MergeCell>())
        {
            if (merge.Reference?.Value is { } range && range.Split(':') is { Length: 2 } ends)
            {
                var (c1, r1) = ExcelColumn.ParseReference(ends[0]);
                var (c2, r2) = ExcelColumn.ParseReference(ends[1]);
                if (cells.Keys.Any(k => k.Row >= r1 && k.Row <= r2 && k.Column >= c1 && k.Column <= c2))
                {
                    Extend(r1, c1);
                    Extend(r2, c2);
                }
            }
        }

        if (maxRow == 0)
        {
            return new SheetContent
            {
                Name = name,
                Hidden = hidden,
                Rows = [],
                ColumnWidthsPoints = [],
            };
        }

        // The cells under a merged range, except its top-left cell, inside the used range.
        var coveredByMerge = new HashSet<(int Row, int Column)>();
        foreach (var merge in worksheet.Descendants<MergeCell>())
        {
            if (merge.Reference?.Value is { } range && range.Split(':') is { Length: 2 } ends)
            {
                var (c1, r1) = ExcelColumn.ParseReference(ends[0]);
                var (c2, r2) = ExcelColumn.ParseReference(ends[1]);
                for (var r = Math.Max(r1, minRow); r <= Math.Min(r2, maxRow); r++)
                {
                    for (var c = Math.Max(c1, minColumn); c <= Math.Min(c2, maxColumn); c++)
                    {
                        if (r != r1 || c != c1)
                        {
                            coveredByMerge.Add((r - minRow, c - minColumn));
                        }
                    }
                }
            }
        }

        // The first row is a header when an Excel table starts there with a header row.
        var hasHeaderRow = part.TableDefinitionParts.Any(tablePart =>
            tablePart.Table is { } definition
            && definition.HeaderRowCount?.Value != 0
            && definition.Reference?.Value?.Split(':') is { Length: 2 } corners
            && ExcelColumn.ParseReference(corners[0]).Row == minRow);

        var rows = new string[maxRow - minRow + 1][];
        for (var r = minRow; r <= maxRow; r++)
        {
            var row = new string[maxColumn - minColumn + 1];
            for (var c = minColumn; c <= maxColumn; c++)
            {
                row[c - minColumn] = cells.TryGetValue((r, c), out var value) ? value : string.Empty;
            }

            rows[r - minRow] = row;
        }

        return new SheetContent
        {
            Name = name,
            Hidden = hidden,
            FirstColumn = minColumn,
            LastColumn = maxColumn,
            FirstRow = minRow,
            LastRow = maxRow,
            Rows = rows,
            HiddenByMerge = coveredByMerge,
            HasHeaderRow = hasHeaderRow,
            ColumnWidthsPoints = ColumnWidths(worksheet, minColumn, maxColumn),
        };
    }

    private static double[] ColumnWidths(Worksheet worksheet, int firstColumn, int lastColumn)
    {
        var defaultWidth = worksheet.SheetFormatProperties?.DefaultColumnWidth?.Value is { } d
            ? CharactersToPoints(d)
            : DefaultColumnWidthPoints;

        var widths = Enumerable.Repeat(defaultWidth, lastColumn - firstColumn + 1).ToArray();
        foreach (var column in worksheet.Descendants<Column>())
        {
            var min = (int)(column.Min?.Value ?? 0);
            var max = (int)(column.Max?.Value ?? 0);
            if (min == 0 || max == 0)
            {
                continue;
            }

            var points = column.Hidden?.Value == true
                ? 0
                : column.Width?.Value is { } w ? CharactersToPoints(w) : defaultWidth;
            for (var c = Math.Max(min, firstColumn); c <= Math.Min(max, lastColumn); c++)
            {
                widths[c - firstColumn] = points;
            }
        }

        return widths;
    }

    /// <summary>Excel's width (in characters of the default font) to points. 7 pixels per character, 0.75 points per pixel.</summary>
    private static double CharactersToPoints(double characters)
    {
        var pixels = Math.Truncate(((256 * characters) + Math.Truncate(128 / MaxDigitWidthPixels)) / 256 * MaxDigitWidthPixels);
        return pixels * 0.75;
    }

    private static string CellText(Cell cell, IReadOnlyList<string> sharedStrings, IReadOnlySet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;

        if (type == CellValues.InlineString)
        {
            return InlineText(cell.InlineString);
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        if (type == CellValues.String || type == CellValues.Error)
        {
            return raw;
        }

        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            return FormatDate(serial) ?? raw;
        }

        return raw;
    }

    private static string? FormatDate(double serial)
    {
        try
        {
            var value = DateTime.FromOADate(serial);
            if (serial < 1)
            {
                return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return serial % 1 == 0
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null; // not a valid date serial
        }
    }

    private static string InlineText(InlineString? inline) =>
        inline is null ? string.Empty : string.Concat(inline.Descendants<Text>().Select(t => t.Text));

    private static IReadOnlyList<string> ReadSharedStrings(WorkbookPart workbookPart)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null)
        {
            return [];
        }

        var strings = new List<string>();
        foreach (var item in table.Elements<SharedStringItem>())
        {
            var builder = new StringBuilder();
            foreach (var text in item.Descendants<Text>().Where(t => !t.Ancestors<PhoneticRun>().Any()))
            {
                builder.Append(text.Text);
            }

            strings.Add(builder.ToString());
        }

        return strings;
    }

    /// <summary>Style indexes (positions in cellXfs) whose number format shows a date or a time.</summary>
    private static IReadOnlySet<uint> ReadDateStyles(WorkbookPart workbookPart)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var formats = stylesheet?.CellFormats;
        if (formats is null)
        {
            return result;
        }

        var custom = (stylesheet!.NumberingFormats?.Elements<NumberingFormat>() ?? [])
            .Where(f => f.NumberFormatId is not null && f.FormatCode?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode!.Value!);

        uint index = 0;
        foreach (var format in formats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            var isDate = custom.TryGetValue(id, out var code) ? LooksLikeDateFormat(code) : IsBuiltInDateFormat(id);
            if (isDate)
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    private static bool IsBuiltInDateFormat(uint id) =>
        id is (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58);

    private static bool LooksLikeDateFormat(string code)
    {
        // Remove quoted text and bracketed parts such as [Red] or [$-409]; what is left decides.
        var stripped = QuotedOrBracketed().Replace(code, string.Empty);
        return stripped.IndexOfAny(['y', 'Y', 'm', 'M', 'd', 'D', 'h', 'H', 's', 'S']) >= 0;
    }

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.")]
    private static partial Regex QuotedOrBracketed();
}
