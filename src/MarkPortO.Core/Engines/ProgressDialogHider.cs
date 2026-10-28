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

using System.Runtime.InteropServices;

namespace MarkPortO.Core.Engines;

/// <summary>
/// Hides the progress dialog that Excel shows while it writes a PDF ("Publishing..."; FR-062). Excel shows it however the
/// application is set up (no screen updating, no interaction), so the dialog is hidden as soon as it appears. Only windows of
/// the Excel processes that this program started are touched. That Excel is not visible, so any window of it that can be seen is a dialog.
/// </summary>
internal sealed class ProgressDialogHider : IDisposable
{
    private const int SwHide = 0;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    private readonly HashSet<uint> _processIds;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public ProgressDialogHider(IEnumerable<int> processIds)
    {
        _processIds = [.. processIds.Select(id => (uint)id)];
        _thread = new Thread(Run) { IsBackground = true, Name = "ProgressDialogHider" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            EnumWindows(HideIfDialog, IntPtr.Zero);
            _stop.Token.WaitHandle.WaitOne(Interval);
        }
    }

    private bool HideIfDialog(IntPtr window, IntPtr parameter)
    {
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            if (_processIds.Contains(processId) && IsWindowVisible(window))
            {
                ShowWindow(window, SwHide);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // a window that went away while it was looked at
        }

        return true;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
