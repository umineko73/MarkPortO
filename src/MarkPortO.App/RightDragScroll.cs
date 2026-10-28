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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkPortO.App;

/// <summary>Scrolls a view by dragging it with the right mouse button (the preview, FR-068): the content follows the pointer.</summary>
internal static class RightDragScroll
{
    /// <summary>Lets the right button drag-scroll a scroll viewer.</summary>
    public static void Attach(ScrollViewer viewer) =>
        Attach(viewer, () => (viewer.HorizontalOffset, viewer.VerticalOffset), (x, y) =>
        {
            viewer.ScrollToHorizontalOffset(x);
            viewer.ScrollToVerticalOffset(y);
        });

    /// <summary>Lets the right button drag-scroll a text box.</summary>
    public static void Attach(TextBox box) =>
        Attach(box, () => (box.HorizontalOffset, box.VerticalOffset), (x, y) =>
        {
            box.ScrollToHorizontalOffset(x);
            box.ScrollToVerticalOffset(y);
        });

    private static void Attach(UIElement element, Func<(double X, double Y)> read, Action<double, double> scrollTo)
    {
        Point start = default;
        (double X, double Y) from = default;
        var dragging = false;
        var moved = false;

        element.PreviewMouseRightButtonDown += (_, e) =>
        {
            dragging = element.CaptureMouse();
            moved = false;
            start = e.GetPosition(element);
            from = read();
        };

        element.PreviewMouseMove += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            if (e.RightButton != MouseButtonState.Pressed)
            {
                element.ReleaseMouseCapture();
                return;
            }

            var now = e.GetPosition(element);
            moved = moved || Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) > 3;
            if (moved)
            {
                ((FrameworkElement)element).Cursor = Cursors.SizeAll;
                scrollTo(from.X - (now.X - start.X), from.Y - (now.Y - start.Y));
            }
        };

        element.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            element.ReleaseMouseCapture();
            ((FrameworkElement)element).ClearValue(FrameworkElement.CursorProperty);
            e.Handled = moved; // a drag is not a click: no context menu after it
        };

        element.LostMouseCapture += (_, _) => dragging = false;
    }
}
