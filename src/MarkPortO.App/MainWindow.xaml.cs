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
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _store = new(SettingsStore.DefaultPath);

    public MainWindow()
    {
        InitializeComponent();
        // The default is tall enough for the list of pages under the conditions; a small screen gets what it has.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Title = "MarkPortO " + AboutWindow.Version;

        _viewModel = new MainViewModel(_store, Dispatcher)
        {
            PickFolder = PickFolder,
            PickFiles = PickFiles,
            Confirm = text => MessageBox.Show(this, text, UiText.Get("Confirm_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
            ConfirmAdd = text => MessageBox.Show(this, text, UiText.Get("Confirm_AddTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK,
            ShowStopped = text => MessageBox.Show(this, text, UiText.Get("Stopped_Title"), MessageBoxButton.OK, MessageBoxImage.Warning),
            ShowConnectionSettings = ShowConnectionSettings,
            ShowExtractorSettings = ShowExtractorSettings,
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
            if (!e.Cancel)
            {
                App.StartExitWatchdog();
            }

            return;
        }

        e.Cancel = true;
        if (MessageBox.Show(this, UiText.Get("Close_RunningConfirm"), UiText.Get("Close_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

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

        // Nothing is left to do: the process ends here, without the teardown of the graphics (see App.ExitNow).
        App.ExitNow();
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e) => _viewModel.SetLanguage((string)((System.Windows.Controls.MenuItem)sender).Tag);

    private void OnInputSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedInputs(((System.Windows.Controls.ListBox)sender).SelectedItems.Cast<string>());

    // FR-068: the preview of the file that is chosen in the list. Each file has its own window.
    private readonly Dictionary<string, PreviewWindow> _previews = new(StringComparer.OrdinalIgnoreCase);

    // FR-138: every page of the files chosen in the list is marked to be read again by the VLM, or the marks are taken off.
    private void OnRereadMarkClick(object sender, RoutedEventArgs e) => SetRereadMarks(marked: true);

    private void OnRereadUnmarkClick(object sender, RoutedEventArgs e) => SetRereadMarks(marked: false);

    private void SetRereadMarks(bool marked)
    {
        foreach (var item in FileGrid.SelectedItems.OfType<FileItemViewModel>().Where(i => i.CanReread))
        {
            if (item.Source is { } document)
            {
                _viewModel.SetRereadMarks(document, null, marked);
            }
        }
    }

    private void OnFileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement((System.Windows.Controls.DataGrid)sender, source) is System.Windows.Controls.DataGridRow)
        {
            OnPreviewClick(sender, e);
        }
    }

    // The folder and the Markdown files of the chosen document: the shell opens them (the folder in Explorer, a file in the program set for .md).
    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenWithShell(_viewModel.Selected is { HasOutputFolder: true } item ? item.OutputFolderPath : null);

    // FR-127: the files chosen in the list are converted again, into the folders of their earlier conversion.
    private async void OnReconvertClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ReconvertAsync([.. FileGrid.SelectedItems.OfType<FileItemViewModel>()]);

    private void OnOpenMarkdownClick(object sender, RoutedEventArgs e) => OpenWithShell(_viewModel.Selected is { HasMarkdown: true } item ? item.MarkdownPath : null);

    private void OnOpenVlmClick(object sender, RoutedEventArgs e) => OpenWithShell(_viewModel.Selected is { HasVlm: true } item ? item.VlmPath : null);

    private void OpenWithShell(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, UiText.Get("About_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPageRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGridRow row)
        {
            PageGrid.SelectedItem = row.Item;
            row.Focus();
        }
    }

    private void OnPagePreviewClick(object sender, RoutedEventArgs e)
    {
        if (PageGrid.SelectedItem is PageRow page)
        {
            OpenPreview(page.Index);
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e) => OpenPreview(null);

    private void OpenPreview(int? pageIndex)
    {
        if (_viewModel.Selected is not { CanPreview: true } item)
        {
            return;
        }

        if (_previews.TryGetValue(item.FullPath, out var open) && open.IsLoaded)
        {
            if (pageIndex is int page)
            {
                open.GoToPage(page);
            }

            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new PreviewWindow(item, _viewModel) { Owner = this };
        if (pageIndex is int initialPage)
        {
            window.GoToPage(initialPage);
        }

        var path = item.FullPath;
        _previews[path] = window;
        // The window can go to another file (FR-137): it is then the preview of that file.
        window.ItemSwitched += (from, to) =>
        {
            _previews.Remove(from.FullPath);
            path = to.FullPath;
            _previews[path] = window;
        };
        window.Closed += (_, _) => _previews.Remove(path);
        window.Show();
    }

    // FR-119: the preview of an output of an earlier run, found from its folder. The windows are not tied to the conversion.
    private void OnOpenPastClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("Dialog_SelectFolder"), ClientGuid = PastFolderDialogId };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var root = PastOutputs.FindRoot(dialog.FolderName);
        if (root is null)
        {
            // No .markporto (the folders were copied, or their state removed): the conversion.json of each document folder (FR-155).
            // They can be previewed and opened as a new conversion; not resumed.
            var chosen = dialog.FolderName;
            var records = PastOutputs.LoadRecords(chosen);
            if (records.Count == 0)
            {
                MessageBox.Show(this, UiText.Get("Past_NotOutput"), UiText.Get("Past_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            new PastOutputsWindow(
                chosen,
                records,
                OpenPastPreview,
                [],
                (documents, _) => _viewModel.OpenPastAsync(chosen, [.. documents.Select(d => d.Document)], null))
            { Owner = this }.Show();
            return;
        }

        new PastOutputsWindow(
            root,
            PastOutputs.Load(root),
            OpenPastPreview,
            PreviousRunFinder.Resumable(root),
            (documents, resumeJobId) => _viewModel.OpenPastAsync(root, [.. documents.Select(d => d.Document)], resumeJobId))
        { Owner = this }.Show();
    }

    private void OpenPastPreview(PastDocument past)
    {
        var key = past.Document.OutputFolder;
        if (_previews.TryGetValue(key, out var open) && open.IsLoaded)
        {
            open.Activate();
            return;
        }

        var window = new PreviewWindow(new FileItemViewModel(FileStatusProjector.Project(past.Document)), _viewModel) { Owner = this };
        _previews[key] = window;
        window.Closed += (_, _) => _previews.Remove(key);
        window.Show();
    }

    private static readonly Guid PastFolderDialogId = new("5b7d1e90-2c4a-4f68-9d13-8e0a6c3f4b57");

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

    // ---- work sets (FR-159) and the history of output folders (FR-156) ----

    private void OnSaveWorkSetClick(object sender, RoutedEventArgs e)
    {
        var name = AskName(UiText.Get("WorkSet_SaveTitle"), string.Empty, null, _viewModel.WorkSetExists);
        if (name is null)
        {
            return;
        }

        if (_viewModel.WorkSetExists(name)
            && MessageBox.Show(this, UiText.Get("WorkSet_Overwrite", name), UiText.Get("WorkSet_SaveTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _viewModel.SaveWorkSet(name);
    }

    /// <summary>The submenu lists the work sets, those opened lately first.</summary>
    private void OnWorkSetsMenuOpened(object sender, RoutedEventArgs e)
    {
        WorkSetsMenu.Items.Clear();
        var names = _viewModel.WorkSetNames;
        if (names.Count == 0)
        {
            WorkSetsMenu.Items.Add(new System.Windows.Controls.MenuItem { Header = UiText.Get("Menu_WorkSetNone"), IsEnabled = false });
            return;
        }

        foreach (var name in names)
        {
            var item = new System.Windows.Controls.MenuItem { Header = name.Replace("_", "__", StringComparison.Ordinal) };
            item.Click += (_, _) => _viewModel.OpenWorkSet(name);
            WorkSetsMenu.Items.Add(item);
        }
    }

    private void OnManageWorkSetsClick(object sender, RoutedEventArgs e)
    {
        var list = new System.Windows.Controls.ListBox { MinHeight = 160, MinWidth = 320, Margin = new Thickness(0, 0, 0, 8) };
        var rename = new System.Windows.Controls.Button { Content = UiText.Get("Preset_Rename"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var delete = new System.Windows.Controls.Button { Content = UiText.Get("Preset_Delete"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var close = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Close"), Padding = new Thickness(16, 3, 16, 3), IsCancel = true, IsDefault = true };
        var empty = new System.Windows.Controls.TextBlock { Text = UiText.Get("Menu_WorkSetNone"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { rename, delete, close } };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12), Children = { empty, list, buttons } };
        var dialog = new Window
        {
            Title = UiText.Get("WorkSet_ManageTitle"),
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        void Reload()
        {
            list.ItemsSource = _viewModel.WorkSetNames;
            empty.Visibility = _viewModel.WorkSetNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            rename.IsEnabled = delete.IsEnabled = list.SelectedItem is not null;
        }

        list.SelectionChanged += (_, _) => rename.IsEnabled = delete.IsEnabled = list.SelectedItem is not null;
        rename.Click += (_, _) =>
        {
            if (list.SelectedItem is not string old || AskName(UiText.Get("Preset_Rename"), old, old, _viewModel.WorkSetExists) is not { } name || name == old)
            {
                return;
            }

            _viewModel.RenameWorkSet(old, name);
            Reload();
        };
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is not string name
                || MessageBox.Show(dialog, UiText.Get("WorkSet_DeleteConfirm", name), UiText.Get("Preset_Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            _viewModel.DeleteWorkSet(name);
            Reload();
        };
        Reload();
        dialog.ShowDialog();
    }

    /// <summary>A row of the list of the history of output folders.</summary>
    private sealed record OutputHistoryRow(string Input, string Output, string At, string State, bool CanOpen);

    /// <summary>
    /// The list of the output folders remembered for the inputs (FR-156): what they are used for, and they can be forgotten one by one or all
    /// (the results of the conversions stay).
    /// </summary>
    private void OnOutputHistoryClick(object sender, RoutedEventArgs e)
    {
        var grid = new System.Windows.Controls.DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            SelectionMode = System.Windows.Controls.DataGridSelectionMode.Extended,
            SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow,
            HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
            MinHeight = 220,
            MaxHeight = 480,
            Width = 860,
            Margin = new Thickness(0, 0, 0, 8),
        };
        foreach (var (header, path, width, tip) in new[]
        {
            (UiText.Get("History_ColumnInput"), nameof(OutputHistoryRow.Input), 300.0, (string?)null),
            (UiText.Get("History_ColumnOutput"), nameof(OutputHistoryRow.Output), 280.0, null),
            (UiText.Get("History_ColumnAt"), nameof(OutputHistoryRow.At), 130.0, null),
            (UiText.Get("History_ColumnState"), nameof(OutputHistoryRow.State), 130.0, UiText.Get("History_ColumnStateTip")),
        })
        {
            grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
            {
                // The meaning of the states is in the tip of the header of their column (the line above the list is short).
                Header = tip is null ? header : new System.Windows.Controls.TextBlock { Text = header, ToolTip = tip },
                Binding = new System.Windows.Data.Binding(path),
                Width = new System.Windows.Controls.DataGridLength(width),
            });
        }

        var about = new System.Windows.Controls.TextBlock { Text = UiText.Get("History_About"), TextWrapping = TextWrapping.Wrap, Width = 860, Margin = new Thickness(0, 0, 0, 8) };
        var empty = new System.Windows.Controls.TextBlock { Text = UiText.Get("History_Empty"), Margin = new Thickness(0, 0, 0, 8) };
        var open = new System.Windows.Controls.Button { Content = UiText.Get("History_Open"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 18, 0) };
        var forget = new System.Windows.Controls.Button { Content = UiText.Get("History_Forget"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var clear = new System.Windows.Controls.Button { Content = UiText.Get("History_ForgetAll"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var close = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Close"), Padding = new Thickness(16, 3, 16, 3), IsCancel = true, IsDefault = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { open, forget, clear, close } };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12), Children = { about, empty, grid, buttons } };
        var dialog = new Window
        {
            Title = UiText.Get("History_Title"),
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        void Reload()
        {
            var rows = _viewModel.OutputHistoryStates().Select(h => new OutputHistoryRow(
                h.Memory.Input,
                h.Memory.Output,
                h.Memory.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                h.State switch
                {
                    MainViewModel.HistoryState.InputGone => UiText.Get("History_InputGone"),
                    MainViewModel.HistoryState.OutputGone => UiText.Get("History_OutputGone"),
                    MainViewModel.HistoryState.NoRecord => UiText.Get("History_NoRecord"),
                    _ => string.Empty,
                },
                h.State is MainViewModel.HistoryState.Ready or MainViewModel.HistoryState.NoRecord)).ToList();
            grid.ItemsSource = rows;
            empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            grid.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            clear.IsEnabled = rows.Count > 0;
            forget.IsEnabled = grid.SelectedItems.Count > 0;
            open.IsEnabled = CanOpenSelected();
        }

        bool CanOpenSelected() => grid.SelectedItems.Count == 1 && grid.SelectedItem is OutputHistoryRow { CanOpen: true };

        // The screen goes back to the input chosen and its output folder; the list closes.
        void Open()
        {
            if (!CanOpenSelected() || grid.SelectedItem is not OutputHistoryRow row)
            {
                return;
            }

            if (!_viewModel.OpenFromHistory(row.Input, row.Output))
            {
                var gone = !(System.IO.File.Exists(row.Input) || System.IO.Directory.Exists(row.Input)) ? row.Input : row.Output;
                MessageBox.Show(dialog, UiText.Get("History_OpenGone", gone), UiText.Get("History_Open"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            dialog.Close();
        }

        grid.SelectionChanged += (_, _) =>
        {
            forget.IsEnabled = grid.SelectedItems.Count > 0;
            open.IsEnabled = CanOpenSelected();
        };
        grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement(grid, source) is System.Windows.Controls.DataGridRow)
            {
                Open();
            }
        };
        open.Click += (_, _) => Open();
        forget.Click += (_, _) =>
        {
            var inputs = grid.SelectedItems.OfType<OutputHistoryRow>().Select(r => r.Input).ToList();
            if (inputs.Count > 0)
            {
                _viewModel.ForgetOutputs(inputs);
                Reload();
            }
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(dialog, UiText.Get("History_ClearConfirm"), UiText.Get("History_ForgetAll"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                _viewModel.ClearHistory();
                Reload();
            }
        };
        Reload();
        dialog.ShowDialog();
    }

    /// <summary>A name asked in a small window (a work set); null when cancelled. <paramref name="current"/>: the name being renamed.</summary>
    private string? AskName(string title, string initial, string? current, Func<string, bool> exists)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 0, 0, 6) };
        var error = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 6) };
        var ok = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Ok"), IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Cancel"), IsCancel = true, Width = 80 };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12), Children = { new System.Windows.Controls.TextBlock { Text = UiText.Get("Preset_NameLabel"), Margin = new Thickness(0, 0, 0, 4) }, box, error, buttons } };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (name.Length == 0 || name.Contains('\n', StringComparison.Ordinal))
            {
                error.Text = UiText.Get("WorkSet_NameInvalid");
            }
            else if (current is not null && !string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && exists(name))
            {
                error.Text = UiText.Get("Prompts_NameDuplicate");
            }
            else
            {
                result = name;
                dialog.DialogResult = true;
            }
        };
        box.SelectAll();
        dialog.Loaded += (_, _) => box.Focus();
        dialog.ShowDialog();
        return result;
    }

    // ---- presets of the conversion (FR-151) ----

    /// <summary>Saves the settings now as a preset of the user, under a name that is asked.</summary>
    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var name = AskPresetName(UiText.Get("Preset_SaveTitle"), string.Empty, null);
        if (name is null)
        {
            return;
        }

        if (_viewModel.UserPresetNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            && MessageBox.Show(this, UiText.Get("Preset_Overwrite", name), UiText.Get("Preset_SaveTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _viewModel.SavePreset(name);
    }

    /// <summary>A small window with the presets of the user: rename and delete. The built-in ones are not in it.</summary>
    private void OnManagePresetsClick(object sender, RoutedEventArgs e)
    {
        var list = new System.Windows.Controls.ListBox { MinHeight = 160, MinWidth = 320, Margin = new Thickness(0, 0, 0, 8) };
        var rename = new System.Windows.Controls.Button { Content = UiText.Get("Preset_Rename"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var delete = new System.Windows.Controls.Button { Content = UiText.Get("Preset_Delete"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        var close = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Close"), Padding = new Thickness(16, 3, 16, 3), IsCancel = true, IsDefault = true };
        var empty = new System.Windows.Controls.TextBlock { Text = UiText.Get("Preset_None"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { rename, delete, close } };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12), Children = { empty, list, buttons } };
        var dialog = new Window
        {
            Title = UiText.Get("Preset_ManageTitle"),
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        void Reload()
        {
            list.ItemsSource = _viewModel.UserPresetNames;
            empty.Visibility = _viewModel.UserPresetNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            rename.IsEnabled = delete.IsEnabled = list.SelectedItem is not null;
        }

        list.SelectionChanged += (_, _) => rename.IsEnabled = delete.IsEnabled = list.SelectedItem is not null;
        rename.Click += (_, _) =>
        {
            if (list.SelectedItem is not string old || AskPresetName(UiText.Get("Preset_Rename"), old, old) is not { } name || name == old)
            {
                return;
            }

            _viewModel.RenamePreset(old, name);
            Reload();
        };
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is not string name
                || MessageBox.Show(dialog, UiText.Get("Preset_DeleteConfirm", name), UiText.Get("Preset_Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            _viewModel.DeletePreset(name);
            Reload();
        };
        Reload();
        dialog.ShowDialog();
    }

    /// <summary>A name for a preset of the user, asked in a small window; null when it is cancelled. A built-in name or another preset's name (when renaming) is refused.</summary>
    private string? AskPresetName(string title, string initial, string? current)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 0, 0, 6) };
        var error = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 6) };
        var ok = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Ok"), IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = UiText.Get("Prompts_Cancel"), IsCancel = true, Width = 80 };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12), Children = { new System.Windows.Controls.TextBlock { Text = UiText.Get("Preset_NameLabel"), Margin = new Thickness(0, 0, 0, 4) }, box, error, buttons } };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (!ConversionPreset.IsValidUserName(name))
            {
                error.Text = UiText.Get("Preset_NameInvalid");
            }
            else if (current is not null && !string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && _viewModel.PresetExists(name))
            {
                error.Text = UiText.Get("Prompts_NameDuplicate");
            }
            else
            {
                result = name;
                dialog.DialogResult = true;
            }
        };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? result : null;
    }

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

    private void ShowExtractorSettings() => new ExtractorSettingsWindow(_store) { Owner = this }.ShowDialog();

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
