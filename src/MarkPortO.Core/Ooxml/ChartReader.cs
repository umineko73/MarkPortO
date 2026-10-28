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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Ooxml;

/// <summary>A chart of a workbook or a presentation: its kind, title, categories, and the values that the file keeps (FR-070, FR-072).</summary>
public sealed record ChartInfo(string Title, string Kind, IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series)
{
    /// <summary>The chart as a small Markdown table under a heading: the values are the ones saved in the file, so they are exact.</summary>
    public string ToMarkdown()
    {
        var heading = "#### Chart: " + (Title.Length > 0 ? MarkdownText.EscapeCell(Title) : "(untitled)") + " (" + Kind + ")";
        var points = Math.Max(Categories.Count, Series.Count == 0 ? 0 : Series.Max(s => s.Values.Count));
        if (Series.Count == 0 || points == 0)
        {
            return heading;
        }

        var rows = new List<IReadOnlyList<string>> { new[] { string.Empty }.Concat(Series.Select(s => s.Name)).ToArray() };
        for (var i = 0; i < points; i++)
        {
            rows.Add(new[] { i < Categories.Count ? Categories[i] : string.Empty }
                .Concat(Series.Select(s => i < s.Values.Count ? s.Values[i] : string.Empty)).ToArray());
        }

        return heading + "\n\n" + MarkdownText.Table(rows);
    }
}

public sealed record ChartSeries(string Name, IReadOnlyList<string> Values);

/// <summary>Reads a chart part by the names of its elements, so that every kind of chart (bar, line, pie, scatter, ...) is read the same way.</summary>
internal static class ChartReader
{
    public static ChartInfo? Read(ChartPart part)
    {
        if (part.ChartSpace is not { } space)
        {
            return null;
        }

        var all = space.Descendants<OpenXmlElement>().ToList();
        var title = string.Concat(
            all.FirstOrDefault(e => e.LocalName == "title" && e.Parent?.LocalName == "chart")
                ?.Descendants<OpenXmlElement>().Where(e => e.LocalName == "t" && e.NamespaceUri.Contains("drawingml", StringComparison.Ordinal)).Select(e => e.InnerText) ?? []);

        var kinds = all.Where(e => e.LocalName.EndsWith("Chart", StringComparison.Ordinal) && e.Parent?.LocalName == "plotArea")
            .Select(e => e.LocalName[..^"Chart".Length])
            .Distinct()
            .ToList();
        if (kinds.Count == 0)
        {
            return null;
        }

        var categories = new List<string>();
        var series = new List<ChartSeries>();
        foreach (var ser in all.Where(e => e.LocalName == "ser"))
        {
            var name = Points(Child(ser, "tx")).FirstOrDefault() ?? string.Empty;
            var cat = Child(ser, "cat") ?? Child(ser, "xVal");
            if (categories.Count == 0 && cat is not null)
            {
                categories.AddRange(Points(cat));
            }

            var val = Child(ser, "val") ?? Child(ser, "yVal");
            series.Add(new ChartSeries(name, Points(val, applyFormat: true)));
        }

        return new ChartInfo(title.Trim(), string.Join("+", kinds), categories, series);
    }

    private static OpenXmlElement? Child(OpenXmlElement parent, string localName) =>
        parent.ChildElements.FirstOrDefault(e => e.LocalName == localName);

    // The points saved in a string or number cache, in order; a missing point is empty.
    private static List<string> Points(OpenXmlElement? source, bool applyFormat = false)
    {
        var result = new List<string>();
        if (source is null)
        {
            return result;
        }

        var cache = source.Descendants<OpenXmlElement>().FirstOrDefault(e => e.LocalName is "strCache" or "numCache");
        if (cache is null)
        {
            // A name that is typed in (c:tx/c:v), not taken from cells.
            var literal = source.Descendants<OpenXmlElement>().FirstOrDefault(e => e.LocalName == "v");
            if (literal is not null)
            {
                result.Add(literal.InnerText);
            }

            return result;
        }

        var formatCode = applyFormat ? cache.ChildElements.FirstOrDefault(e => e.LocalName == "formatCode")?.InnerText : null;
        foreach (var point in cache.ChildElements.Where(e => e.LocalName == "pt"))
        {
            var index = int.TryParse(point.GetAttributes().FirstOrDefault(a => a.LocalName == "idx").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : result.Count;
            while (result.Count <= index)
            {
                result.Add(string.Empty);
            }

            var text = point.ChildElements.FirstOrDefault(e => e.LocalName == "v")?.InnerText ?? string.Empty;
            if (formatCode is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                text = XlsxNumberFormat.Format(number, formatCode) ?? text;
            }

            result[index] = text;
        }

        return result;
    }
}
