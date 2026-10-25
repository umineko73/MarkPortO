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

using MarkPort.App.Services;
using System.Windows;
using Microsoft.Win32;
using MarkPort.App.ViewModels;
using MarkPort.Core.Settings;

namespace MarkPort.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _store = new(SettingsStore.DefaultPath);

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(_store, Dispatcher)
        {
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            ShowConnectionSettings = ShowConnectionSettings,
            ShowPrompts = ShowPrompts,
        };
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Saves any setting that is still waiting to be written.
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e) => _viewModel.SetLanguage((string)((System.Windows.Controls.MenuItem)sender).Tag);


    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        MessageBox.Show(this, UiText.Get("About_Body", version), UiText.Get("About_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Opens the license of MarkPort and the notices of the packages it uses; both are installed next to the program.</summary>
    private void OnLicensesClick(object sender, RoutedEventArgs e)
    {
        foreach (var name in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.md" })
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
            if (!System.IO.File.Exists(path))
            {
                MessageBox.Show(this, UiText.Get("Licenses_NotFound", path), UiText.Get("About_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
    }

    private void ShowConnectionSettings()
    {
        // The window edits the same settings the main window uses, so its changes are saved and shown at once.
        new ConnectionSettingsWindow(_store) { Owner = this }.ShowDialog();
    }

    private void ShowPrompts() => new PromptsWindow(_store) { Owner = this }.ShowDialog();

    private string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("Dialog_SelectFolder") };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Dialog_SelectFiles"),
            Multiselect = true,
            Filter = UiText.Get("Dialog_Filter"),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }
}
