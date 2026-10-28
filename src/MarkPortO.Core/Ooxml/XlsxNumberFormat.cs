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

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Writes a number the way an Excel number format shows it (FR-070): digits after the point, a thousands separator,
/// a percent, a currency. A format that is not understood (scientific, fraction, text) gives <c>null</c>, and the caller keeps
/// the stored value. Dates are not handled here.
/// </summary>
internal static class XlsxNumberFormat
{
    /// <summary>The formats that Excel has built in, by id, for the ones that show a plain number.</summary>
    private static readonly Dictionary<uint, string> BuiltIn = new()
    {
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [37] = "#,##0;(#,##0)",
        [38] = "#,##0;(#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;(#,##0.00)",
    };

    /// <summary>The format code of a built-in number format id, or null when it is General or not a plain number.</summary>
    public static string? BuiltInCode(uint id) => BuiltIn.GetValueOrDefault(id);

    /// <summary>True when the code is "General" (or empty): the stored value is the text.</summary>
    public static bool IsGeneral(string code) => code.Length == 0 || code.Equals("General", StringComparison.OrdinalIgnoreCase);

    public static string? Format(double value, string code)
    {
        if (IsGeneral(code) || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        var sections = SplitSections(code);
        string section;
        var negative = value < 0;
        var implicitMinus = false;
        if (sections.Count == 1)
        {
            section = sections[0];
            implicitMinus = negative;
        }
        else if (negative)
        {
            section = sections[1];
        }
        else if (value == 0 && sections.Count >= 3)
        {
            section = sections[2];
        }
        else
        {
            section = sections[0];
        }

        var parts = Tokenise(section);
        if (parts is null)
        {
            return null;
        }

        if (parts.LiteralOnly is not null)
        {
            return parts.LiteralOnly;
        }

        var number = Math.Abs(value);
        var percent = parts.Percents;
        for (var i = 0; i < percent; i++)
        {
            number *= 100;
        }

        for (var i = 0; i < parts.TrailingCommas; i++)
        {
            number /= 1000;
        }

        if (number > (double)decimal.MaxValue / 10)
        {
            return null;
        }

        var rounded = Math.Round((decimal)number, parts.Decimals, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("F" + parts.Decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integer = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];

        // A format with no digit before the point (".00") writes no zero there.
        if (parts.MinIntegerDigits == 0 && integer == "0")
        {
            integer = string.Empty;
        }

        integer = integer.PadLeft(parts.MinIntegerDigits, '0');
        if (parts.Grouping && integer.Length > 3)
        {
            var grouped = new StringBuilder();
            for (var i = 0; i < integer.Length; i++)
            {
                if (i > 0 && (integer.Length - i) % 3 == 0)
                {
                    grouped.Append(',');
                }

                grouped.Append(integer[i]);
            }

            integer = grouped.ToString();
        }

        var digits = fraction.Length > 0 ? integer + "." + fraction : integer;
        var result = parts.Prefix + digits + parts.Suffix;
        return implicitMinus && rounded != 0 ? "-" + result : result;
    }

    private sealed class Parts
    {
        public string Prefix = string.Empty;
        public string Suffix = string.Empty;
        public int Decimals;
        public int MinIntegerDigits;
        public bool Grouping;
        public int Percents;
        public int TrailingCommas;
        public string? LiteralOnly;
    }

    private static List<string> SplitSections(string code)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '\\' && i + 1 < code.Length)
            {
                current.Append(ch).Append(code[++i]);
                continue;
            }

            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch == '[')
            {
                bracket = true;
            }
            else if (!quoted && ch == ']')
            {
                bracket = false;
            }

            if (ch == ';' && !quoted && !bracket)
            {
                sections.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        sections.Add(current.ToString());
        return sections;
    }

    private enum Kind { Digit, Point, Comma, Percent, Literal }

    private readonly record struct Token(Kind Kind, char Digit, string Text);

