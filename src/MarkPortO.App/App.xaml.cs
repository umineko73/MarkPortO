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
using MarkPortO.App.Services;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

public partial class App : Application
{
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
        base.OnExit(e);

        // The Office programs that this program started are stopped now (not only when the process ends).
        MarkPortO.Core.Engines.OfficeEngine.StopStartedProcesses();

        // The process ends by itself within a moment. If something holds it (a call that does not return), it is stopped after 8 seconds:
        // MarkPortO.exe is never left behind with no window. The thread is a background thread, so it does not hold the process.
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
    }
}
