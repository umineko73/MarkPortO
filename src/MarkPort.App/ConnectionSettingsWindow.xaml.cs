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
using MarkPort.App.ViewModels;
using MarkPort.Core.Settings;

namespace MarkPort.App;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ConnectionSettingsViewModel _viewModel;
    private bool _loading;

    public ConnectionSettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _viewModel = new ConnectionSettingsViewModel(store);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // The key box never shows a key: it is emptied whenever another connection is chosen.
            if (e.PropertyName == nameof(ConnectionSettingsViewModel.Selected))
            {
                _loading = true;
                KeyBox.Clear();
                _loading = false;
            }
        };
    }

    /// <summary>A new connection has a temporary name: put the cursor in the name box, selected, so the user can type the real one.</summary>
    private void OnAddClicked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && _viewModel.Selected is { } editor)
        {
            editor.SetApiKey(KeyBox.Password);
        }
    }
}
