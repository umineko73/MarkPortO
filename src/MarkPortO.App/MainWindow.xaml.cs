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

using MarkPortO.App.Services;
using System.Windows;
using Microsoft.Win32;
using MarkPortO.App.ViewModels;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

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
            Confirm = text => MessageBox.Show(this, text, UiText.Get("Confirm_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
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

    private void OnInputSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedInputs(((System.Windows.Controls.ListBox)sender).SelectedItems.Cast<string>());

    private void OnThemeClick(object sender, RoutedEventArgs e) => _viewModel.SetTheme((string)((System.Windows.Controls.MenuItem)sender).Tag);

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
        MessageBox.Show(this, UiText.Get("About_Body", version), UiText.Get("About_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Opens the license of MarkPortO and the notices of the packages it uses; both are installed next to the program.</summary>
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

    // A dialog with its own ClientGuid keeps its own last folder, so the inputs and the output do not share one.
    private static readonly Guid InputFolderDialogId = new("6f1c2a54-3b7e-4d0a-9c35-5a1e0f7b2d11");
    private static readonly Guid OutputFolderDialogId = new("c3e8b9a7-14d2-4f6b-8a90-2e5d7c4b1f22");
    private static readonly Guid FilesDialogId = new("a92d5e03-7c18-4b4f-b6e1-0d3f8a6c9e33");

    private string? PickFolder(FolderPurpose purpose)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText.Get("Dialog_SelectFolder"),
            ClientGuid = purpose == FolderPurpose.Output ? OutputFolderDialogId : InputFolderDialogId,
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Dialog_SelectFiles"),
            Multiselect = true,
            Filter = UiText.Get("Dialog_Filter"),
            ClientGuid = FilesDialogId,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames : [];
    }
}
