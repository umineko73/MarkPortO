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

namespace MarkPortO.App;

/// <summary>The product, its version, the author, the license and the link to GitHub; nothing more.</summary>
public partial class AboutWindow : Window
{
    private const string Url = "https://github.com/umineko73/MarkPortO";

    public static string Version { get; } = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = Version;
        AuthorText.Text = "umineko73";
        LicenseText.Text = "GPL-3.0-only";
        LinkText.Text = Url;
    }

    private void OnLinkClick(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true });

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();
}
