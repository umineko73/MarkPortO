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
using System.Windows;
using MarkPortO.App.Services;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

public partial class App : Application
{
    private static int _exitWatchdogStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The language and the theme are set before the first window is made, from the saved settings ("system": the OS).
        UiSettings ui;
        using (var saved = new SettingsStore(SettingsStore.DefaultPath))
        {
            ui = saved.Load().Settings.Ui;
        }

        LanguageService.Apply(ui.Language);
        ThemeService.Apply(ui.Theme);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartExitWatchdog();
        base.OnExit(e);

        // The Office programs that this program started are stopped now (not only when the process ends).
        MarkPortO.Core.Engines.OfficeEngine.StopStartedProcesses();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>
    /// Ends the process now, once the work of the program is done (the settings are written, the Office programs it started are stopped). The windows
    /// are not taken down one by one: the teardown of the graphics (and of WebView2) is where the process used to stop with a fast fail and stay alive
    /// while Windows Error Reporting worked. Nothing is left to do after this.
    /// </summary>
    internal static void ExitNow(int exitCode = 0)
    {
        try
        {
            MarkPortO.Core.Engines.OfficeEngine.StopStartedProcesses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        TerminateProcess(GetCurrentProcess(), (uint)exitCode);
        Environment.Exit(exitCode); // when TerminateProcess did not work
    }

    // Arm before closing the main window: owned windows, WebView2 and settings can block before OnExit is reached.
    // Call only once closing is accepted, after the conversion has had its time to stop.
    internal static void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref _exitWatchdogStarted, 1) != 0)
        {
            return;
        }

        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(8));
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true,
            Name = "MarkPortO exit watchdog",
        };
        watchdog.Start();

        // A second guard outside the process: the thread above cannot run when the runtime itself is stuck (a collection that waits for a thread that
        // does not come back, a crash that Windows Error Reporting is still handling). It ends at once when the process has ended.
        try
        {
            var id = Environment.ProcessId;
            var script = $"Wait-Process -Id {id} -Timeout 15 -ErrorAction SilentlyContinue; $p = Get-Process -Id {id} -ErrorAction SilentlyContinue; if ($p -and $p.ProcessName -eq 'MarkPortO') {{ $p.Kill() }}";
            var guard = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            };
            using var started = System.Diagnostics.Process.Start(guard);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
