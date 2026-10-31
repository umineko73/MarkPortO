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
/// <summary>A cell hidden by a merged range: it is in the first row of the range, and where the top-left cell of the range is (0-based in <see cref="SheetContent.Rows"/>).</summary>
public readonly record struct MergeMark(bool FirstRow, int OriginRow, int OriginColumn);


/// <summary>An Excel table (<c>xl/tables</c>) of a sheet: its name, and its cells (0-based in <see cref="SheetContent.Rows"/>).</summary>
/// <summary>A shared string of a workbook: its text, and for rich text its parts, each with whether it has a strikethrough (null: as the font of the cell).</summary>
internal sealed record SharedItem(string Plain, List<(string Text, bool? Struck, bool Bold, bool Italic)>? Runs)
{
    /// <summary>
    /// The text for a cell whose font has (or has not) a strikethrough; a part with a strikethrough is written between ~~ (FR-092). With
    /// <paramref name="marking"/>, a part in bold is written between ** and one in italic between * (FR-101), unless all the parts are.
    /// </summary>
    public string Text(bool cellStruck, bool marking = false)
    {
        if (Runs is null)
        {
            return cellStruck && Plain.Length > 0 ? Strikethrough.Wrap(Plain) : Plain;
        }

        var filled = Runs.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        var boldAllowed = marking && filled.Count > 0 && !filled.All(r => r.Bold);
        var italicAllowed = marking && filled.Count > 0 && !filled.All(r => r.Italic);
        var builder = new StringBuilder();
        foreach (var (text, struck, bold, italic) in Runs)
        {
            var piece = text;
            if (italicAllowed && italic)
            {
                piece = TextEmphasis.Italic(piece);
            }

            if (boldAllowed && bold)
            {
                piece = TextEmphasis.Bold(piece);
            }

            builder.Append((struck ?? cellStruck) ? Strikethrough.Wrap(piece) : piece);
        }

        return TextEmphasis.Merge(Strikethrough.Merge(builder.ToString()));
    }
}

/// <summary>A piece of the Markdown of a sheet: a table (or a part of one that runs over pages), or the comments and the charts.</summary>
/// <param name="Page">The page of the printed sheet (0-based) that the piece is on.</param>
/// <param name="HeadPage">For a table: the page where the table starts (the page of its first piece).</param>
public sealed record SheetPart(int Page, string Markdown, bool IsTable, int HeadPage);

public sealed record SheetTable(string Name, int FirstRow, int LastRow, int FirstColumn, int LastColumn, bool HasHeader);

