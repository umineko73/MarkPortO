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
using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Pipeline;

namespace MarkPortO.App;

/// <summary>
/// The documents of the earlier runs in an output folder, one row for each file with its newest run (FR-119). A file that was converted more than
/// once has its runs in the box below, to pick by the time; the buttons act on the run that is chosen there (preview, open the folder in Explorer, open it on the main window). With several files
/// chosen, each file's newest run is used and there is no preview (FR-128). Only reads; the preview and the main window are the caller's.
/// </summary>
public partial class PastOutputsWindow : Window
{
    private readonly Action<PastDocument> _open;
    private readonly Func<IReadOnlyList<PastDocument>, Guid?, Task<bool>> _load;
    private readonly Dictionary<Guid, PreviousRun> _resumable;

    /// <param name="outputRoot">The folder that has <c>.markporto</c>.</param>
    /// <param name="documents">What <see cref="PastOutputs.Load"/> found, newest run first.</param>
    /// <param name="open">Opens the preview of a document.</param>
    /// <param name="resumable">The runs that can be resumed or retried (FR-121).</param>
    /// <param name="load">
    /// Puts the documents in the inputs and the file list of the main window, without converting; the second value is the run to resume, when
    /// one file of a run that can be resumed is chosen. False when that is not possible now (a conversion runs).
    /// </param>
    public PastOutputsWindow(
        string outputRoot,
        IReadOnlyList<PastDocument> documents,
        Action<PastDocument> open,
        IReadOnlyList<PreviousRun> resumable,
        Func<IReadOnlyList<PastDocument>, Guid?, Task<bool>> load)
    {
        InitializeComponent();
        _open = open;
        _load = load;
        _resumable = resumable.ToDictionary(r => r.Job.JobId);
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

        UpdateButtons();
    }

    private Row? SelectedRow => Grid.SelectedItems.Count == 1 ? Grid.SelectedItem as Row : null;

    // FR-128: several files are chosen; the folder and "open" act on the newest run of each.
    private IReadOnlyList<Row> SelectedRows => [.. Grid.SelectedItems.OfType<Row>()];

    private bool IsMultiple => Grid.SelectedItems.Count > 1;

    private PastDocument? Chosen => IsMultiple ? null : (Versions.SelectedItem as Version)?.Document;

    // The run that is chosen in the box can be loaded to resume it when it is one of the runs that can be resumed (FR-121).
    private PreviousRun? ChosenRun => Chosen is { } chosen && _resumable.TryGetValue(chosen.Job.JobId, out var run) ? run : null;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Versions.ItemsSource = SelectedRow?.Versions;
        Versions.SelectedIndex = SelectedRow is null ? -1 : 0;
        Versions.IsEnabled = !IsMultiple;
        UpdateButtons();
    }

    private void OnVersionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (IsMultiple)
        {
            // The preview is of one file; the folders are opened, and the files put on the main window, for each file.
            OpenButton.IsEnabled = false;
            OpenButton.ToolTip = UiText.Get("Past_PreviewOne");
            FolderButton.IsEnabled = SelectedRows.Any(r => r.Latest.FolderFound);
            LoadButton.IsEnabled = true;
            LoadButton.ToolTip = UiText.Get("Past_OpenTip");
            return;
        }

        OpenButton.IsEnabled = Chosen is { CanPreview: true };
        OpenButton.ToolTip = Chosen switch
        {
            { FolderFound: false } => UiText.Get("Past_Missing"),
            { CanPreview: false } => UiText.Get("Past_NoPages"),
            _ => null,
        };
        FolderButton.IsEnabled = Chosen is { FolderFound: true };
        LoadButton.IsEnabled = Chosen is not null;
        LoadButton.ToolTip = UiText.Get("Past_OpenTip") + (ChosenRun is { } run
            ? Environment.NewLine + UiText.Get("Past_RunCounts", run.DoneCount, run.FailedCount, run.UnfinishedCount)
                + (run.Job.Status == MarkPortO.Core.Jobs.Models.JobStatus.Abandoned ? "  " + UiText.Get("Past_Abandoned") : string.Empty)
            : string.Empty);
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

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        // Several files: the newest run of each (FR-128). One file: the run chosen in the box, and that run is resumed when it can be (FR-121).
        IReadOnlyList<PastDocument> documents = IsMultiple ? [.. SelectedRows.Select(r => r.Latest)] : Chosen is { } chosen ? [chosen] : [];
        if (documents.Count == 0)
        {
            return;
        }

        if (await _load(documents, IsMultiple ? RunToResume(documents) : ChosenRun?.Job.JobId))
        {
            Close();
        }
        else
        {
            MessageBox.Show(this, UiText.Get("Past_LoadFailed"), UiText.Get("Past_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // Several files: when one of them did not finish in a run that can be resumed, that run (the newest such) is the one "resume" acts on.
    private Guid? RunToResume(IReadOnlyList<PastDocument> documents) =>
        documents
            .Where(d => !d.Document.IsDone() && !d.Document.IsFailed())
            .Select(d => _resumable.TryGetValue(d.Job.JobId, out var run) ? run : null)
            .OfType<PreviousRun>()
            .Where(run => run.CanResume)
            .OrderByDescending(run => run.Job.UpdatedAt)
            .FirstOrDefault()?.Job.JobId;

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        var folders = IsMultiple
            ? SelectedRows.Where(r => r.Latest.FolderFound).Select(r => r.Latest.Document.OutputFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : Chosen is { FolderFound: true } chosen ? [chosen.Document.OutputFolder] : [];
        foreach (var folder in folders)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, ex.Message, UiText.Get("Past_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
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
