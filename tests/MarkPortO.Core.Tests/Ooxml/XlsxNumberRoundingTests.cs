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

using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-107: a number with the error of the calculation is written with the 15 digits that Excel keeps.</summary>
public class XlsxNumberRoundingTests
{
    [Theory]
    [InlineData("1.1068500000000001", "1.10685")]
    [InlineData("1.2251169225000003", "1.2251169225")]
    [InlineData("0.30000000000000004", "0.3")]
    [InlineData("-0.30000000000000004", "-0.3")]
    [InlineData("1.838792045123456789", "1.83879204512346")]
    public void ANumberWithMoreThan15Digits_IsWrittenWith15(string raw, string expected) =>
        Assert.Equal(expected, XlsxReader.Rounded(raw));

    [Theory]
    [InlineData("0.1")]
    [InlineData("12345.6789")]
    [InlineData("123456789012345")]
    [InlineData("12345678901234567890")]
    [InlineData("1.5E-10")]
    [InlineData("TRUE")]
    [InlineData("")]
    public void ANumberThatIsShort_AnIntegerAndOtherText_StayAsTheyAre(string raw) =>
        Assert.Equal(raw, XlsxReader.Rounded(raw));
}
