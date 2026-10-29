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

public class ExcelUnitPlannerTests
{
    private static SheetContent Sheet(string name, params double[] widths) => new()
    {
        Name = name,
        FirstColumn = 1,
        LastColumn = widths.Length,
        FirstRow = 1,
        LastRow = 1,
        Rows = [Enumerable.Repeat("x", widths.Length).ToArray()],
        ColumnWidthsPoints = widths,
    };

    [Fact]
    public void TheLimitIsThePrintableWidthDividedByTheMinimumScale()
    {
        // A3 landscape 1190.55 pt minus 2 x 18 pt margins = 1154.55 pt; / 0.2 = 5772.75 pt.
        Assert.Equal(1154.55, ExcelUnitPlanner.PrintableWidthPoints, 2);
        Assert.Equal(5772.75, ExcelUnitPlanner.MaxGroupWidthPoints, 2);
    }

    [Theory]
    [InlineData(48.0, 1.0)]       // 5 columns: not shrunk
    [InlineData(1924.25, 1.0)]    // shrunk to 0.6: the text is as large as the DPI says
    [InlineData(2886.375, 1.5)]   // shrunk to 0.4: 0.6 / 0.4
    [InlineData(5772.75, 3.0)]    // shrunk to 0.2: 0.6 / 0.2
    public void ASheetThatIsShrunkBelowTheComfortScale_GetsAHigherDpi(double width, double expected)
    {
        var sheet = Sheet("Data", width);
        var unit = Assert.Single(ExcelUnitPlanner.Plan(sheet));

        Assert.Equal(expected, ExcelUnitPlanner.DpiFactor(sheet, unit), 3);
    }

    [Fact]
    public void ASheetThatFits_IsOneUnitWithAllColumns()
    {
        var units = ExcelUnitPlanner.Plan(Sheet("Data", 48, 48, 48, 48, 48));

        var unit = Assert.Single(units);
        Assert.Equal((1, 1, 0, 4, "A:E"), (unit.Part, unit.PartCount, unit.FromColumnIndex, unit.ToColumnIndex, unit.ColumnRange));
    }

    [Fact]
    public void ASheetExactlyAtTheLimit_IsStillOneUnit()
    {
        var half = ExcelUnitPlanner.MaxGroupWidthPoints / 2;

        Assert.Single(ExcelUnitPlanner.Plan(Sheet("Data", half, half)));
    }

    [Fact]
    public void ASheetJustOverTheLimit_IsSplitAtAColumnBoundary()
    {
        var half = (ExcelUnitPlanner.MaxGroupWidthPoints / 2) + 1;

        var units = ExcelUnitPlanner.Plan(Sheet("Data", half, half));

        Assert.Equal(2, units.Count);
        Assert.Equal(["A:A", "B:B"], units.Select(u => u.ColumnRange));
        Assert.Equal([1, 2], units.Select(u => u.Part));
        Assert.All(units, u => Assert.Equal(2, u.PartCount));
    }

    [Fact]
    public void OneHundredSixtyWideColumns_AreSplitIntoGroupsThatEachFit_CoveringEveryColumnOnce()
    {
        var sheet = Sheet("Wide", Enumerable.Repeat(73.5, 160).ToArray());

        var units = ExcelUnitPlanner.Plan(sheet);

        // 78 columns x 73.5 pt = 5733 pt fits; 79 columns = 5806.5 pt does not.
        Assert.Equal(3, units.Count);
        Assert.Equal(["A:BZ", "CA:EZ", "FA:FD"], units.Select(u => u.ColumnRange));
        Assert.All(units, u =>
        {
            var width = sheet.ColumnWidthsPoints.Skip(u.FromColumnIndex).Take(u.ToColumnIndex - u.FromColumnIndex + 1).Sum();
            Assert.True(width <= ExcelUnitPlanner.MaxGroupWidthPoints, $"{u.ColumnRange} is {width} pt wide");
        });
        Assert.Equal(Enumerable.Range(0, 160), units.SelectMany(u => Enumerable.Range(u.FromColumnIndex, u.ToColumnIndex - u.FromColumnIndex + 1)));
    }

    [Fact]
    public void AColumnWiderThanTheLimit_GetsAGroupOfItsOwn()
    {
        var units = ExcelUnitPlanner.Plan(Sheet("Data", 48, 9000, 48));

        Assert.Equal(["A:A", "B:B", "C:C"], units.Select(u => u.ColumnRange));
    }

    [Fact]
    public void HiddenColumns_HaveNoWidth_AndDoNotForceASplit()
    {
        var widths = new double[40];
        Array.Fill(widths, 0); // forty hidden columns
        widths[0] = 48;

        Assert.Single(ExcelUnitPlanner.Plan(Sheet("Data", widths)));
    }

    [Fact]
    public void AnEmptySheet_HasNoUnits()
    {
        var empty = new SheetContent { Name = "Empty", Rows = [], ColumnWidthsPoints = [] };

        Assert.Empty(ExcelUnitPlanner.Plan(empty));
    }

    [Fact]
    public void ColumnRange_StartsAtTheFirstColumnOfTheUsedRange()
    {
        var sheet = new SheetContent
        {
            Name = "Offset",
            FirstColumn = 3, // C
            LastColumn = 5,
            FirstRow = 2,
            LastRow = 2,
            Rows = [["a", "b", "c"]],
            ColumnWidthsPoints = [48, 48, 48],
        };

        Assert.Equal("C:E", Assert.Single(ExcelUnitPlanner.Plan(sheet)).ColumnRange);
    }

    [Theory]
    [InlineData(MarkPortO.Core.Jobs.Models.ExcelRange.Rows)]
    [InlineData(MarkPortO.Core.Jobs.Models.ExcelRange.PrintArea)]
    public void WhenTheRangeIsNotTheColumns_AWideSheetIsOneUnit(MarkPortO.Core.Jobs.Models.ExcelRange range)
    {
        var half = (ExcelUnitPlanner.MaxGroupWidthPoints / 2) + 1;

        var unit = Assert.Single(ExcelUnitPlanner.Plan(Sheet("Data", half, half, half), range));

        Assert.Equal(1, unit.PartCount);
        Assert.Equal((0, 2), (unit.FromColumnIndex, unit.ToColumnIndex));
    }
}
