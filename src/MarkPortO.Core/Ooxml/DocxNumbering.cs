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
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// The automatic numbers of a docx ("1.", "1.1", "第1章", "(a)"): the file keeps only the definition of the list, so the
/// numbers are counted here, paragraph by paragraph in document order, the way Word shows them (FR-077).
/// </summary>
/// <remarks>
/// The list of a paragraph is its own, or the one of its style (a heading style that is linked to a multilevel list), or
/// the one of the style it is based on. A paragraph that is in a list advances the count even when its text is empty.
/// </remarks>
internal sealed partial class DocxNumbering
{
    private const int Levels = 9;

    private readonly Numbering? _numbering;
    private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?[]> _counters = [];

    public DocxNumbering(MainDocumentPart main)
    {
        _numbering = main.NumberingDefinitionsPart?.Numbering;
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
        {
            if (style.StyleId?.Value is { } id)
            {
                _styles[id] = style;
            }
        }
    }

    /// <summary>
    /// The number of the paragraph as it is shown ("1.2", "第3章"), or null when the paragraph has no number (or has a bullet).
    /// Every paragraph of the body is passed in order, so that the count is right.
    /// </summary>
    public string? Label(Paragraph paragraph)
    {
        if (_numbering is null)
        {
            return null;
        }

        var (numberId, level) = Resolve(paragraph);
        if (numberId is null or 0)
        {
            return null;
        }

        var instance = _numbering.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numberId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        var abstractNum = _numbering.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
        if (instance is null || abstractNum is null)
        {
            return null;
        }

        level = Math.Clamp(level, 0, Levels - 1);

        // A list that restarts its count by an override of its own does not continue the count of the list it is made from.
        var restarts = instance.Elements<LevelOverride>().Any(o => o.StartOverrideNumberingValue is not null);
        var key = restarts ? "n" + numberId.Value.ToString(CultureInfo.InvariantCulture) : "a" + abstractId!.Value.ToString(CultureInfo.InvariantCulture);
        if (!_counters.TryGetValue(key, out var counters))
        {
            _counters[key] = counters = new int?[Levels];
        }

        counters[level] = counters[level] is { } current ? current + 1 : StartOf(instance, abstractNum, level);
        for (var deeper = level + 1; deeper < Levels; deeper++)
        {
            counters[deeper] = null;
        }

        var definition = LevelOf(instance, abstractNum, level);
        var format = definition?.NumberingFormat?.Val;
        var text = definition?.LevelText?.Val?.Value;
        if (definition is null || string.IsNullOrEmpty(text) || format is null || format == NumberFormatValues.Bullet || format == NumberFormatValues.None)
        {
            return null;
        }

        var legal = definition.IsLegalNumberingStyle is not null;
        var label = Placeholder().Replace(text, match =>
        {
            var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            var value = counters[index] ?? StartOf(instance, abstractNum, index);
            var levelFormat = legal ? NumberFormatValues.Decimal : LevelOf(instance, abstractNum, index)?.NumberingFormat?.Val?.Value ?? NumberFormatValues.Decimal;
            return Format(value, levelFormat);
        });
        return label.Trim().Length == 0 ? null : label.Trim();
    }

    // The list and the level of a paragraph: its own, else its style's, else the style it is based on.
    private (int? NumberId, int Level) Resolve(Paragraph paragraph)
    {
        var direct = paragraph.ParagraphProperties?.NumberingProperties;
        var numberId = direct?.NumberingId?.Val?.Value;
        var level = direct?.NumberingLevelReference?.Val?.Value;
        if (numberId == 0)
        {
            return (0, 0); // "no list" on purpose
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var chain = StyleChain(styleId).ToList();
        foreach (var style in chain)
        {
            var properties = style.StyleParagraphProperties?.NumberingProperties;
            if (properties is null)
            {
                continue;
            }

            if (numberId is null && properties.NumberingId?.Val?.Value is { } styleNumberId)
            {
                numberId = styleNumberId;
                level ??= properties.NumberingLevelReference?.Val?.Value;
            }

            level ??= numberId is not null ? properties.NumberingLevelReference?.Val?.Value : null;
            if (numberId is not null && level is not null)
            {
                break;
            }
        }

        if (numberId is null or 0)
        {
            return (numberId, 0);
        }

        // A heading style that is linked to a level of the list says so in the list, not in the style.
        level ??= LinkedLevel(numberId.Value, chain.Select(s => s.StyleId?.Value));
        return (numberId, level ?? 0);
    }

    private IEnumerable<Style> StyleChain(string? styleId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (styleId is not null && seen.Add(styleId) && _styles.TryGetValue(styleId, out var style))
        {
            yield return style;
            styleId = style.BasedOn?.Val?.Value;
        }
    }

    private int? LinkedLevel(int numberId, IEnumerable<string?> styleIds)
    {
        var instance = _numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numberId);
        var abstractNum = _numbering?.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == instance?.AbstractNumId?.Val?.Value);
        if (abstractNum is null)
        {
            return null;
        }

