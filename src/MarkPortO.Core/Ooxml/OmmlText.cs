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

using System.Text;
using DocumentFormat.OpenXml;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// An equation of Word (OMML) as a line of text (FR-142): a fraction is <c>a/b</c> (<c>(a+1)/(b+2)</c> when a part is longer than one word), a power
/// <c>x^2</c>, an index <c>x_i</c>, a root <c>√(x)</c>, a sum or an integral with its limits. The text of the parts alone (<c>ab</c> for a fraction)
/// would read as another word.
/// </summary>
public static class OmmlText
{
    public static string Of(OpenXmlElement math) => Linear(math).Trim();

    private static OpenXmlElement? Part(OpenXmlElement element, string name) => element.ChildElements.FirstOrDefault(c => c.LocalName == name);

    private static string Of(OpenXmlElement element, string name) => Part(element, name) is { } part ? Linear(part) : string.Empty;

    // A part that is more than a word is in parentheses: (a+1)/(b+2), x^(n+1).
    private static string Group(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1 || trimmed.All(char.IsLetterOrDigit) ? trimmed : "(" + trimmed + ")";
    }

    private static string Linear(OpenXmlElement element)
    {
        switch (element.LocalName)
        {
            case "t":
                return element.InnerText;
            case "f":
                return Group(Of(element, "num")) + "/" + Group(Of(element, "den"));
            case "sSup":
                return Group(Of(element, "e")) + "^" + Group(Of(element, "sup"));
            case "sSub":
                return Group(Of(element, "e")) + "_" + Group(Of(element, "sub"));
            case "sSubSup":
                return Group(Of(element, "e")) + "_" + Group(Of(element, "sub")) + "^" + Group(Of(element, "sup"));
            case "rad":
                var degree = Of(element, "deg").Trim();
                return (degree.Length > 0 ? Group(degree) : string.Empty) + "√(" + Of(element, "e") + ")";
            case "d":
                var properties = Part(element, "dPr");
                var open = Attribute(properties, "begChr", "(");
                var close = Attribute(properties, "endChr", ")");
                var separator = Attribute(properties, "sepChr", ",");
                return open + string.Join(separator, element.ChildElements.Where(c => c.LocalName == "e").Select(Linear)) + close;
            case "nary":
                var naryProperties = Part(element, "naryPr");
                var symbol = Attribute(naryProperties, "chr", "∫");
                var lower = Of(element, "sub");
                var upper = Of(element, "sup");
                return symbol + (lower.Length > 0 ? "_" + Group(lower) : string.Empty) + (upper.Length > 0 ? "^" + Group(upper) : string.Empty) + " " + Of(element, "e");
            case "limLow":
                return Group(Of(element, "e")) + "_" + Group(Of(element, "lim"));
            case "limUpp":
                return Group(Of(element, "e")) + "^" + Group(Of(element, "lim"));
            case "func":
                return Of(element, "fName") + " " + Of(element, "e");
            default:
                var builder = new StringBuilder();
                foreach (var child in element.ChildElements)
                {
                    if (!child.LocalName.EndsWith("Pr", StringComparison.Ordinal))
                    {
                        builder.Append(Linear(child));
                    }
                }

                return builder.ToString();
        }
    }

    // The value of the property of the properties element (<m:begChr m:val="[" />); an empty value is "none".
    private static string Attribute(OpenXmlElement? properties, string name, string fallback)
    {
        var element = properties?.ChildElements.FirstOrDefault(c => c.LocalName == name);
        if (element is null)
        {
            return fallback;
        }

        return element.GetAttributes().FirstOrDefault(a => a.LocalName == "val").Value ?? string.Empty;
    }
}
