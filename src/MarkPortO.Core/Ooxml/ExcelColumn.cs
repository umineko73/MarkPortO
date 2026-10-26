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

namespace MarkPortO.Core.Ooxml;

/// <summary>Excel column letters: 1 is <c>A</c>, 26 is <c>Z</c>, 27 is <c>AA</c>.</summary>
public static class ExcelColumn
{
    public static string Name(int oneBasedIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(oneBasedIndex, 1);
        var name = string.Empty;
        for (var n = oneBasedIndex; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    public static int Index(string letters)
    {
        var index = 0;
        foreach (var ch in letters.ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z')
            {
                throw new FormatException($"'{letters}' is not a column name.");
            }

            index = (index * 26) + (ch - 'A' + 1);
        }

        return index;
    }

    /// <summary>Splits a reference such as <c>B12</c> into column 2 and row 12.</summary>
    public static (int Column, int Row) ParseReference(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
        var digits = reference[letters.Length..];
        return (Index(letters), int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
    }
}