        foreach (var styleId in styleIds)
        {
            var linked = abstractNum.Elements<Level>().FirstOrDefault(l => string.Equals(l.ParagraphStyleIdInLevel?.Val?.Value, styleId, StringComparison.OrdinalIgnoreCase));
            if (linked?.LevelIndex?.Value is { } index)
            {
                return index;
            }
        }

        return null;
    }

    private static Level? LevelOf(NumberingInstance instance, AbstractNum abstractNum, int level)
    {
        var overridden = instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == level)?.Level;
        return overridden ?? abstractNum.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
    }

    private static int StartOf(NumberingInstance instance, AbstractNum abstractNum, int level)
    {
        var overridden = instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == level)?.StartOverrideNumberingValue?.Val?.Value;
        return overridden ?? LevelOf(instance, abstractNum, level)?.StartNumberingValue?.Val?.Value ?? 1;
    }

    /// <summary>A number in the format of a level. A format that is not known is written as a decimal number.</summary>
    internal static string Format(int value, NumberFormatValues format)
    {
        if (format == NumberFormatValues.DecimalZero)
        {
            return value.ToString("00", CultureInfo.InvariantCulture);
        }

        if (format == NumberFormatValues.UpperRoman || format == NumberFormatValues.LowerRoman)
        {
            var roman = Roman(value);
            return format == NumberFormatValues.UpperRoman ? roman : roman.ToLowerInvariant();
        }

        if (format == NumberFormatValues.UpperLetter || format == NumberFormatValues.LowerLetter)
        {
            var letter = (char)('A' + ((value - 1) % 26));
            var text = new string(letter, ((value - 1) / 26) + 1);
            return format == NumberFormatValues.UpperLetter ? text : text.ToLowerInvariant();
        }

        if (format == NumberFormatValues.DecimalFullWidth || format == NumberFormatValues.DecimalFullWidth2)
        {
            return new string([.. value.ToString(CultureInfo.InvariantCulture).Select(c => (char)(c + 0xFEE0))]);
        }

        if (format == NumberFormatValues.DecimalEnclosedCircle && value is >= 1 and <= 20)
        {
            return ((char)(0x2460 + value - 1)).ToString();
        }

        if (format == NumberFormatValues.JapaneseCounting || format == NumberFormatValues.IdeographTraditional
            || format == NumberFormatValues.ChineseCounting || format == NumberFormatValues.TaiwaneseCounting)
        {
            return value is >= 1 and <= 99 ? KanjiNumber(value) : value.ToString(CultureInfo.InvariantCulture);
        }

        if (format == NumberFormatValues.IdeographDigital)
        {
            return new string([.. value.ToString(CultureInfo.InvariantCulture).Select(c => "〇一二三四五六七八九"[c - '0'])]);
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Roman(int value)
    {
        if (value is < 1 or > 3999)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        foreach (var (number, text) in new (int, string)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            while (value >= number)
            {
                builder.Append(text);
                value -= number;
            }
        }

        return builder.ToString();
    }

    // 1..99 as 一, 十, 十一, 二十, 九十九.
    private static string KanjiNumber(int value)
    {
        const string digits = "〇一二三四五六七八九";
        var tens = value / 10;
        var ones = value % 10;
        var text = new StringBuilder();
        if (tens > 0)
        {
            if (tens > 1)
            {
                text.Append(digits[tens]);
            }

            text.Append('十');
        }

        if (ones > 0)
        {
            text.Append(digits[ones]);
        }

        return text.ToString();
    }

    [GeneratedRegex("%([1-9])")]
    private static partial Regex Placeholder();
}