/// <summary>A comment (note) of a cell (0-based in <see cref="SheetContent.Rows"/>).</summary>
public sealed record SheetNote(int Row, int Column, string Text);

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
    public IReadOnlyDictionary<(int Row, int Column), MergeMark> HiddenByMerge { get; init; } = new Dictionary<(int Row, int Column), MergeMark>();

    /// <summary>The Excel tables of the sheet (FR-070). When they are stacked and cover the content beside them, each is written as a table of its own.</summary>
    public IReadOnlyList<SheetTable> Tables { get; init; } = [];

    /// <summary>The charts of the sheet (FR-070): written after the tables, in the first part of a sheet that is cut into parts.</summary>
    public IReadOnlyList<ChartInfo> Charts { get; init; } = [];

    /// <summary>The comments of the cells (FR-070): written after the tables.</summary>
    public IReadOnlyList<SheetNote> Notes { get; init; } = [];

    /// <summary>The rows (0-based in <see cref="Rows"/>) that are hidden: they are not in the image, so they are not in the Markdown either (FR-070).</summary>
    public IReadOnlySet<int> HiddenRows { get; init; } = new HashSet<int>();

    /// <summary>The columns (0-based in <see cref="Rows"/>) that are hidden (FR-070).</summary>
    public IReadOnlySet<int> HiddenColumns { get; init; } = new HashSet<int>();

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
    public string ToMarkdown(int fromIndex, int toIndex) =>
        string.Join("\n\n", ToMarkdownParts(fromIndex, toIndex, null).Select(part => part.Markdown).Where(markdown => markdown.Length > 0));

    /// <summary>
    /// The same Markdown as pieces: a table that runs over several pages of the printed sheet is cut at the page breaks, one table for each page
    /// (FR-090). <paramref name="rowPages"/> is the page (0-based) of each row of the sheet; null puts everything on page 0.
    /// </summary>
    public IReadOnlyList<SheetPart> ToMarkdownParts(int fromIndex, int toIndex, IReadOnlyList<int>? rowPages)
    {
        var parts = new List<SheetPart>();

        // One table of the sheet, as one piece for each page that its rows are on.
        void AddTable(int firstRow, int lastRow, int from, int to, bool hasHeader, bool skipEmptyRows, string? heading)
        {
            var groups = new List<(int First, int Last, int Page)>();
            var previous = 0;
            for (var r = firstRow; r <= lastRow && r < Rows.Length; r++)
            {
                var page = rowPages is null ? 0 : Math.Max(previous, r < rowPages.Count ? rowPages[r] : previous);
                previous = page;
                if (groups.Count > 0 && groups[^1].Page == page)
                {
                    groups[^1] = (groups[^1].First, r, page);
                }
                else
                {
                    groups.Add((r, r, page));
                }
            }

            var started = false;
            var headPage = groups.Count > 0 ? groups[0].Page : 0;
            var folded = FoldedColumns(firstRow, lastRow, from, to, skipEmptyRows); // the same columns on every page of the table
            foreach (var (first, last, page) in groups)
            {
                var markdown = Block(first, last, from, to, hasHeader: !started && hasHeader, skipEmptyRows, folded);
                if (markdown.Length == 0)
                {
                    continue;
                }

                parts.Add(new SheetPart(page, !started && heading is not null ? heading + "\n\n" + markdown : markdown, IsTable: true, headPage));
                started = true;
            }
        }

        if (StackedBlocks() is { } blocks)
        {
            foreach (var (first, last, table) in blocks)
            {
                if (table is null)
                {
                    // The rows between the tables, without the empty rows at their top and bottom.
                    var filled = Enumerable.Range(first, last - first + 1).Where(r => !HiddenRows.Contains(r) && HasContent(r, fromIndex, toIndex)).ToList();
                    if (filled.Count > 0)
                    {
                        AddTable(filled[0], filled[^1], fromIndex, toIndex, hasHeader: false, skipEmptyRows: false, heading: null);
                    }

                    continue;
                }

                var from = Math.Max(fromIndex, table.FirstColumn);
                var to = Math.Min(toIndex, table.LastColumn);
                if (from <= to)
                {
                    AddTable(table.FirstRow, table.LastRow, from, to, table.HasHeader, skipEmptyRows: false, heading: "### " + MarkdownText.EscapeCell(table.Name));
                }
            }
        }
        else if (!HasHeaderRow && Regions(0, Rows.Length - 1, fromIndex, toIndex) is { Count: > 1 } regions)
        {
            // Tables drawn on the sheet without the Excel table feature: each is written as a table of its own.
            foreach (var r in JoinThinBlocks(regions))
            {
                AddTable(r.FirstRow, r.LastRow, r.From, r.To, hasHeader: false, skipEmptyRows: r.Joined, heading: null);
            }
        }
        else
        {
            AddTable(0, Rows.Length - 1, fromIndex, toIndex, HasHeaderRow, skipEmptyRows: false, heading: null);
        }

        var notes = Notes
            .Where(n => n.Column >= fromIndex && n.Column <= toIndex && !HiddenRows.Contains(n.Row) && !HiddenColumns.Contains(n.Column))
            .Select(n => "> **" + ExcelColumn.Name(FirstColumn + n.Column) + (FirstRow + n.Row).ToString(CultureInfo.InvariantCulture) + "**: " + MarkdownText.EscapeCell(n.Text))
            .ToList();
        if (notes.Count > 0)
        {
            parts.Add(new SheetPart(0, string.Join("\n>\n", notes), IsTable: false, 0));
        }

        if (fromIndex == 0)
        {
            parts.AddRange(Charts.Select(chart => new SheetPart(0, chart.ToMarkdown(), IsTable: false, 0)));
        }

        return parts;
    }

    private bool HasContent(int row, int fromIndex, int toIndex) =>
        Enumerable.Range(fromIndex, toIndex - fromIndex + 1).Any(c => c < Rows[row].Length && (Rows[row][c].Length > 0 || HiddenByMerge.ContainsKey((row, c))));

    private bool IsFilled(int row, int column) =>
        !HiddenRows.Contains(row) && !HiddenColumns.Contains(column)
        && column < Rows[row].Length && (Rows[row][column].Length > 0 || HiddenByMerge.ContainsKey((row, column)) || Overflow.Contains((row, column)));

    private HashSet<(int Row, int Column)>? overflow;

    // The empty cells that the text of the cell on their left runs over: on the sheet the text is drawn across them, so they do not cut a block.
    private HashSet<(int Row, int Column)> Overflow => overflow ??= BuildOverflow();

    private HashSet<(int Row, int Column)> BuildOverflow()
    {
        var covered = new HashSet<(int, int)>();
        for (var r = 0; r < Rows.Length; r++)
        {
            for (var c = 0; c < Rows[r].Length && c < ColumnWidthsPoints.Length; c++)
            {
                var text = Rows[r][c];
                if (text.Length == 0 || text.Contains('\n', StringComparison.Ordinal) || text.All(ch => "0123456789.,-+$%()¥ ".Contains(ch, StringComparison.Ordinal)))
                {
                    continue; // a number does not run over the cells beside it
                }

                var need = text.Sum(ch => ch < 256 ? 5.5 : 11.0);
                var have = ColumnWidthsPoints[c];
                for (var k = c + 1; have < need && k < Rows[r].Length && k < ColumnWidthsPoints.Length && Rows[r][k].Length == 0 && !HiddenByMerge.ContainsKey((r, k)); k++)
                {
                    covered.Add((r, k));
                    have += ColumnWidthsPoints[k];
                }
            }
        }

        return covered;
    }

    // The blocks of content, cut at the rows and then the columns that are empty all across (and again inside each block),
    // from the top and the left. A block is the smallest rectangle around its cells.
    private List<(int FirstRow, int LastRow, int From, int To)> Regions(int firstRow, int lastRow, int from, int to)
    {
        var result = new List<(int, int, int, int)>();
        Cut(firstRow, lastRow, from, to, byRows: true, otherDidNotSplit: false);
        return result;

        void Cut(int r0, int r1, int c0, int c1, bool byRows, bool otherDidNotSplit)
        {
            var spans = Spans(r0, r1, c0, c1, byRows);
            if (spans.Count == 1)
            {
                var (s, e) = spans[0];
                var (nr0, nr1, nc0, nc1) = byRows ? (s, e, c0, c1) : (r0, r1, s, e);
                if (otherDidNotSplit)
                {
                    result.Add((nr0, nr1, nc0, nc1));
                }
                else
                {
                    Cut(nr0, nr1, nc0, nc1, !byRows, otherDidNotSplit: true);
                }

                return;
            }

            foreach (var (s, e) in spans)
            {
                if (byRows)
                {
                    Cut(s, e, c0, c1, byRows: false, otherDidNotSplit: false);
                }
                else
                {
                    Cut(r0, r1, s, e, byRows: true, otherDidNotSplit: false);
                }
            }
        }
    }

    // Blocks of one row each, one below the other with their columns overlapping (the rows of a table that has an empty row between them),
    // are one table: a table of its own for every row only adds noise. Blocks of more than one row are kept as they are.
    private static List<(int FirstRow, int LastRow, int From, int To, bool Joined)> JoinThinBlocks(List<(int FirstRow, int LastRow, int From, int To)> regions)
    {
        var result = new List<(int FirstRow, int LastRow, int From, int To, bool Joined)>();
        foreach (var (first, last, from, to) in regions)
        {
            if (result.Count > 0 && first == last)
            {
                var previous = result[^1];
                var previousIsThin = previous.Joined || previous.FirstRow == previous.LastRow;
                if (previousIsThin && first > previous.LastRow && from <= previous.To && to >= previous.From)
                {
                    result[^1] = (previous.FirstRow, last, Math.Min(previous.From, from), Math.Max(previous.To, to), true);
                    continue;
                }
            }

            result.Add((first, last, from, to, false));
        }

        return result;
    }

    // The runs of rows (or columns) that are not empty all across, in the given block.
    private List<(int Start, int End)> Spans(int r0, int r1, int c0, int c1, bool byRows)
    {
        var spans = new List<(int, int)>();
        var start = -1;
        var (lo, hi) = byRows ? (r0, r1) : (c0, c1);
        for (var i = lo; i <= hi + 1; i++)
        {
            // A hidden row or column is not in the output: it neither joins nor cuts.
            if (i <= hi && (byRows ? HiddenRows.Contains(i) : HiddenColumns.Contains(i)))
            {
                continue;
            }

            var filled = i <= hi && (byRows
                ? Enumerable.Range(c0, c1 - c0 + 1).Any(c => r0 <= i && i < Rows.Length && IsFilled(i, c))
                : Enumerable.Range(r0, r1 - r0 + 1).Any(r => r < Rows.Length && IsFilled(r, i)));
            if (filled && start < 0)
            {
                start = i;
            }
            else if (!filled && start >= 0)
            {
                spans.Add((start, i - 1));
                start = -1;
            }
        }

        return spans;
    }

    // The tables and the rows between them, from the top, when the tables are one above the other and nothing is beside them; otherwise null.
    private List<(int First, int Last, SheetTable? Table)>? StackedBlocks()
    {
        if (Tables.Count == 0)
        {
            return null;
        }

        var ordered = Tables.OrderBy(t => t.FirstRow).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var table = ordered[i];
            if (i > 0 && table.FirstRow <= ordered[i - 1].LastRow)
            {
                return null; // two tables share a row
            }

            for (var r = table.FirstRow; r <= table.LastRow; r++)
            {
                for (var c = 0; c < Rows[r].Length; c++)
                {
                    if ((c < table.FirstColumn || c > table.LastColumn) && (Rows[r][c].Length > 0 || HiddenByMerge.ContainsKey((r, c))))
                    {
                        return null; // content beside the table
                    }
                }
            }
        }

        var blocks = new List<(int, int, SheetTable?)>();
        var cursor = 0;
        foreach (var table in ordered)
        {
            if (table.FirstRow > cursor)
            {
                blocks.Add((cursor, table.FirstRow - 1, null));
            }

            blocks.Add((table.FirstRow, table.LastRow, table));
            cursor = table.LastRow + 1;
        }

        if (cursor < Rows.Length)
        {
            blocks.Add((cursor, Rows.Length - 1, null));
        }

        // One table that is all of the sheet needs no heading: it is the sheet's table.
        return blocks.Count == 1 ? null : blocks;
    }

    // The cells of rows firstRow..lastRow and columns fromIndex..toIndex as a Markdown table (hidden rows and columns left out).
    private string Block(int firstRow, int lastRow, int fromIndex, int toIndex, bool hasHeader, bool skipEmptyRows = false, IReadOnlySet<int>? foldedColumns = null)
    {
        var rows = new List<IReadOnlyList<string>>();
        for (var r = firstRow; r <= lastRow && r < Rows.Length; r++)
        {
            if (HiddenRows.Contains(r) || (skipEmptyRows && !HasContent(r, fromIndex, toIndex)))
            {
                continue;
            }

            var row = Rows[r];
            var cells = Enumerable.Range(fromIndex, toIndex - fromIndex + 1)
                .Where(c => c < row.Length && !HiddenColumns.Contains(c) && foldedColumns?.Contains(c) != true)
                .Select(c => CellOrMark(r, c, row[c], fromIndex))
                .ToList();

            // A row that only goes on with the merged cells above it has nothing of its own (FR-106); the header row stays.
            if (foldedColumns is not null && (rows.Count > 0 || !hasHeader) && IsOnlyMerged(cells))
            {
                continue;
            }

            rows.Add(cells);
        }

        return MarkdownText.Table(rows, hasHeader);
    }

    // The cells are all hidden by merged cells ("<" or "^") or empty, and at least one is hidden by a merged cell.
    private static bool IsOnlyMerged(IEnumerable<string> cells)
    {
        var merged = false;
        foreach (var cell in cells)
        {
            if (cell is MarkdownText.MergedLeft or MarkdownText.MergedUp)
            {
                merged = true;
            }
            else if (cell.Trim().Length > 0)
            {
                return false;
            }
        }

        return merged;
    }

    /// <summary>
    /// The columns of a table that no cell starts in (FR-106): in every row the cell is hidden by a merged cell or empty, and in some row it is
    /// hidden by a merged cell. A sheet laid out on a grid (Excel 方眼紙) merges narrow columns to make one wide cell; the columns that are only a
    /// part of a wide cell have nothing to write. An empty column, with no merged cell in it, stays.
    /// </summary>
    private HashSet<int> FoldedColumns(int firstRow, int lastRow, int fromIndex, int toIndex, bool skipEmptyRows)
    {
        var folded = new HashSet<int>();
        for (var c = fromIndex; c <= toIndex; c++)
        {
            if (HiddenColumns.Contains(c))
            {
                continue;
            }

            var cells = new List<string>();
            for (var r = firstRow; r <= lastRow && r < Rows.Length; r++)
            {
                if (HiddenRows.Contains(r) || (skipEmptyRows && !HasContent(r, fromIndex, toIndex)))
                {
                    continue;
                }

                cells.Add(c < Rows[r].Length ? CellOrMark(r, c, Rows[r][c], fromIndex) : string.Empty);
            }

            if (IsOnlyMerged(cells))
            {
                folded.Add(c);
            }
        }

        // Every column folded is no table: the sheet is left as it is.
        var shown = Enumerable.Range(fromIndex, toIndex - fromIndex + 1).Count(c => !HiddenColumns.Contains(c));
        return folded.Count < shown ? folded : [];
    }

    public string ToMarkdown() => ToMarkdown(0, Math.Max(0, ColumnCount - 1));

    // A cell hidden by a merged cell is "<" (first row of the range) or "^" (below). When a merged range begins left of
    // the columns that are shown (a sheet cut into parts), its first shown cell gets the value of the top-left cell.
    private string CellOrMark(int row, int column, string text, int firstShownColumn)
    {
        if (!HiddenByMerge.TryGetValue((row, column), out var mark))
        {
            return text;
        }

        if (mark.FirstRow && column == firstShownColumn && mark.OriginColumn < firstShownColumn)
        {
            return Rows[mark.OriginRow][mark.OriginColumn];
        }

        return mark.FirstRow ? MarkdownText.MergedLeft : MarkdownText.MergedUp;
    }
}

