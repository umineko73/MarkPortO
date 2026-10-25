// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

namespace MarkPort.Core.Ooxml;

/// <summary>
/// A shape, described only by what decides whether it is part of a "×" mark: its geometry, how much of the page it
/// covers, and for a line, which diagonal it follows.
/// </summary>
/// <param name="Geometry">The preset name (for example <c>mathMultiply</c>, <c>line</c>) or <c>custGeom</c> for a freehand shape.</param>
/// <param name="WidthFraction">Width as a fraction of the page width (1 = the whole width).</param>
/// <param name="HeightFraction">Height as a fraction of the page height.</param>
/// <param name="AntiDiagonal">For a line: it runs from the top right to the bottom left. Otherwise from the top left to the bottom right.</param>
public sealed record ShapeBox(string Geometry, double WidthFraction, double HeightFraction, bool AntiDiagonal = false);

/// <summary>What the shapes of one page add up to.</summary>
/// <param name="IsCross">The page carries a "×" over (nearly) the whole page: it is a deleted page (FR-012).</param>
/// <param name="HasUnsupportedLargeShape">
/// A freehand shape covers the page. It may be a "×" drawn by hand, but that cannot be told: the page is not treated
/// as deleted and the user is warned instead.
/// </param>
public sealed record CrossVerdict(bool IsCross, bool HasUnsupportedLargeShape)
{
    public static readonly CrossVerdict None = new(false, false);
}

/// <summary>
/// Decides whether a page is a deleted page: a "×" drawn with shapes over the whole page (research R5).
/// Two forms are recognised: one "multiply" shape, or two diagonal lines that cross (one in each direction).
/// Text "×", small "×" marks, pictures, and freehand drawings are not recognised.
/// </summary>
public static class CrossShapeDetector
{
    /// <summary>A shape covers "the whole page" when it is at least this fraction of the page in both directions.</summary>
    public const double CoverageThreshold = 0.70;

    private static readonly HashSet<string> LineGeometries = new(StringComparer.OrdinalIgnoreCase)
    {
        "line", "straightConnector1",
    };

    public static CrossVerdict Evaluate(IEnumerable<ShapeBox> shapesOfOnePage)
    {
        var large = shapesOfOnePage.Where(IsLarge).ToList();

        var multiply = large.Any(s => string.Equals(s.Geometry, "mathMultiply", StringComparison.OrdinalIgnoreCase));
        var lines = large.Where(s => LineGeometries.Contains(s.Geometry)).ToList();
        var crossingLines = lines.Any(l => !l.AntiDiagonal) && lines.Any(l => l.AntiDiagonal);
        var freehand = large.Any(s => string.Equals(s.Geometry, "custGeom", StringComparison.OrdinalIgnoreCase));

        var isCross = multiply || crossingLines;
        return new CrossVerdict(isCross, freehand && !isCross);
    }

    private static bool IsLarge(ShapeBox shape) =>
        shape.WidthFraction >= CoverageThreshold && shape.HeightFraction >= CoverageThreshold;
}
