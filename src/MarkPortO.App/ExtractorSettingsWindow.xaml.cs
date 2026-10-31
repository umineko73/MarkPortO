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
using MarkPortO.App.ViewModels;
using MarkPortO.Core.Settings;
using Microsoft.Win32;

namespace MarkPortO.App;

public partial class ExtractorSettingsWindow : Window
{
    private readonly ExtractorSettingsViewModel _viewModel;

    public ExtractorSettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _viewModel = new ExtractorSettingsViewModel(store);
        DataContext = _viewModel;
    }

    /// <summary>A new extractor has a temporary name: put the cursor in the name box, selected, so the user can type the real one.</summary>
    private void OnAddClicked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);

    private void OnTemplateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = DataContext;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>Shows what the user agrees to, and records the consent only on Yes (FR-110).</summary>
    private void OnAgreeClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is not { } editor)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            editor.ConsentNotice + Environment.NewLine + Environment.NewLine + UiText.Get("Conn_ConsentQuestion"),
            UiText.Get("Ext_ConsentTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            editor.Agree();
        }
    }

    private void OnWithdrawClicked(object sender, RoutedEventArgs e) => _viewModel.Selected?.Withdraw();

    private async void OnTryClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is not { } editor)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = UiText.Get("Ext_TryPick"), Filter = UiText.Get("Ext_TryFilter") + "|*.docx;*.xlsx;*.pptx;*.pdf|*.*|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            await editor.TryAsync(dialog.FileName);
        }
    }
}