/// <summary>Reads the worksheets of an .xlsx file (research R2, R4).</summary>
public static partial class XlsxReader
{
    /// <summary>Default column width of Excel (8.43 characters = 64 pixels = 48 points).</summary>
    private const double DefaultColumnWidthPoints = 48;

    private const double MaxDigitWidthPixels = 7;

    /// <param name="path">The workbook.</param>
    /// <param name="includeHidden">Include hidden sheets (FR-010a).</param>
    // Whether the cells that are being read are written with the marks of bold and italic (set by Read; the reading of a file is on one thread).
    [ThreadStatic]
    private static bool _marking;

    /// <param name="emphasis">Mark the parts of a text in bold or italic (FR-101); a cell all in bold (italic) is not marked.</param>
    public static IReadOnlyList<SheetContent> Read(string path, bool includeHidden, bool emphasis = false)
    {
        _marking = emphasis;
        OoxmlPackage.EnsureOpens(path, DocumentKind.Xlsx);
        try
        {
            using var document = SpreadsheetDocument.Open(path, isEditable: false);
            var workbookPart = document.WorkbookPart ?? throw new InvalidDocumentException(Loc.T("The workbook has no main part."));
            var sheets = workbookPart.Workbook?.Sheets ?? throw new InvalidDocumentException(Loc.T("The workbook has no sheets."));

            var sharedStrings = ReadSharedStrings(workbookPart);
            var struckStyles = ReadStruckStyles(workbookPart);
            var dateStyles = ReadDateStyles(workbookPart);
            var numberFormats = ReadNumberFormats(workbookPart, dateStyles);

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

                result.Add(ReadSheet(sheet.Name?.Value ?? "Sheet", hidden, worksheetPart, sharedStrings, dateStyles, numberFormats, struckStyles));
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
        IReadOnlyList<SharedItem> sharedStrings,
        IReadOnlySet<uint> dateStyles,
        IReadOnlyDictionary<uint, string> numberFormats,
        IReadOnlySet<uint> struckStyles)
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
            var text = CellText(cell, sharedStrings, dateStyles, numberFormats, struckStyles);
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

        // A cell with an outside link is written as a Markdown link (FR-070). A link inside the workbook has no address to write.
        foreach (var link in worksheet.Descendants<Hyperlink>())
        {
            if (link.Id?.Value is { } relationshipId && link.Reference?.Value is { } linkRange
                && part.HyperlinkRelationships.FirstOrDefault(h => h.Id == relationshipId)?.Uri is { } uri)
            {
                var ends = linkRange.Split(':');
                var (lc1, lr1) = ExcelColumn.ParseReference(ends[0]);
                var (lc2, lr2) = ends.Length == 2 ? ExcelColumn.ParseReference(ends[1]) : (lc1, lr1);
                var address = uri.OriginalString.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
                for (var lr = lr1; lr <= lr2; lr++)
                {
                    for (var lc = lc1; lc <= lc2; lc++)
                    {
                        if (cells.TryGetValue((lr, lc), out var linkText) && linkText.Length > 0)
                        {
                            cells[(lr, lc)] = "[" + linkText.Replace("]", "\\]", StringComparison.Ordinal) + "](" + address + ")";
                        }
                    }
                }
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
                Charts = [.. (part.DrawingsPart?.ChartParts ?? []).Select(ChartReader.Read).OfType<ChartInfo>()],
            };
        }

        // The cells under a merged range, except its top-left cell, inside the used range.
        var coveredByMerge = new Dictionary<(int Row, int Column), MergeMark>();
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
                            coveredByMerge[(r - minRow, c - minColumn)] = new MergeMark(r == r1, r1 - minRow, c1 - minColumn);
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
            Tables = ReadTables(part, minRow, maxRow, minColumn, maxColumn),
            Notes = ReadNotes(part, minRow, maxRow, minColumn, maxColumn),
            Charts = [.. (part.DrawingsPart?.ChartParts ?? []).Select(ChartReader.Read).OfType<ChartInfo>()],
            HiddenRows = HiddenRowSet(worksheet, minRow, maxRow),
            HiddenColumns = HiddenColumnSet(worksheet, minColumn, maxColumn),
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

    private static List<SheetTable> ReadTables(WorksheetPart part, int minRow, int maxRow, int minColumn, int maxColumn)
    {
        var result = new List<SheetTable>();
        foreach (var tablePart in part.TableDefinitionParts)
        {
            if (tablePart.Table is not { } definition || definition.Reference?.Value?.Split(':') is not { Length: 2 } corners)
            {
                continue;
            }

            var (c1, r1) = ExcelColumn.ParseReference(corners[0]);
            var (c2, r2) = ExcelColumn.ParseReference(corners[1]);
            var firstRow = Math.Max(r1, minRow);
            var lastRow = Math.Min(r2, maxRow);
            var firstColumn = Math.Max(c1, minColumn);
            var lastColumn = Math.Min(c2, maxColumn);
            if (firstRow > lastRow || firstColumn > lastColumn)
            {
                continue;
            }

            var name = definition.DisplayName?.Value ?? definition.Name?.Value ?? "Table";
            result.Add(new SheetTable(name, firstRow - minRow, lastRow - minRow, firstColumn - minColumn, lastColumn - minColumn, definition.HeaderRowCount?.Value != 0 && r1 >= minRow));
        }

        return result;
    }

    private static List<SheetNote> ReadNotes(WorksheetPart part, int minRow, int maxRow, int minColumn, int maxColumn)
    {
        var result = new List<SheetNote>();
        foreach (var comment in part.WorksheetCommentsPart?.Comments?.CommentList?.Elements<Comment>() ?? [])
        {
            if (comment.Reference?.Value is not { } reference)
            {
                continue;
            }

            var (column, row) = ExcelColumn.ParseReference(reference);
            var text = string.Concat(comment.Descendants<Text>().Select(t => t.Text)).Trim();
            if (text.Length > 0 && row >= minRow && row <= maxRow && column >= minColumn && column <= maxColumn)
            {
                result.Add(new SheetNote(row - minRow, column - minColumn, text));
            }
        }

        return result.OrderBy(n => n.Row).ThenBy(n => n.Column).ToList();
    }

    private static HashSet<int> HiddenRowSet(Worksheet worksheet, int firstRow, int lastRow)
    {
        var result = new HashSet<int>();
        foreach (var row in worksheet.Descendants<Row>())
        {
            if (row.Hidden?.Value == true && row.RowIndex?.Value is { } index && index >= firstRow && index <= lastRow)
            {
                result.Add((int)index - firstRow);
            }
        }

        return result;
    }

    private static HashSet<int> HiddenColumnSet(Worksheet worksheet, int firstColumn, int lastColumn)
    {
        var result = new HashSet<int>();
        foreach (var column in worksheet.Descendants<Column>())
        {
            if (column.Hidden?.Value != true || column.Min is null || column.Max is null)
            {
                continue;
            }

            for (var c = Math.Max((int)column.Min.Value, firstColumn); c <= Math.Min((int)column.Max.Value, lastColumn); c++)
            {
                result.Add(c - firstColumn);
            }
        }

        return result;
    }

    /// <summary>Excel's width (in characters of the default font) to points. 7 pixels per character, 0.75 points per pixel.</summary>
    private static double CharactersToPoints(double characters)
    {
        var pixels = Math.Truncate(((256 * characters) + Math.Truncate(128 / MaxDigitWidthPixels)) / 256 * MaxDigitWidthPixels);
        return pixels * 0.75;
    }

    // The text of a cell; a cell whose font has a strikethrough is written between ~~ (FR-092).
    private static string CellText(Cell cell, IReadOnlyList<SharedItem> sharedStrings, IReadOnlySet<uint> dateStyles, IReadOnlyDictionary<uint, string> numberFormats, IReadOnlySet<uint> struckStyles)
    {
        var struck = cell.StyleIndex?.Value is { } style && struckStyles.Contains(style);
        if (cell.DataType?.Value == CellValues.SharedString && cell.CellValue?.Text is { } raw
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count)
        {
            // The parts of the text that have a font of their own take it (and that font has a strikethrough or not); the others have the font of the cell.
            return sharedStrings[index].Text(struck, _marking);
        }

        var text = CellTextOf(cell, dateStyles, numberFormats);
        return struck && text.Length > 0 ? Strikethrough.Wrap(text) : text;
    }

    private static string CellTextOf(Cell cell, IReadOnlySet<uint> dateStyles, IReadOnlyDictionary<uint, string> numberFormats)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;

        if (type == CellValues.InlineString)
        {
            // The parts of a rich text in bold or italic are marked (FR-101).
            return _marking && cell.InlineString is { } inline && inline.Elements<Run>().Any() ? ItemOf(inline).Text(false, marking: true) : InlineText(cell.InlineString);
        }

        if (raw is null)
        {
            return string.Empty;
        }

        if (type == CellValues.SharedString)
        {
            return string.Empty; // read by CellText
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

        // A number is written as the cell shows it: digits after the point, a thousands separator, a percent, a currency (FR-070).
        if (cell.StyleIndex?.Value is { } numberStyle && numberFormats.TryGetValue(numberStyle, out var code)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return XlsxNumberFormat.Format(number, code) ?? Rounded(raw);
        }

        return Rounded(raw);
    }

    /// <summary>
    /// A number as the file stores it has up to 17 digits, with the error of the calculation (<c>1.1068500000000001</c>); Excel keeps 15
    /// (FR-107). A number with more than 15 significant digits is written with 15; an integer, and a number that has fewer digits, stay as they are.
    /// </summary>
    internal static string Rounded(string raw)
    {
        var digits = raw.TakeWhile(c => c != 'E' && c != 'e').Count(char.IsAsciiDigit);
        if (digits <= 15 || !raw.Contains('.', StringComparison.Ordinal) || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }

        var text = number.ToString("G15", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal) && !raw.Contains('E', StringComparison.OrdinalIgnoreCase) ? raw : text;
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

    /// <summary>The text of a shared string or of an inline string, with its parts when it is rich text (a part has its font: strikethrough, bold, italic).</summary>
    private static SharedItem ItemOf(DocumentFormat.OpenXml.OpenXmlElement item)
    {
        var runs = new List<(string Text, bool? Struck, bool Bold, bool Italic)>();
        var plain = new StringBuilder();
        foreach (var child in item.ChildElements)
        {
            switch (child)
            {
                case Run run:
                    var part = string.Concat(run.Elements<Text>().Select(t => t.Text));
                    plain.Append(part);

                    // A run with a font of its own has that font whole: it says by itself whether it has a strikethrough. One without has the font of the cell.
                    bool? struck = run.RunProperties is null
                        ? null
                        : run.RunProperties.Elements<Strike>().FirstOrDefault() is { } strike && (strike.Val is null || strike.Val.Value);
                    var bold = run.RunProperties?.Elements<Bold>().FirstOrDefault() is { } b && (b.Val is null || b.Val.Value);
                    var italic = run.RunProperties?.Elements<Italic>().FirstOrDefault() is { } i && (i.Val is null || i.Val.Value);
                    runs.Add((part, struck, bold, italic));
                    break;
                case Text text:
                    plain.Append(text.Text);
                    break;
            }
        }

        return new SharedItem(plain.ToString(), runs.Count > 0 ? runs : null);
    }

    private static IReadOnlyList<SharedItem> ReadSharedStrings(WorkbookPart workbookPart)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null)
        {
            return [];
        }

        var strings = new List<SharedItem>();
        foreach (var item in table.Elements<SharedStringItem>())
        {
            strings.Add(ItemOf(item));
        }

        return strings;
    }