    // Reads one section: the text before the digits, the digit pattern, and the text after. null: not understood.
    private static Parts? Tokenise(string section)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < section.Length; i++)
        {
            var ch = section[i];
            switch (ch)
            {
                case '0' or '#' or '?':
                    tokens.Add(new Token(Kind.Digit, ch, string.Empty));
                    break;
                case '.':
                    tokens.Add(new Token(Kind.Point, ch, string.Empty));
                    break;
                case ',':
                    tokens.Add(new Token(Kind.Comma, ch, string.Empty));
                    break;
                case '%':
                    tokens.Add(new Token(Kind.Percent, ch, "%"));
                    break;
                case 'E' or 'e' or 'G' or 'g' or '@' or '/' or 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'A' or 'a' or 'B' or 'b':
                    // Scientific, fraction, text, date, "General" inside a section: not a plain number.
                    return null;
                case '"':
                    var end = section.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        return null;
                    }

                    tokens.Add(new Token(Kind.Literal, ch, section.Substring(i + 1, end - i - 1)));
                    i = end;
                    break;
                case '\\':
                    if (i + 1 < section.Length)
                    {
                        tokens.Add(new Token(Kind.Literal, ch, section[++i].ToString()));
                    }

                    break;
                case '_':
                    if (i + 1 < section.Length)
                    {
                        i++;
                        tokens.Add(new Token(Kind.Literal, ch, " "));
                    }

                    break;
                case '*':
                    if (i + 1 < section.Length)
                    {
                        i++; // a fill character: not written
                    }

                    break;
                case '[':
                    var close = section.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    var inside = section.Substring(i + 1, close - i - 1);
                    if (inside.StartsWith('$'))
                    {
                        // [$¥-411]: the part before the "-" is the currency symbol.
                        var symbol = inside[1..];
                        var dash = symbol.IndexOf('-', StringComparison.Ordinal);
                        tokens.Add(new Token(Kind.Literal, ch, dash >= 0 ? symbol[..dash] : symbol));
                    }
                    else if (!IsColourOrCondition(inside))
                    {
                        return null; // a time such as [h]
                    }

                    i = close;
                    break;
                default:
                    tokens.Add(new Token(Kind.Literal, ch, ch.ToString()));
                    break;
            }
        }

        var first = tokens.FindIndex(t => t.Kind == Kind.Digit || (t.Kind == Kind.Point));
        var last = tokens.FindLastIndex(t => t.Kind == Kind.Digit);
        if (last < 0)
        {
            // A section with no digit placeholder, such as "zero" in 0;-0;"zero", is just its text.
            return tokens.All(t => t.Kind == Kind.Literal)
                ? new Parts { LiteralOnly = string.Concat(tokens.Select(t => t.Text)) }
                : null;
        }

        // A point before the first digit counts (".00"); a lone point in the text does not.
        if (first > last)
        {
            return null;
        }

        var parts = new Parts();
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var seenPoint = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (i < first)
            {
                prefix.Append(token.Kind == Kind.Comma ? "," : token.Text);
                parts.Percents += token.Kind == Kind.Percent ? 1 : 0;
            }
            else if (i <= last)
            {
                switch (token.Kind)
                {
                    case Kind.Digit:
                        if (seenPoint)
                        {
                            parts.Decimals++;
                        }
                        else if (token.Digit == '0')
                        {
                            parts.MinIntegerDigits++;
                        }

                        break;
                    case Kind.Point when !seenPoint:
                        seenPoint = true;
                        break;
                    case Kind.Comma when !seenPoint:
                        parts.Grouping = true;
                        break;
                    default:
                        return null; // text, a percent, or a second point inside the digits
                }
            }
            else
            {
                if (token.Kind == Kind.Comma && suffix.Length == 0 && tokens.Skip(last + 1).Take(i - last - 1).All(t => t.Kind == Kind.Comma))
                {
                    parts.TrailingCommas++; // a comma right after the digits divides by 1000
                    continue;
                }

                suffix.Append(token.Kind == Kind.Comma ? "," : token.Text);
                parts.Percents += token.Kind == Kind.Percent ? 1 : 0;
            }
        }

        parts.Prefix = prefix.ToString();
        parts.Suffix = suffix.ToString();
        return parts;
    }

    private static bool IsColourOrCondition(string inside)
    {
        var lower = inside.ToLowerInvariant();
        return lower is "red" or "black" or "blue" or "green" or "yellow" or "cyan" or "magenta" or "white"
            || lower.StartsWith("color", StringComparison.Ordinal)
            || lower.StartsWith('<') || lower.StartsWith('>') || lower.StartsWith('=');
    }
}
