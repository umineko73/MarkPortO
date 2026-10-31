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
        Title = "MarkPortO " + AboutWindow.Version;

        _viewModel = new MainViewModel(_store, Dispatcher)
        {
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            Confirm = text => MessageBox.Show(this, text, UiText.Get("Confirm_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
            ShowConnectionSettings = ShowConnectionSettings,
            ShowPrompts = ShowPrompts,
        };
        DataContext = _viewModel;

        // A value of the folded part was refused while it was closed: it is open now, and the item has the focus (FR-102).
        _viewModel.ConditionItemRejected += item => Dispatcher.BeginInvoke(() =>
        {
            System.Windows.Controls.Control? target = item switch
            {
                "dpi" => DpiBox,
                "engine" => EngineBox,
                "types" => DocxBox,
                _ => null,
            };
            target?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private bool _closeConfirmed;

    // Closing in the middle of a conversion stops it first and waits (up to 40 seconds) until Office and the job are closed; the
    // window does not go before that. If Office is stuck, it is stopped when the program ends (OfficeEngine).
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !_viewModel.IsRunning)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        await _viewModel.StopAsync(TimeSpan.FromSeconds(40));
        _closeConfirmed = true;
        Close();
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

    // FR-068: the preview of the file that is chosen in the list. Each file has its own window.
    private readonly Dictionary<string, PreviewWindow> _previews = new(StringComparer.OrdinalIgnoreCase);

    private void OnFileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement((System.Windows.Controls.DataGrid)sender, source) is System.Windows.Controls.DataGridRow)
        {
            OnPreviewClick(sender, e);
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is not { CanPreview: true } item)
        {
            return;
        }

        if (_previews.TryGetValue(item.FullPath, out var open) && open.IsLoaded)
        {
            open.Activate();
            return;
        }

        var window = new PreviewWindow(item) { Owner = this };
        _previews[item.FullPath] = window;
        window.Closed += (_, _) => _previews.Remove(item.FullPath);
        window.Show();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_viewModel.IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropInputs(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.DropInputs(paths);
        }

        e.Handled = true;
    }

    private void OnDropOutput(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.DropOutput(paths);
        }

        e.Handled = true;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e) => _viewModel.SetTheme((string)((System.Windows.Controls.MenuItem)sender).Tag);

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
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