    /// <summary>Style indexes (positions in cellXfs) whose font has a strikethrough.</summary>
    private static IReadOnlySet<uint> ReadStruckStyles(WorkbookPart workbookPart)
    {
        var result = new HashSet<uint>();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is not { } formats || stylesheet.Fonts is not { } fonts)
        {
            return result;
        }

        var fontList = fonts.Elements<Font>().ToList();
        uint index = 0;
        foreach (var format in formats.Elements<CellFormat>())
        {
            var fontId = format.FontId?.Value ?? 0;
            if (fontId < fontList.Count && fontList[(int)fontId].Elements<Strike>().FirstOrDefault() is { } strike && (strike.Val is null || strike.Val.Value))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
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

    /// <summary>Style indexes whose number format is a plain number (not General, not a date), with the format code.</summary>
    private static IReadOnlyDictionary<uint, string> ReadNumberFormats(WorkbookPart workbookPart, IReadOnlySet<uint> dateStyles)
    {
        var result = new Dictionary<uint, string>();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is not { } formats)
        {
            return result;
        }

        var custom = (stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
            .Where(f => f.NumberFormatId is not null && f.FormatCode?.Value is not null)
            .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode!.Value!);

        uint index = 0;
        foreach (var format in formats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            var code = custom.TryGetValue(id, out var own) ? own : XlsxNumberFormat.BuiltInCode(id);
            if (code is not null && !XlsxNumberFormat.IsGeneral(code) && !dateStyles.Contains(index))
            {
                result[index] = code;
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
