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

namespace MarkPortO.App;

/// <summary>
/// Tells when the wheel may change the page of the preview (FR-080): only for a wheel that starts after a pause, so that a scroll that
/// goes on past the end of a page does not carry on to the next page. One clock for all the panes, because the page (and the document of
/// the WebView2 pane) is new after a change, and a clock inside it would start again from zero.
/// </summary>
internal static class WheelGesture
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FitPause = TimeSpan.FromMilliseconds(120);
    private static DateTime _last = DateTime.MinValue;
    private static DateTime _lastFit = DateTime.MinValue;

    /// <summary>The wheel turned, and the pane could scroll (or the wheel was not at its end): not a gesture that changes the page.</summary>
    public static void Scrolled() => _last = DateTime.UtcNow;

    /// <summary>
    /// The wheel turned over a pane whose page fits, with no scroll bar: there is nothing to scroll past, so every turn changes the page.
    /// Only the turns that come within a hair of each other (one notch of a fast wheel gives several events) count as one.
    /// </summary>
    public static bool StepsWhenFits()
    {
        var now = DateTime.UtcNow;
        var steps = now - _lastFit >= FitPause;
        _lastFit = now;
        _last = now;
        return steps;
    }

    /// <summary>The wheel turned at the end of the pane. True when it started after a pause: the page changes.</summary>
    public static bool StartsAtEnd()
    {
        var now = DateTime.UtcNow;
        var starts = now - _last >= Pause;
        _last = now;
        return starts;
    }
}
