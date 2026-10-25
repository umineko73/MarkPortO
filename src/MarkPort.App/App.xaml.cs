// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using System.Windows;
using MarkPort.App.Services;
using MarkPort.Core.Settings;

namespace MarkPort.App;

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
}
