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

using DocumentFormat.OpenXml;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Whether a picture, a shape, a text box or a group is hidden in the document (the eye of the selection pane: <c>hidden="1"</c>
/// on its <c>cNvPr</c> or <c>docPr</c>). Such an object is not shown, so it is not written. Read by name, for every kind of document.
/// </summary>
internal static class HiddenObjects
{
    /// <summary>The object itself is hidden.</summary>
    public static bool IsHidden(OpenXmlElement element)
    {
        // An old (VML) shape is hidden by its style.
        if (element.GetAttributes().Any(a => a.LocalName == "style" && a.NamespaceUri.Length == 0
            && a.Value?.Replace(" ", string.Empty, StringComparison.Ordinal).Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase) == true))
        {
            return true;
        }

        foreach (var child in element.ChildElements)
        {
            if (child.LocalName is "cNvPr" or "docPr")
            {
                if (HasHidden(child))
                {
                    return true;
                }
            }
            else if (child.LocalName.StartsWith("nv", StringComparison.Ordinal)
                && child.ChildElements.FirstOrDefault(e => e.LocalName == "cNvPr") is { } properties && HasHidden(properties))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The object, or a group, a drawing or an anchor that holds it, is hidden.</summary>
    public static bool IsHiddenOrInHidden(OpenXmlElement element) =>
        IsHidden(element) || element.Ancestors<OpenXmlElement>().Any(IsHidden);

    private static bool HasHidden(OpenXmlElement properties) =>
        properties.GetAttributes().FirstOrDefault(a => a.LocalName == "hidden" && a.NamespaceUri.Length == 0).Value is "1" or "true";
}
