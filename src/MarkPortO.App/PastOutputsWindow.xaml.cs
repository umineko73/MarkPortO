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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using MarkPortO.App.Services;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Pipeline;

namespace MarkPortO.App;

/// <summary>
/// The documents of the earlier runs in an output folder, one row for each file with its newest run (FR-119). A file that was converted more than
/// once has its other runs in the box below, to pick by the time. Only reads; the preview is opened by the caller.
/// </summary>
public partial class PastOutputsWindow : Window
{
    private readonly Action<PastDocument> _open;

    /// <param name="outputRoot">The folder that has <c>.markporto</c>.</param>
    /// <param name="documents">What <see cref="PastOutputs.Load"/> found, newest run first.</param>
    /// <param name="open">Opens the preview of a document.</param>
    public PastOutputsWindow(string outputRoot, IReadOnlyList<PastDocument> documents, Action<PastDocument> open)
    {
        InitializeComponent();
        _open = open;
        FolderText.Text = outputRoot;
        FolderText.ToolTip = outputRoot;

        // One row for each source file, with its runs, newest first (the list comes newest first).
        var rows = documents
            .GroupBy(d => d.Document.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Row([.. g]))
            .OrderByDescending(r => r.Latest.Job.UpdatedAt)
            .ToList();
        Grid.ItemsSource = rows;
        CountText.Text = rows.Count == 0 ? UiText.Get("Past_Empty") : UiText.Get("Past_Count", rows.Count);
        if (rows.Count > 0)
        {
            Grid.SelectedIndex = 0;
        }

        UpdateOpenButton();
    }

    private Row? SelectedRow => Grid.SelectedItem as Row;

    private PastDocument? Chosen => (Versions.SelectedItem as Version)?.Document;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Versions.ItemsSource = SelectedRow?.Versions;
        Versions.SelectedIndex = SelectedRow is null ? -1 : 0;
        // The box only matters for a file that was converted more than once.
        VersionsPanel.Visibility = SelectedRow is { Versions.Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        UpdateOpenButton();
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e) => UpdateOpenButton();

    private void UpdateOpenButton()
    {
        OpenButton.IsEnabled = Chosen is { CanPreview: true };
        OpenButton.ToolTip = Chosen switch
        {
            { FolderFound: false } => UiText.Get("Past_Missing"),
            { CanPreview: false } => UiText.Get("Past_NoPages"),
            _ => null,
        };
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindRow(source) is not null)
        {
            OnOpenClick(sender, e);
        }
    }

    private static DataGridRow? FindRow(DependencyObject? element)
    {
        while (element is not null and not DataGridRow)
        {
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return element as DataGridRow;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Chosen is { CanPreview: true } chosen)
        {
            _open(chosen);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static string DateOf(PastDocument d) => d.Job.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string StateOf(PastDocument d) =>
        d.FolderFound ? FileStatusProjector.Text(FileStatusProjector.StateOf(d.Document)) : UiText.Get("Past_Missing");

    private sealed record Version(PastDocument Document)
    {
        public string Text => DateOf(Document) + "  " + Path.GetFileName(Document.Document.OutputFolder) + "  " + StateOf(Document);
    }

    private sealed class Row(IReadOnlyList<PastDocument> runs)
    {
        public PastDocument Latest { get; } = runs[0];

        public IReadOnlyList<Version> Versions { get; } = [.. runs.Select(r => new Version(r))];

        public string Name => FileStatusProjector.Project(Latest.Document).FileName;

        public string DateText => DateOf(Latest);

        public string StateText => StateOf(Latest);

        public string Model => Latest.Document.VlmModel ?? string.Empty;

        public string Prompt => Latest.Document.VlmPrompt ?? string.Empty;
    }
}
