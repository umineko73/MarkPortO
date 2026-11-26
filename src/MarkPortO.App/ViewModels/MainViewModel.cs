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
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.App.ViewModels;

/// <summary>What a folder dialog is for; each purpose remembers the folder it was last opened in on its own.</summary>
public enum FolderPurpose
{
    Input,
    Output,
}

/// <summary>
/// The main window: what to convert, where to put it, and the list of files with the state of each. It only
/// collects the user's choices and calls <see cref="ConvertService"/>, like the command line does (constitution II).
/// Every value the user sets is written to settings.json at once and comes back at the next start (FR-018).
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cancellation;
    private string _status = string.Empty;
    private Func<string> _statusSource = () => string.Empty; // makes the text again when the language changes
    private bool _running;
    private bool _refreshingFiles;
    private string _dpiText;
    private string _pageRangeText = string.Empty;
    private FileItemViewModel? _selected;
    private PreviousRun? _previous;

    /// <summary>The files of the inputs, each with its last record in the output folder (FR-157); empty when not looked for.</summary>
    private IReadOnlyList<FileEntry> _entries = [];
    private int _scanVersion;

    // The inputs are being set together with the list of files (a past output is opened): the change of the inputs does not refill the list.
    private bool _keepList;

    // FR-156・FR-158: the first input and output folder whose conditions are on the screen (null: none); a work set stops both (FR-159)
    // until the inputs are changed.
    private string? _firstKey;
    private bool _restoreSuspended;

    // A note for the status that the next search of the states shows with its own text (FR-160, FR-159).
    private string? _pendingNote;

    // FR-159: the work set that was opened, and its conditions, until the inputs are changed.
    private string? _workSetName;
    private string? _workSetConditions;

    public MainViewModel(SettingsStore store, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;

        var loaded = store.Load();
        var messages = loaded.Warnings.Concat(loaded.ValidationErrors).ToList();
        _statusSource = () => messages.Count > 0 ? string.Join(" / ", messages) : UiText.Get("Status_Ready");

        var settings = store.Current;
        InputPaths = new ObservableCollection<string>(settings.Input.Paths);
        _dpiText = settings.Images.Dpi.ToString(CultureInfo.InvariantCulture);
        if (DetectEngines() is { } engineNote)
        {
            var before = _statusSource;
            _statusSource = () => messages.Count > 0 ? $"{before()} / {engineNote()}" : engineNote();
            ConditionsExpanded = true; // the engine is in the folded part: the message is about it
        }

        _status = _statusSource();

        // The line of the folded part is made again when one of its items changes (FR-102).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Recurse) or nameof(FolderLayoutIndex) or nameof(TypeDocx) or nameof(TypeXlsx) or nameof(TypePptx) or nameof(TypePdf)
                or nameof(SelectedEngine) or nameof(DpiText) or nameof(Emphasis) or nameof(ExtractPictures) or nameof(ExtractShapes) or nameof(GroupNearShapes)
                or nameof(ExtractorDocx) or nameof(ExtractorXlsx) or nameof(ExtractorPptx) or nameof(ExtractorPdf)
                or nameof(IncludeHiddenSheets) or nameof(IncludeHiddenSlides) or nameof(IncludeOffSlideText) or nameof(UsePdfText)
                or nameof(ExcelColumns) or nameof(ExcelSheet) or nameof(ExcelPrintArea)
                or nameof(PageImageIndex) or nameof(Step2) or nameof(Step3) or nameof(VlmSeparate) or nameof(Step1TablesIndex) or nameof(DropRepeatedTables) or nameof(StructureNote) or nameof(Chunks) or nameof(ExcelPerSheet))
            {
                Raise(nameof(ConditionSummaryText));
            }

            // Whether the settings still have the values of the preset is worked out again after any change (FR-151).
            if (e.PropertyName is not (nameof(PresetStateText) or nameof(Status) or nameof(FinishedCount) or nameof(ProgressText) or nameof(ProgressVisibility)))
            {
                Raise(nameof(PresetStateText));
            }
        };

        // The four ways to convert; the list of files decides which can be pressed (FR-129):
        // empty: "Start". Something not finished: "Resume" and "Redo all". Everything done: "Redo all" and "Changed files only".
        // FR-157: "Start" when the list is empty or no file has been converted; "Redo all" otherwise.
        StartCommand = new RelayCommand(StartAllAsync, () => CanStart() && (Files.Count == 0 || ListIsNotConverted));
        ResumeCommand = new RelayCommand(
            () => FileStates.ToResume(_entries).Count > 0
                ? ResumeFilesAsync()
                : ResumeAsync(_previous is { CanResume: true } ? ResumeMode.Unfinished : ResumeMode.RetryFailed),
            () => !IsRunning && HasPrevious);
        RedoAllCommand = new RelayCommand(StartAllAsync, () => CanStart() && Files.Count > 0 && !ListIsNotConverted);
        StartChangedCommand = new RelayCommand(
            () => StartAsync(RunKind.ChangedOnly),
            () => CanStart() && (_entries.Count > 0 ? _entries.Any(e => e.State == FileState.Changed) : Files.Count > 0 && Files.All(f => f.IsDone)));
        RereadCommand = new RelayCommand(() => RereadAsync(), () => CanReread);
        InterruptCommand = new RelayCommand(InterruptAsync, () => IsRunning);
        AddFilesCommand = new RelayCommand(AddFilesAsync, () => !IsRunning);
        AddFolderCommand = new RelayCommand(AddFolderAsync, () => !IsRunning);
        ClearInputsCommand = new RelayCommand(ClearInputsAsync, () => !IsRunning);
        RemoveSelectedInputsCommand = new RelayCommand(RemoveSelectedInputsAsync, () => !IsRunning && SelectedInputPaths.Count > 0);
        BrowseOutputCommand = new RelayCommand(BrowseOutputAsync, () => !IsRunning);
        ConnectionSettingsCommand = new RelayCommand(OpenConnectionSettingsAsync, () => !IsRunning);
        ExtractorSettingsCommand = new RelayCommand(OpenExtractorSettingsAsync, () => !IsRunning);
        PromptsCommand = new RelayCommand(() => { ShowPrompts?.Invoke(); RefreshPrompts(); return Task.CompletedTask; }, () => !IsRunning);
        PromptChoices = [.. new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf }.Select(k => new PromptChoiceViewModel(k, _store))];

        // FR-130: the list of files shows the earlier conversion of the inputs of the screen, so a change of the inputs looks for it again
        // (the list empties when the output folder has no conversion of the new inputs).
        InputPaths.CollectionChanged += (_, _) =>
        {
            UpdateRunCommands();
            if (!_keepList)
            {
                _restoreSuspended = false;
                _workSetName = null;
                _ = RefreshPreviousAsync(showFiles: true, inputsChanged: true);
            }
        };
        Files.CollectionChanged += (_, _) =>
        {
            UpdateRunCommands();
            RaiseProgress();
        };
        foreach (var choice in PromptChoices)
        {
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PromptChoiceViewModel.SelectedIndex))
                {
                    RaiseRereadState();
                }
            };
        }
        _ = RefreshPreviousAsync(showFiles: true, appendToStatus: true, inputsChanged: true); // the earlier state of the inputs (FR-156〜158)
    }

    /// <summary>Opens the window for VLM connections (set by the window).</summary>
    public Action? ShowConnectionSettings { get; set; }

    public Action? ShowExtractorSettings { get; set; }

    public Action? ShowPrompts { get; set; }

    /// <summary>The choice of the prompt for each kind of file (FR-115).</summary>
    public IReadOnlyList<PromptChoiceViewModel> PromptChoices { get; } = [];

    /// <summary>The settings of Step 3 are shown while Step 3 is on (FR-102): they do nothing when it is off.</summary>
    public System.Windows.Visibility Step3DetailsVisibility => Step3 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>The page range is shown for one file only (FR-097).</summary>
    public System.Windows.Visibility PagesVisibility => Step3DetailsVisibility == System.Windows.Visibility.Visible && IsOneFile
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // ---- the connection of Step 3, chosen in a drop-down list ----

    /// <summary>The names of the connections of the settings, after the item of no connection.</summary>
    public IReadOnlyList<string> ConnectionItems => [UiText.Get("Conn_None"), .. _store.Current.Vlm.Connections.Select(c => c.Name)];

    /// <summary>The connection in use: 0 is none.</summary>
    public int ConnectionIndex
    {
        get
        {
            var index = _store.Current.Vlm.Connections.FindIndex(c => string.Equals(c.Name, _store.Current.Vlm.Selected, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index + 1;
        }

        set
        {
            var connections = _store.Current.Vlm.Connections;
            if (value < 0 || value > connections.Count)
            {
                return; // the list was replaced and nothing is chosen yet
            }

            var name = value == 0 ? string.Empty : connections[value - 1].Name;
            if (!string.Equals(name, _store.Current.Vlm.Selected, StringComparison.Ordinal))
            {
                _store.Update(s => s.Vlm.Selected = name);
                RaiseConnection();
            }
        }
    }

    private void RaiseConnection()
    {
        Raise(nameof(ConnectionInUse));
        Raise(nameof(ConnectionItems));
        Raise(nameof(ConnectionIndex));
        RaiseRereadState();
    }

    /// <summary>The prompts were changed in their window: the choices are shown again.</summary>
    public void RefreshPrompts()
    {
        foreach (var choice in PromptChoices)
        {
            choice.Refresh();
        }

        RaiseRereadState();
        Raise(nameof(PresetStateText));
    }

    // ---- presets of the conversion (FR-151) ----

    /// <summary>One item of the list of presets: the name that is saved, and the name that is shown. A rename changes the item, not the list.</summary>
    public sealed class PresetItem(string name, string display) : ObservableObject
    {
        public string Name { get; private set; } = name;

        public string Display { get; private set; } = display;

        /// <summary>The name that is shown: what a screen reader says for the item, and the text of the closed drop-down list.</summary>
        public override string ToString() => Display;

        internal void Change(string name, string display)
        {
            if (Name != name)
            {
                Name = name;
                Raise(nameof(Name));
            }

            if (Display != display)
            {
                Display = display;
                Raise(nameof(Display));
            }
        }
    }

    /// <summary>"Custom" (no preset), the built-in presets, then the presets of the user.</summary>
    /// <remarks>
    /// One list, whose items are changed in place (<see cref="SyncPresetItems"/>). A new list each time made the drop-down list choose again, which set
    /// <see cref="PresetIndex"/> again and again until the stack overflowed when "Custom" was chosen; and a replaced list lost the choice after a rename.
    /// </remarks>
    public ObservableCollection<PresetItem> PresetItems
    {
        get
        {
            if (_presetItems.Count == 0)
            {
                SyncPresetItems();
            }

            return _presetItems;
        }
    }

    private readonly ObservableCollection<PresetItem> _presetItems = [];
    private bool _applyingPreset;

    /// <summary>
    /// Makes the items of the list say the presets now and the words of the screen now. An item is kept while its preset is there (by its name), so the
    /// drop-down list keeps its choice: only the item of a deleted preset goes, and a new one is added. A rename changes its item first (<see cref="RenamePreset"/>).
    /// </summary>
    private void SyncPresetItems()
    {
        (string Name, string Display)[] fixedItems =
        [
            (string.Empty, UiText.Get("Preset_Custom")),
            .. ConversionPreset.BuiltIn.Select(p => (p.Name, ConversionPreset.DisplayName(p.Name))),
        ];
        for (var i = 0; i < fixedItems.Length; i++)
        {
            if (i < _presetItems.Count)
            {
                _presetItems[i].Change(fixedItems[i].Name, fixedItems[i].Display);
            }
            else
            {
                _presetItems.Add(new PresetItem(fixedItems[i].Name, fixedItems[i].Display));
            }
        }

        var users = _store.Current.Presets.Select(p => p.Name).ToList();
        for (var i = _presetItems.Count - 1; i >= fixedItems.Length; i--)
        {
            if (!users.Contains(_presetItems[i].Name, StringComparer.OrdinalIgnoreCase))
            {
                _presetItems.RemoveAt(i);
            }
        }

        for (var k = 0; k < users.Count; k++)
        {
            var at = fixedItems.Length + k;
            var found = -1;
            for (var i = fixedItems.Length; i < _presetItems.Count; i++)
            {
                if (string.Equals(_presetItems[i].Name, users[k], StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                _presetItems.Insert(at, new PresetItem(users[k], users[k]));
                continue;
            }

            if (found != at)
            {
                _presetItems.Move(found, at);
            }

            _presetItems[at].Change(users[k], users[k]); // the same name in other capitals
        }
    }

    /// <summary>The chosen preset in <see cref="PresetItems"/>. Choosing one puts its values into the settings; "Custom" changes nothing.</summary>
    public int PresetIndex
    {
        get
        {
            var items = PresetItems;
            var index = items.ToList().FindIndex(i => i.Name.Length > 0 && string.Equals(i.Name, _store.Current.PresetSelected, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index;
        }

        set
        {
            var items = PresetItems;
            if (_applyingPreset || value < 0 || value >= items.Count)
            {
                return; // the drop-down list chooses again while the settings are shown again, or the list was replaced
            }

            if (value == 0)
            {
                // "Custom": the settings stay as they are; only the name of the preset is forgotten.
                if (_store.Current.PresetSelected.Length > 0)
                {
                    _store.Update(s => s.PresetSelected = string.Empty);
                    RaisePresetChoice();
                }

                return;
            }

            if (value == PresetIndex && !IsPresetModified)
            {
                return;
            }

            ApplyPreset(items[value].Name);
        }
    }

    /// <summary>
    /// The chosen item, for the drop-down list. When the list is replaced (a preset saved, renamed or deleted), the drop-down list is given the item of
    /// the new list: bound to the place or to the name, it kept a choice that did not change and showed nothing.
    /// </summary>
    public PresetItem? SelectedPreset
    {
        get => PresetItems[PresetIndex];
        set
        {
            if (value is null)
            {
                return; // nothing is chosen while the list is replaced: that is not "Custom"
            }

            var index = PresetItems.ToList().FindIndex(i => string.Equals(i.Name, value.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                PresetIndex = index;
            }
        }
    }

    private void RaisePresetChoice()
    {
        Raise(nameof(PresetIndex));
        Raise(nameof(SelectedPreset));
        Raise(nameof(PresetStateText));
    }

    /// <summary>Puts the values of the preset of this name into the settings (empty: no preset, the settings stay) and shows them all again.</summary>
    public void ApplyPreset(string name)
    {
        if (IsRunning)
        {
            return;
        }

        _store.Update(s =>
        {
            ConversionPreset.Find(s, name)?.ApplyTo(s);
            s.PresetSelected = ConversionPreset.Find(s, name)?.Name ?? string.Empty;
        });
        _applyingPreset = true;
        try
        {
            _dpiText = _store.Current.Images.Dpi.ToString(CultureInfo.InvariantCulture); // the box keeps its own text: it would show the DPI from before
            Raise(string.Empty); // every setting of the steps may have changed
            RefreshPrompts();
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>The settings were changed after the preset was chosen.</summary>
    private bool IsPresetModified => ConversionPreset.Find(_store.Current, _store.Current.PresetSelected) is { } preset && !preset.Matches(_store.Current);

    /// <summary>"(changed)" after the name of the preset when the settings no longer have its values; empty otherwise.</summary>
    public string PresetStateText =>
        IsPresetModified ? UiText.Get("Preset_Modified")
        : _workSetName is { } workSet && _store.Current.PresetSelected.Length == 0
            ? UiText.Get("Preset_WorkSet", workSet) + (JsonDefaults.Serialize(ConditionSet.From(_store.Current)) == _workSetConditions ? string.Empty : " " + UiText.Get("Preset_Modified"))
        : _store.IsHoldingConditions && _store.Current.PresetSelected.Length == 0 ? UiText.Get("Preset_Previous")
        : string.Empty;

    /// <summary>The conditions were put into the settings from elsewhere (an earlier conversion, a work set): every item is shown again.</summary>
    private void RaiseAllConditions()
    {
        _applyingPreset = true;
        try
        {
            _dpiText = _store.Current.Images.Dpi.ToString(CultureInfo.InvariantCulture);
            Raise(string.Empty);
            RefreshPrompts();
            RaiseConnection();
        }
        finally
        {
            _applyingPreset = false;
        }

        UpdateRunCommands();
    }

    /// <summary>Saves the settings now as a preset of the user with this name (an existing one of the same name is replaced) and chooses it.</summary>
    public void SavePreset(string name)
    {
        var trimmed = name.Trim();
        _store.Update(s =>
        {
            // A preset of the same name is replaced where it is, so the list keeps its order.
            var index = s.Presets.FindIndex(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                s.Presets[index] = ConversionPreset.From(s, trimmed);
            }
            else
            {
                s.Presets.Add(ConversionPreset.From(s, trimmed));
            }

            s.PresetSelected = trimmed;
        });
        SyncPresetItems();
        RaisePresetChoice();
    }

    /// <summary>Renames a preset of the user; the choice follows it.</summary>
    public void RenamePreset(string oldName, string newName)
    {
        var trimmed = newName.Trim();
        _store.Update(s =>
        {
            if (s.Presets.FirstOrDefault(p => string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase)) is { } preset)
            {
                preset.Name = trimmed;
                if (string.Equals(s.PresetSelected, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    s.PresetSelected = trimmed;
                }
            }
        });

        // The item is renamed, not replaced: the drop-down list keeps showing it when it is the chosen one.
        _presetItems.Skip(1 + ConversionPreset.BuiltIn.Count).FirstOrDefault(i => string.Equals(i.Name, oldName, StringComparison.OrdinalIgnoreCase))?.Change(trimmed, trimmed);
        SyncPresetItems();
        RaisePresetChoice();
    }

    /// <summary>Deletes a preset of the user; when it was chosen, the choice becomes "Custom" (the settings stay).</summary>
    public void DeletePreset(string name)
    {
        _store.Update(s =>
        {
            s.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(s.PresetSelected, name, StringComparison.OrdinalIgnoreCase))
            {
                s.PresetSelected = string.Empty;
            }
        });

        // The choice moves first, then the item goes: a chosen item that goes leaves the drop-down list with nothing chosen.
        RaisePresetChoice();
        SyncPresetItems();
        RaisePresetChoice();
    }

    /// <summary>The names of the presets of the user, for the window that manages them.</summary>
    public IReadOnlyList<string> UserPresetNames => [.. _store.Current.Presets.Select(p => p.Name)];

    /// <summary>A preset of the user, or a built-in one, already has this name.</summary>
    public bool PresetExists(string name) => ConversionPreset.Find(_store.Current, name) is not null;

    /// <summary>Write the description of the structure at the top of the Markdown (FR-151).</summary>
    public bool StructureNote
    {
        get => _store.Current.Output.StructureNote;
        set
        {
            _store.Update(s => s.Output.StructureNote = value);
            Raise();
        }
    }

    /// <summary>Write an xlsx as one Markdown for each sheet (FR-153).</summary>
    public bool ExcelPerSheet
    {
        get => _store.Current.Output.ExcelPerSheet;
        set
        {
            _store.Update(s => s.Output.ExcelPerSheet = value);
            Raise();
        }
    }

    /// <summary>Write the chunks for retrieval next to the Markdown, as <c>&lt;name&gt;_chunks.jsonl</c> (FR-152).</summary>
    public bool Chunks
    {
        get => _store.Current.Output.Chunks;
        set
        {
            _store.Update(s => s.Output.Chunks = value);
            Raise();
        }
    }

    public bool CanEditPrompts => CanEdit && Step3;

    // ---- what the view needs to show dialogs (set by the window) ----

    public Func<FolderPurpose, string?>? PickFolder { get; set; }

    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>Asks a yes/no question with the given text; true for yes (set by the window).</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Asks whether to add inputs whose earlier output folder or conditions differ from the first (FR-160); true adds them.</summary>
    public Func<string, bool>? ConfirmAdd { get; set; }

    /// <summary>Tells the user, in a dialog, why a conversion did not start or stopped because of the VLM (the status bar alone is easy to miss).</summary>
    public Action<string>? ShowStopped { get; set; }

    // ---- state ----

    public ObservableCollection<string> InputPaths { get; }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public FileItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(CanMarkSelectedForReread));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>
    /// Shows a message in the status bar. The message is kept as the way to make it, so that it is made again in the new
    /// language when the language is changed.
    /// </summary>
    private void SetStatus(Func<string> source)
    {
        _statusSource = source;
        Status = source();
    }

    // ---- the folded part of the conditions (FR-102) ----

    /// <summary>The part with the kinds of file and the conversion is open. It is kept in the settings.</summary>
    public bool ConditionsExpanded
    {
        get => _store.Current.Ui.ConditionsExpanded;
        set
        {
            if (value == _store.Current.Ui.ConditionsExpanded)
            {
                return;
            }

            _store.Update(s => s.Ui.ConditionsExpanded = value);
            Raise();
            Raise(nameof(ConditionsCollapsedVisibility));
        }
    }

    /// <summary>The line of the choices is shown when the part is closed.</summary>
    public System.Windows.Visibility ConditionsCollapsedVisibility => ConditionsExpanded ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>What is chosen in the folded part, in one line.</summary>
    public string ConditionSummaryText => ConditionSummary.Build(_store.Current);

    /// <summary>An item of the folded part (<c>dpi</c>, <c>engine</c>, <c>types</c>) was refused while the part was closed: the part is open now, the screen puts the focus on the item.</summary>
    public event Action<string>? ConditionItemRejected;

    private void OpenCondition(string item)
    {
        if (ConditionsExpanded)
        {
            return; // it is on the screen already
        }

        ConditionsExpanded = true;
        ConditionItemRejected?.Invoke(item);
    }

    // ---- what can be changed (FR-066) ----

    /// <summary>Nothing of the settings or the inputs can be changed while a conversion runs.</summary>
    public bool CanEdit => !IsRunning;

    /// <summary>A conversion of the output folder that can be resumed or retried is shown (FR-013e): its settings are used again.</summary>
    public bool HasPrevious => _previous is { CanResume: true } or { CanRetryFailed: true } || FileStates.ToResume(_entries).Count > 0;

    /// <summary>The list shows the files of the inputs and none of them has been converted into the output folder (FR-157).</summary>
    private bool ListIsNotConverted => Files.Count > 0 && Files.All(f => f.Entry is { State: FileState.NotConverted });

    /// <summary>
    /// The types, the range and the conversion (engine, DPI). A resume does not use them (it goes on with those of its run), but they can be
    /// changed while it can be done: "Redo all" uses them (FR-129).
    /// </summary>
    public bool CanEditNewRun => !IsRunning;

    /// <summary>"Write the results to a separate file" has a meaning only when Step 3 is on.</summary>
    public bool CanEditVlmSeparate => !IsRunning && Step3;

    public bool CanEditStep1Tables => !IsRunning && Step3;

    /// <summary>How the page image is written can be chosen while Step2 is on (FR-149).</summary>
    public bool CanEditPageImage => !IsRunning && Step2;

    /// <summary>The pages that Step3 reads, as written (FR-097). Not kept in the settings: it is for the one file that is converted.</summary>
    public string PageRangeText
    {
        get => _pageRangeText;
        set => Set(ref _pageRangeText, value);
    }

    /// <summary>The page range can be written when one file (not a folder) is the input and a new run can be set up.</summary>
    public bool CanEditPages => CanEditNewRun && IsOneFile;

    /// <summary>The input is one file, not a folder and not several.</summary>
    private bool IsOneFile => InputPaths.Count == 1 && !Directory.Exists(InputPaths[0]);

    public string PagesTip =>
        !IsOneFile ? UiText.Get("Main_Cond_PagesOneFileTip")
        : !Step3 ? UiText.Get("Main_Cond_PagesStep3OffTip")
        : UiText.Get("Main_Cond_PagesTip");

    private void RaiseEditState()
    {
        Raise(nameof(CanEdit));
        Raise(nameof(HasPrevious));
        Raise(nameof(CanEditNewRun));
        Raise(nameof(CanEditVlmSeparate));
        Raise(nameof(CanEditStep1Tables));
        Raise(nameof(CanEditPageImage));
        Raise(nameof(CanEditPrompts));
        Raise(nameof(CanEditPages));
        Raise(nameof(PagesTip));
        Raise(nameof(PagesVisibility));
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value))
            {
                RaiseEditState();
                RaiseProgress();
                Raise(nameof(BusyVisibility));
                Raise(nameof(CanReread));
                Raise(nameof(CanMarkSelectedForReread));
                foreach (var command in new[] { StartCommand, StartChangedCommand, RedoAllCommand, InterruptCommand, ResumeCommand, RereadCommand, AddFilesCommand, AddFolderCommand, ClearInputsCommand, RemoveSelectedInputsCommand, BrowseOutputCommand, ConnectionSettingsCommand, ExtractorSettingsCommand, PromptsCommand })
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public bool IsRefreshingFiles
    {
        get => _refreshingFiles;
        private set
        {
            if (Set(ref _refreshingFiles, value))
            {
                Raise(nameof(BusyVisibility));
            }
        }
    }

    public System.Windows.Visibility BusyVisibility => IsRunning || IsRefreshingFiles
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    // ---- settings the user can change (each is saved as soon as it changes) ----

    public string OutputFolder
    {
        get => _store.Current.Output.Folder;
        set
        {
            if (value != _store.Current.Output.Folder)
            {
                _store.Update(s => s.Output.Folder = value);
                Raise();
                UpdateRunCommands();
                _ = RefreshPreviousAsync(showFiles: true);
            }
        }
    }

    public bool TypeDocx { get => HasType(DocumentKind.Docx); set => SetType(DocumentKind.Docx, value); }

    public bool TypeXlsx { get => HasType(DocumentKind.Xlsx); set => SetType(DocumentKind.Xlsx, value); }

    public bool TypePptx { get => HasType(DocumentKind.Pptx); set => SetType(DocumentKind.Pptx, value); }

    public bool TypePdf { get => HasType(DocumentKind.Pdf); set => SetType(DocumentKind.Pdf, value); }

    // The extractor for each kind of file (FR-111): the external commands are written in settings.json. The choices are shown only when there are some.
    public sealed record ExtractorItem(string Name, string Display);

    public IReadOnlyList<ExtractorItem> ExtractorChoices =>
        [new(string.Empty, UiText.Get("Main_Cond_ExtractorBuiltIn")), .. _store.Current.Extractors.Select(e => new ExtractorItem(e.Name, e.Name))];

    public System.Windows.Visibility ExtractorVisibility => _store.Current.Extractors.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string ExtractorDocx { get => _store.Current.Input.Extractors.Docx; set => SetExtractor(DocumentKind.Docx, value); }

    public string ExtractorXlsx { get => _store.Current.Input.Extractors.Xlsx; set => SetExtractor(DocumentKind.Xlsx, value); }

    public string ExtractorPptx { get => _store.Current.Input.Extractors.Pptx; set => SetExtractor(DocumentKind.Pptx, value); }

    public string ExtractorPdf { get => _store.Current.Input.Extractors.Pdf; set => SetExtractor(DocumentKind.Pdf, value); }

    // The options beside a kind are for the built-in extraction; they do nothing to the Markdown of an extractor, so they are off while one is chosen.
    public bool XlsxOptionsEnabled => ExtractorXlsx.Length == 0;

    public bool PptxOptionsEnabled => ExtractorPptx.Length == 0;

    public bool PdfOptionsEnabled => ExtractorPdf.Length == 0;

    public string XlsxOptionsTip => ExtractorXlsx.Length == 0 ? string.Empty : UiText.Get("Main_Cond_OptionsOffTip");

    public string PptxOptionsTip => ExtractorPptx.Length == 0 ? string.Empty : UiText.Get("Main_Cond_OptionsOffTip");

    public string PdfOptionsTip => ExtractorPdf.Length == 0 ? string.Empty : UiText.Get("Main_Cond_OptionsOffTip");

    private void SetExtractor(DocumentKind kind, string? name)
    {
        _store.Update(s => s.Input.Extractors.Set(kind, name ?? string.Empty));
        Raise(kind switch { DocumentKind.Docx => nameof(ExtractorDocx), DocumentKind.Xlsx => nameof(ExtractorXlsx), DocumentKind.Pptx => nameof(ExtractorPptx), _ => nameof(ExtractorPdf) });
        RaiseOptions();
    }

    /// <summary>Use the text layer of a PDF next to its page images (FR-076).</summary>
    public bool UsePdfText
    {
        get => _store.Current.Input.UsePdfText;
        set
        {
            _store.Update(s => s.Input.UsePdfText = value);
            Raise();
        }
    }

    /// <summary>A PDF is read best through its image: tell it when the PDF is chosen and Step3 is off.</summary>
    public System.Windows.Visibility PdfHintVisibility =>
        TypePdf && !Step3 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public bool Recurse
    {
        get => _store.Current.Input.Recurse;
        set
        {
            _store.Update(s => s.Input.Recurse = value);
            Raise();
            RefreshEntriesForSteps();
        }
    }

    /// <summary>Where the output of a folder goes (FR-154): 0 makes a folder of its name, 1 puts what is in it straight into the output folder.</summary>
    public int FolderLayoutIndex
    {
        get => _store.Current.Output.FolderLayout == FolderLayout.Contents ? 1 : 0;
        set
        {
            _store.Update(s => s.Output.FolderLayout = value == 1 ? FolderLayout.Contents : FolderLayout.Folder);
            Raise();
            RefreshEntriesForSteps();
        }
    }

    public bool IncludeOffSlideText
    {
        get => _store.Current.Input.IncludeOffSlideText;
        set
        {
            _store.Update(s => s.Input.IncludeOffSlideText = value);
            Raise();
        }
    }

    /// <summary>Write bold text between ** and italic text between * (FR-101).</summary>
    public bool Emphasis
    {
        get => _store.Current.Input.Emphasis;
        set
        {
            _store.Update(s => s.Input.Emphasis = value);
            Raise();
        }
    }

    /// <summary>Save the pictures of a docx, pptx or xlsx in pictures/ and refer to them from the Markdown (FR-133 to FR-135).</summary>
    public bool ExtractPictures
    {
        get => _store.Current.Input.ExtractPictures;
        set
        {
            _store.Update(s => s.Input.ExtractPictures = value);
            Raise();
        }
    }

    /// <summary>Cut the drawn shapes of a pptx or xlsx out of the page image into pictures/ (FR-136).</summary>
    public bool ExtractShapes
    {
        get => _store.Current.Input.ExtractShapes;
        set
        {
            _store.Update(s => s.Input.ExtractShapes = value);
            Raise();
        }
    }

    /// <summary>Cut the shapes and pictures that are near one another, or on the same row, out as one picture (pptx).</summary>
    public bool GroupNearShapes
    {
        get => _store.Current.Input.GroupNearShapes;
        set
        {
            _store.Update(s => s.Input.GroupNearShapes = value);
            Raise();
        }
    }

    public bool IncludeHiddenSheets
    {
        get => _store.Current.Input.IncludeHiddenSheets;
        set
        {
            _store.Update(s => s.Input.IncludeHiddenSheets = value);
            Raise();
        }
    }

    public bool IncludeHiddenSlides
    {
        get => _store.Current.Input.IncludeHiddenSlides;
        set
        {
            _store.Update(s => s.Input.IncludeHiddenSlides = value);
            Raise();
        }
    }

    public bool Step2
    {
        get => _store.Current.Steps.Step2;
        set
        {
            _store.Update(s => s.Steps.Step2 = value);
            Raise();
            RefreshEntriesForSteps();
            Raise(nameof(CanEditPageImage));
        }
    }

    /// <summary>The engines found on this computer. The combo box lists only these.</summary>
    public ObservableCollection<EngineChoice> EngineChoices { get; } = [];

    /// <summary>The engine in use. It is always one of <see cref="EngineChoices"/>, or null when none is installed.</summary>
    public EngineKind? SelectedEngine
    {
        get => EngineChoices.Any(c => c.Kind == _store.Current.Engine.Kind) ? _store.Current.Engine.Kind : null;
        set
        {
            if (value is { } kind && kind != _store.Current.Engine.Kind)
            {
                _store.Update(s => s.Engine.Kind = kind);
                Raise();
            }
        }
    }

    /// <summary>Checks which engines are installed and fills the combo box. A saved engine that is gone is replaced.</summary>
    private Func<string>? DetectEngines()
    {
        var engine = _store.Current.Engine;
        var found = new List<EngineChoice>();
        foreach (var (kind, name) in new[] { (EngineKind.Office, "Microsoft Office"), (EngineKind.LibreOffice, "LibreOffice") })
        {
            var candidate = EngineFactory.Create(new EngineSettings { Kind = kind, LibreOfficePath = engine.LibreOfficePath });
            try
            {
                if (candidate.CheckAvailability().IsAvailable)
                {
                    found.Add(new EngineChoice(kind, name));
                }
            }
            finally
            {
                (candidate as IDisposable)?.Dispose();
            }
        }

        foreach (var choice in found)
        {
            EngineChoices.Add(choice);
        }

        if (found.Count == 0)
        {
            return () => UiText.Get("Status_NoEngine");
        }
        else if (found.All(c => c.Kind != engine.Kind))
        {
            var fallback = found[0];
            _store.Update(s => s.Engine.Kind = fallback.Kind);
            var was = engine.Kind == EngineKind.Office ? "Microsoft Office" : "LibreOffice";
            return () => UiText.Get("Status_EngineChanged", was, fallback.Name);
        }

        return null;
    }

    // ---- language and theme (FR-032, FR-033, FR-035) ----

    public bool LanguageIsJapanese => _store.Current.Ui.Language == LanguageResolver.Japanese;

    public bool LanguageIsEnglish => _store.Current.Ui.Language == LanguageResolver.English;

    public bool LanguageIsSystem => !LanguageIsJapanese && !LanguageIsEnglish;

    public bool ThemeIsLight => _store.Current.Ui.Theme == UiSettings.LightTheme;

    public bool ThemeIsDark => _store.Current.Ui.Theme == UiSettings.DarkTheme;

    public bool ThemeIsSystem => !ThemeIsLight && !ThemeIsDark;

    /// <summary>Changes the theme of the screen at once and saves it (<c>system</c>, <c>light</c> or <c>dark</c>).</summary>
    public void SetTheme(string setting)
    {
        _store.Update(s => s.Ui.Theme = setting);
        ThemeService.Apply(setting);
        Raise(nameof(ThemeIsLight));
        Raise(nameof(ThemeIsDark));
        Raise(nameof(ThemeIsSystem));
    }

    /// <summary>Changes the language of the screen at once and saves it (<c>system</c>, <c>ja</c> or <c>en</c>).</summary>
    public void SetLanguage(string setting)
    {
        _store.Update(s => s.Ui.Language = setting);
        LanguageService.Apply(setting);
        Raise(nameof(LanguageIsJapanese));
        Raise(nameof(LanguageIsEnglish));
        Raise(nameof(LanguageIsSystem));
        SyncPresetItems(); // the names of "Custom" and of the built-in presets
        RaiseConnection();
        Raise(nameof(ConditionSummaryText));
        Status = _statusSource();
        if (!IsRunning)
        {
            // The texts of the list were made in the earlier language: make them again.
            foreach (var file in Files)
            {
                file.Reproject();
            }
        }
    }

    // The three choices of how the pages of an Excel sheet are cut (FR-073) are radio buttons: each one is true when it is chosen.
    public bool ExcelColumns
    {
        get => _store.Current.Images.ExcelRange == ExcelRange.Columns;
        set => ChooseExcelRange(value, ExcelRange.Columns);
    }

    public bool ExcelSheet
    {
        get => _store.Current.Images.ExcelRange == ExcelRange.Sheet;
        set => ChooseExcelRange(value, ExcelRange.Sheet);
    }

    public bool ExcelPrintArea
    {
        get => _store.Current.Images.ExcelRange == ExcelRange.PrintArea;
        set => ChooseExcelRange(value, ExcelRange.PrintArea);
    }

    private void ChooseExcelRange(bool chosen, ExcelRange range)
    {
        if (chosen && _store.Current.Images.ExcelRange != range)
        {
            _store.Update(s => s.Images.ExcelRange = range);
            Raise(nameof(ExcelColumns));
            Raise(nameof(ExcelSheet));
            Raise(nameof(ExcelPrintArea));
        }
    }

    /// <summary>Changes the theme at once and saves it (<c>system</c>, <c>light</c> or <c>dark</c>).</summary>
    public string DpiText
    {
        get => _dpiText;
        set
        {
            if (!Set(ref _dpiText, value))
            {
                return;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi)
                && dpi is >= SettingsLimits.MinDpi and <= SettingsLimits.MaxDpi)
            {
                _store.Update(s => s.Images.Dpi = dpi);
                SetStatus(() => UiText.Get("Status_DpiSet", dpi));
            }
            else
            {
                var kept = _store.Current.Images.Dpi;
                SetStatus(() => UiText.Get("Status_DpiInvalid", SettingsLimits.MinDpi, SettingsLimits.MaxDpi, kept));
                OpenCondition("dpi");
            }
        }
    }

    // ---- commands ----

    public RelayCommand StartCommand { get; }

    public RelayCommand StartChangedCommand { get; }

    public RelayCommand InterruptCommand { get; }

    public RelayCommand ResumeCommand { get; }

    /// <summary>"Read again with the VLM": the pages that are marked (FR-139).</summary>
    public RelayCommand RereadCommand { get; }

    /// <summary>The pages that are marked can be read again: Step 3 is on, nothing is running, and a page is marked (FR-139).</summary>
    public bool CanReread => !IsRunning && Step3 && (Files.Any(f => f.RereadCount.Marked > 0) || StaleCount() > 0);

    /// <summary>The model and the prompt that a reading of a file of this kind would use now: the choices on the screen (FR-139).</summary>
    private (string? Model, string Prompt, Step3Mode Mode) CurrentStep3(DocumentKind kind)
    {
        var settings = _store.Current;
        return (Step3Settings.ModelText(settings.FindConnection(settings.Vlm.Selected)), Step3Settings.PromptText(settings.Vlm.Prompts.VariantsOf(kind).Selected), settings.Steps.Step3Mode);
    }

    /// <summary>How many pages of the list were read with other settings than those on the screen (the connection, the prompt of their kind of file): Step 3 can be run again for them.</summary>
    public int StaleCount() => Files.Sum(f => f.Source is { } document && document.Step3 == StepStatus.Done
        ? RereadMarks.Stale(document, CurrentStep3(document.Kind).Model, CurrentStep3(document.Kind).Prompt, CurrentStep3(document.Kind).Mode).Count()
        : 0);

    /// <summary>The state of "Run Step 3 again" is worked out again (the settings of Step 3 or the list changed).</summary>
    public void RaiseRereadState()
    {
        RereadCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanReread));
    }

    /// <summary>The marks of a re-reading can be put on the files chosen in the list: Step 3 is on and nothing runs (FR-138).</summary>
    public bool CanMarkSelectedForReread => !IsRunning && Step3 && Selected is { CanReread: true };

    /// <summary>
    /// Puts the marks of a re-reading on the pages of a document, or takes them off (FR-138); <paramref name="pages"/> null is every page.
    /// Not while something runs (the state is being written). Returns false when the marks could not be kept.
    /// </summary>
    public bool SetRereadMarks(DocumentRecord document, IEnumerable<int>? pages, bool marked)
    {
        if (IsRunning)
        {
            return false;
        }

        var store = new StateStore(_store.Current.Output.Folder);
        var saved = pages is null
            ? RereadMarks.SetAll(store, document, marked)
            : RereadMarks.Set(store, document, pages, marked);
        RefreshRereadMarks(document);
        return saved;
    }

    /// <summary>Marks the pages of the document that could not be read.</summary>
    public bool MarkFailedForReread(DocumentRecord document)
    {
        if (IsRunning)
        {
            return false;
        }

        var saved = RereadMarks.MarkFailed(new StateStore(_store.Current.Output.Folder), document);
        RefreshRereadMarks(document);
        return saved;
    }

    private void RefreshRereadMarks(DocumentRecord document)
    {
        Raise(nameof(CanMarkSelectedForReread));
        Files.FirstOrDefault(f => ReferenceEquals(f.Source, document))?.Reproject();
        RereadCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanReread));
    }

    /// <summary>
    /// Reads the marked pages again (FR-139): only those pages go to the VLM, with the connection and the prompts that are chosen on the screen. Step 1 and
    /// Step 2 are not done again, and the earlier text of a page stays until the page has been read.
    /// </summary>
    public Task RereadAsync()
    {
        if (!CanReread || !ConfirmSending())
        {
            return Task.CompletedTask;
        }

        // The pages that were read with other settings than those on the screen are read again too: they are marked, so that the list shows it.
        var store = new StateStore(_store.Current.Output.Folder);
        foreach (var file in Files.Where(f => f.Source is { Step3: StepStatus.Done }).ToList())
        {
            var document = file.Source!;
            var (model, prompt, step3Mode) = CurrentStep3(document.Kind);
            if (RereadMarks.Stale(document, model, prompt, step3Mode).Any())
            {
                RereadMarks.MarkStale(store, document, model, prompt, step3Mode);
                RefreshRereadMarks(document);
            }
        }

        // The files of the list are one run (FR-130): the marked pages of the first file's job. A document of another job is not read here.
        var jobId = Files.Where(f => f.RereadCount.Marked > 0).Select(f => f.Source?.JobId).FirstOrDefault(id => id is { } v && v != Guid.Empty);
        if (jobId is null)
        {
            SetStatus(() => UiText.Get("Status_RereadNoJob"));
            return Task.CompletedTask;
        }

        return RunAsync((service, snapshot, token) =>
            service.ResumeAsync(
                new ResumeRequest
                {
                    OutputFolder = snapshot.Output.Folder,
                    JobId = jobId,
                    Mode = ResumeMode.Reread,
                    Step3 = true,
                    PromptNames = ConvertRequest.OptionsFrom(snapshot).PromptNames,
                    Step3Mode = snapshot.Steps.Step3Mode,
                },
                token));
    }

    public RelayCommand RedoAllCommand { get; }

    public RelayCommand AddFilesCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand ClearInputsCommand { get; }

    public RelayCommand RemoveSelectedInputsCommand { get; }

    /// <summary>The rows selected in the list of inputs; the window sets it when the selection changes.</summary>
    public IReadOnlyList<string> SelectedInputPaths { get; private set; } = [];

    public void SetSelectedInputs(IEnumerable<string> paths)
    {
        SelectedInputPaths = [.. paths];
        RemoveSelectedInputsCommand.RaiseCanExecuteChanged();
    }

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand ConnectionSettingsCommand { get; }

    public RelayCommand ExtractorSettingsCommand { get; }

    public RelayCommand PromptsCommand { get; }

    /// <summary>How the print image of a page is written in the Markdown: 0 an image that a viewer shows, 1 a comment that it does not, 2 a link (FR-149).</summary>
    public int PageImageIndex
    {
        get => (int)_store.Current.Steps.PageImage;
        set
        {
            if (value is >= 0 and <= 2)
            {
                _store.Update(s => s.Steps.PageImage = (PageImageStyle)value);
            }

            Raise();
        }
    }

    /// <summary>Write the VLM results to a separate file (FR-058).</summary>
    public bool VlmSeparate
    {
        get => _store.Current.Steps.VlmSeparate;
        set
        {
            _store.Update(s => s.Steps.VlmSeparate = value);
            Raise();
            Raise(nameof(VlmSeparateIndex));
        }
    }

    /// <summary>Where the results of Step3 go, as the list shows it: 0 in the Markdown, 1 in a separate file (FR-058).</summary>
    public int VlmSeparateIndex
    {
        get => VlmSeparate ? 1 : 0;
        set => VlmSeparate = value == 1;
    }

    /// <summary>What is done with the tables of the VLM result where the text of the page has a table: 0 they stay, 1 Step1 replaces them, 2 they are left out (FR-105).</summary>
    public int Step1TablesIndex
    {
        get => (int)_store.Current.Steps.Step1Tables;
        set
        {
            if (value is >= 0 and <= 2)
            {
                _store.Update(s => s.Steps.Step1Tables = (Step1TablesMode)value);
            }

            Raise();
        }
    }

    /// <summary>Leave the tables that the VLM wrote again on a page out of the Markdown (FR-124).</summary>
    /// <summary>
    /// "Start" and "Redo all" write a file that was converted before into its earlier folder (true) or into a new folder (false) (FR-129).
    /// The two items of the drop-down list beside the buttons are the two values (<see cref="SameFolderIndex"/>).
    /// </summary>
    public bool SameFolder
    {
        get => _store.Current.Output.SameFolder;
        set
        {
            if (value != _store.Current.Output.SameFolder)
            {
                _store.Update(s => s.Output.SameFolder = value);
                Raise();
                Raise(nameof(SameFolderIndex));
            }
        }
    }

    /// <summary>The drop-down list: 0 makes a new folder, 1 writes into the earlier one.</summary>
    public int SameFolderIndex
    {
        get => SameFolder ? 1 : 0;
        set => SameFolder = value == 1;
    }

    public bool DropRepeatedTables
    {
        get => _store.Current.Steps.DropRepeatedTables;
        set
        {
            _store.Update(s => s.Steps.DropRepeatedTables = value);
            Raise();
        }
    }

    /// <summary>How Step3 uses the VLM (FR-150): 0 adds to the text of Step1, 1 reads every page whole.</summary>
    public int Step3ModeIndex
    {
        get => (int)_store.Current.Steps.Step3Mode;
        set
        {
            if (value is >= 0 and <= 1)
            {
                _store.Update(s => s.Steps.Step3Mode = (Step3Mode)value);
                RaiseRereadState(); // the pages read with the other use can be read again
            }

            Raise();
        }
    }

    /// <summary>Step 3: read the page images with the VLM.</summary>
    public bool Step3
    {
        get => _store.Current.Steps.Step3;
        set
        {
            _store.Update(s => s.Steps.Step3 = value);
            RefreshEntriesForSteps();
            Raise(nameof(CanEditVlmSeparate));
            Raise(nameof(CanEditStep1Tables));
            Raise(nameof(CanEditPrompts));
            Raise(nameof(PagesTip));
            Raise(nameof(PdfHintVisibility));
            Raise(nameof(Step3DetailsVisibility));
            Raise(nameof(PagesVisibility));
            RereadCommand.RaiseCanExecuteChanged();
            Raise(nameof(CanReread));
            Raise(nameof(CanMarkSelectedForReread));
            Raise();
        }
    }

    /// <summary>The name of the VLM connection in use, for display.</summary>
    public string ConnectionInUse => string.IsNullOrEmpty(_store.Current.Vlm.Selected) ? UiText.Get("Conn_None") : _store.Current.Vlm.Selected;

    private Task OpenConnectionSettingsAsync()
    {
        ShowConnectionSettings?.Invoke();
        RaiseConnection(); // the window may have added, removed or chosen a connection
        return Task.CompletedTask;
    }

    private Task OpenExtractorSettingsAsync()
    {
        ShowExtractorSettings?.Invoke();

        // The window may have added, removed or renamed extractors, or changed what is chosen.
        foreach (var name in new[] { nameof(ExtractorChoices), nameof(ExtractorVisibility), nameof(ExtractorDocx), nameof(ExtractorXlsx), nameof(ExtractorPptx), nameof(ExtractorPdf) })
        {
            Raise(name);
        }

        RaiseOptions();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _elsewhereTimer?.Dispose();
        _store.Dispose();
    }

    private System.Threading.Timer? _elsewhereTimer;

    /// <summary>
    /// While the list has a file that another process converts (FR-157), its state is looked for again every 10 seconds, so that it can be
    /// taken when that run has ended.
    /// </summary>
    private void ScheduleElsewhereCheck()
    {
        if (!_entries.Any(e => e.State == FileState.RunningElsewhere))
        {
            _elsewhereTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _elsewhereTimer ??= new System.Threading.Timer(_ => _dispatcher.BeginInvoke(() =>
        {
            if (!IsRunning && _entries.Any(e => e.State == FileState.RunningElsewhere) && Files.Any(f => f.Entry is not null))
            {
                _ = RefreshPreviousAsync(showFiles: true);
            }
        }));
        _elsewhereTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private TaskCompletionSource? _runFinished;

    /// <summary>
    /// Asks a conversion that is running to stop, and waits until it has closed what it opened (Office, the connection, the state of the job).
    /// Returns false when it did not finish within the time.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        if (!IsRunning || _runFinished is not { } finished)
        {
            return true;
        }

        SetStatus(() => UiText.Get("Status_Interrupting"));
        _cancellation?.Cancel();
        return await Task.WhenAny(finished.Task, Task.Delay(timeout)).ConfigureAwait(true) == finished.Task;
    }

    // ---- input and output ----

    private Task AddFilesAsync()
    {
        AddInputs([.. PickFiles?.Invoke() ?? []]);
        return Task.CompletedTask;
    }

    private Task AddFolderAsync()
    {
        if (PickFolder?.Invoke(FolderPurpose.Input) is { } folder)
        {
            AddInputs([folder]);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds inputs in their order. When the earlier output folder or conditions of one of them differ from those of the first (FR-160), one
    /// question asks first: "Add" adds them (the output folder and the conditions stay those of the first), "Cancel" changes nothing.
    /// </summary>
    private void AddInputs(IReadOnlyList<string> paths)
    {
        var adding = paths.Where(p => !InputPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (adding.Count == 0)
        {
            return;
        }

        var differences = AddDifferences(adding);
        if (differences.Count > 0)
        {
            if (ConfirmAdd is { } confirm && !confirm(UiText.Get("Confirm_AddDifferent", string.Join(Environment.NewLine, differences))))
            {
                return;
            }

            _pendingNote = UiText.Get("Status_AddedAligned");
        }

        foreach (var path in adding)
        {
            InputPaths.Add(path);
        }

        SaveInputs();
    }

    /// <summary>
    /// The added inputs whose earlier output folder or conditions differ from those of the first (FR-160), one line each. The first is that of
    /// the list, or else the one of the added inputs that would be the first. The conditions of the first are those of its record.
    /// </summary>
    internal List<string> AddDifferences(IReadOnlyList<string> adding)
    {
        var history = _store.Current.History;
        var output = _store.Current.Output.Folder;
        IReadOnlyList<string> before = [.. InputPaths];
        var first = PreviousState.FindFirst(output, before, history, out _) ?? PreviousState.FindFirst(output, [.. before, .. adding], history, out _);
        var lines = new List<string>();
        if (first is null)
        {
            return lines;
        }

        var firstConditions = PreviousState.ConditionsOf(first.Output, first.Input)?.Conditions;
        foreach (var input in adding.Where(a => !string.Equals(a, first.Input, StringComparison.OrdinalIgnoreCase)))
        {
            if (PreviousState.OfAdded(input, first.Output, history) is not { } added)
            {
                continue; // no record anywhere: a new file of the first's folder
            }

            var parts = new List<string>();
            if (!PreviousState.SameFolder(added.Output, first.Output))
            {
                parts.Add(UiText.Get("Add_OtherOutput", added.Output));
            }

            if (added.Conditions is { } conditions && firstConditions is { } wanted)
            {
                parts.AddRange(conditions.Differences(wanted));
            }

            if (parts.Count > 0)
            {
                lines.Add($"{Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}: {string.Join(UiText.Get("List_Separator"), parts)}");
            }
        }

        return lines;
    }

    // ---- work sets (FR-159) ----

    /// <summary>The names of the work sets, those opened lately first.</summary>
    public IReadOnlyList<string> WorkSetNames =>
        [.. _store.Current.WorkSets.OrderByDescending(w => w.OpenedAt ?? w.UpdatedAt).Select(w => w.Name)];

    public bool WorkSetExists(string name) => _store.Current.WorkSets.Any(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the inputs, the output folder and the conditions on the screen as a work set (one of the same name is replaced). Also while a conversion runs.</summary>
    public void SaveWorkSet(string name)
    {
        var trimmed = name.Trim();
        var now = DateTimeOffset.Now;
        _store.Update(s =>
        {
            var old = s.WorkSets.FirstOrDefault(w => string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            var set = new WorkSet
            {
                Name = trimmed,
                Inputs = [.. InputPaths],
                Output = s.Output.Folder,
                Conditions = ConditionSet.From(s),
                CreatedAt = old?.CreatedAt ?? now,
                UpdatedAt = now,
                OpenedAt = old?.OpenedAt,
            };
            if (old is null)
            {
                s.WorkSets.Add(set);
            }
            else
            {
                s.WorkSets[s.WorkSets.IndexOf(old)] = set;
            }
        });
        SetStatus(() => UiText.Get("Status_WorkSetSaved", trimmed));
    }

    public void RenameWorkSet(string oldName, string newName) => _store.Update(s =>
    {
        if (s.WorkSets.FirstOrDefault(w => string.Equals(w.Name, oldName, StringComparison.OrdinalIgnoreCase)) is { } set)
        {
            set.Name = newName.Trim();
            set.UpdatedAt = DateTimeOffset.Now;
        }
    });

    public void DeleteWorkSet(string name) => _store.Update(s => s.WorkSets.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Opens a work set (FR-159): the inputs, the output folder and the conditions of the screen are first put back to empty and to the
    /// defaults, then those of the work set are put in, and saved. The output folder and the conditions do not follow the inputs (FR-156,
    /// FR-158) until the inputs are changed. Returns false while a conversion runs.
    /// </summary>
    public bool OpenWorkSet(string name)
    {
        if (IsRunning || _store.Current.WorkSets.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } set)
        {
            return false;
        }

        var notes = new List<string>();
        var inputs = set.Inputs.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        var gone = set.Inputs.Count - inputs.Count;
        if (gone > 0)
        {
            notes.Add(UiText.Get("Status_WorkSetInputsGone", gone));
        }

        _store.KeepConditions();
        var connectionFound = true;
        _store.Update(s =>
        {
            ConditionSet.From(AppSettings.CreateDefault()).ApplyTo(s);
            connectionFound = set.Conditions.ApplyTo(s);
            if (!connectionFound || set.Conditions.Connection.Length == 0)
            {
                s.Vlm.Selected = string.Empty;
            }

            s.Output.Folder = set.Output;
            s.Input.Paths = [.. inputs];
            if (s.WorkSets.FirstOrDefault(w => w.Name == set.Name) is { } opened)
            {
                opened.OpenedAt = DateTimeOffset.Now;
            }
        });
        if (!connectionFound)
        {
            notes.Add(UiText.Get("Status_ConnectionMissing", set.Conditions.Connection));
        }

        // The conditions of the work set win; how they differ from the earlier conversion is told.
        var first = PreviousState.FindFirst(set.Output, inputs, new HistorySettings(), out _);
        if (first is not null && PreviousState.ConditionsOf(set.Output, first.Input) is { } recorded
            && set.Conditions.Differences(recorded.Conditions) is { Count: > 0 } differences)
        {
            notes.Add(UiText.Get("Status_WorkSetDiffers", string.Join(UiText.Get("List_Separator"), differences)));
        }

        _keepList = true;
        try
        {
            InputPaths.Clear();
            foreach (var path in inputs)
            {
                InputPaths.Add(path);
            }
        }
        finally
        {
            _keepList = false;
        }

        _restoreSuspended = true;
        _firstKey = null;
        _entries = [];
        _workSetName = set.Name;
        _workSetConditions = JsonDefaults.Serialize(ConditionSet.From(_store.Current));
        _pendingNote = string.Join(" ", [UiText.Get("Status_WorkSetOpened", set.Name), .. notes]);
        Raise(nameof(OutputFolder));
        RaiseAllConditions();
        _ = RefreshPreviousAsync(showFiles: true);
        return true;
    }

    private Task ClearInputsAsync()
    {
        InputPaths.Clear();
        SaveInputs();
        return Task.CompletedTask;
    }

    private Task RemoveSelectedInputsAsync()
    {
        foreach (var path in SelectedInputPaths)
        {
            InputPaths.Remove(path);
        }

        SelectedInputPaths = [];
        RemoveSelectedInputsCommand.RaiseCanExecuteChanged();
        SaveInputs();
        return Task.CompletedTask;
    }

    /// <summary>Adds what was dropped on the list of inputs: folders, and the files of the supported kinds. Ignored while a conversion runs.</summary>
    public void DropInputs(IEnumerable<string> paths)
    {
        if (IsRunning)
        {
            return;
        }

        AddInputs([.. paths.Where(path => System.IO.Directory.Exists(path) || (System.IO.File.Exists(path) && IsSupportedFile(path)))]);
    }

    /// <summary>Sets the output folder to the folder that was dropped (for a file, the folder it is in). Ignored while a conversion runs.</summary>
    public void DropOutput(IEnumerable<string> paths)
    {
        if (IsRunning)
        {
            return;
        }

        var path = paths.FirstOrDefault();
        var folder = path is null ? null : System.IO.Directory.Exists(path) ? path : System.IO.File.Exists(path) ? System.IO.Path.GetDirectoryName(path) : null;
        if (!string.IsNullOrEmpty(folder))
        {
            OutputFolder = folder;
        }
    }

    private static bool IsSupportedFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx" or ".pdf";

    private Task BrowseOutputAsync()
    {
        if (PickFolder?.Invoke(FolderPurpose.Output) is { } folder)
        {
            OutputFolder = folder;
        }

        return Task.CompletedTask;
    }

    private void SaveInputs() => _store.Update(s => s.Input.Paths = [.. InputPaths]);

    private bool HasType(DocumentKind kind) => _store.Current.Input.Types.Contains(kind);

    private void SetType(DocumentKind kind, bool selected)
    {
        _store.Update(s =>
        {
            s.Input.Types.Remove(kind);
            if (selected)
            {
                s.Input.Types.Add(kind);
            }
        });
        Raise(nameof(TypeDocx));
        Raise(nameof(TypeXlsx));
        Raise(nameof(TypePptx));
        Raise(nameof(TypePdf));
        Raise(nameof(PdfHintVisibility));
        Raise(nameof(ConditionSummaryText));
        RaiseOptions();
        RefreshEntriesForSteps();
        UpdateRunCommands();
    }

    private void RaiseOptions()
    {
        foreach (var name in new[] { nameof(XlsxOptionsEnabled), nameof(PptxOptionsEnabled), nameof(PdfOptionsEnabled), nameof(XlsxOptionsTip), nameof(PptxOptionsTip), nameof(PdfOptionsTip) })
        {
            Raise(name);
        }
    }

    // ---- running ----

    /// <summary>
    /// The question before page images are sent outside the local network (FR-051): where they go, that charges may apply,
    /// and the limits. <c>true</c> when nothing needs to be asked, or the user said yes. Without the consent nothing is asked
    /// here: the check before the run reports it.
    /// </summary>
    /// <summary>The chosen connection sends outside the PC and the user has to be asked before each run (FR-114).</summary>
    private bool SendingNeedsQuestion()
    {
        var settings = _store.Current;
        return settings.FindConnection(settings.Vlm.Selected) is { } connection
            && ExternalConsent.IsRequired(connection)
            && ExternalConsent.MayUse(connection)
            && Confirm is not null;
    }

    /// <param name="readsStep3">Whether the run reads pages with the VLM; by default, Step3 on the screen.</param>
    private bool ConfirmSending(bool? readsStep3 = null)
    {
        var settings = _store.Current;
        if (!(readsStep3 ?? settings.Steps.Step3)
            || settings.FindConnection(settings.Vlm.Selected) is not { } connection
            || !ExternalConsent.IsRequired(connection)
            || !ExternalConsent.MayUse(connection)
            || Confirm is not { } confirm)
        {
            return true;
        }

        var none = UiText.Get("Conn_LimitNone");
        var limits = connection.MaxPages is null && connection.MaxCostUsd is null
            ? string.Empty
            : UiText.Get(
                "Confirm_StartExternalLimits",
                connection.MaxPages?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? none,
                connection.MaxCostUsd?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? none);
        var prompts = settings.Vlm.Prompts;
        var language = LanguageResolver.Resolve(settings.Ui.Language);
        bool SendsText(DocumentKind kind)
        {
            try
            {
                return PromptSupplement.UsesPageText(prompts.Resolve(kind, language, prompts.VariantsOf(kind).Selected));
            }
            catch (PromptNotFoundException)
            {
                return false; // the run stops with a message of its own
            }
        }

        if (new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx }.Any(SendsText))
        {
            limits = UiText.Get("Confirm_StartExternalText") + limits;
        }

        return confirm(UiText.Get("Confirm_StartExternal", InputPaths.Count, ExternalConsent.Destination(connection), limits));
    }

    /// <summary>How a run that starts from the inputs converts them.</summary>
    private enum RunKind
    {
        /// <summary>Every file, each into a new folder.</summary>
        New,

        /// <summary>Every file, each into the folder of its earlier conversion when it has one (FR-129).</summary>
        SameFolders,

        /// <summary>Only the files that changed since their last conversion, into their earlier folders (FR-126).</summary>
        ChangedOnly,
    }

    private Task StartAsync(RunKind kind)
    {
        if (!ConfirmSending())
        {
            return Task.CompletedTask;
        }

        // The interrupted runs that this one replaces are given up once it has really started (FR-013g). There is no question first:
        // "Redo all" says what it does, and "Resume" is the button beside it (FR-129).
        var replaced = _previous is { CanResume: true }
            ? new StateStore(_store.Current.Output.Folder).FindResumableJobs([.. InputPaths], includeFinishedWithErrors: false).Select(j => j.JobId).ToList()
            : [];
        return StartRunAsync(kind, replaced);
    }

    /// <summary>
    /// "Start" and "Redo all" (FR-129): every file of the inputs is converted with the conditions on the screen. A file that was converted
    /// before goes to a new folder or into its earlier folder, as chosen beside the buttons (<see cref="SameFolder"/>).
    /// </summary>
    private Task StartAllAsync() => StartAsync(SameFolder ? RunKind.SameFolders : RunKind.New);

    private Task StartRunAsync(RunKind kind, IReadOnlyList<Guid> replaced)
    {
        RememberOutputs(onlyNew: kind == RunKind.ChangedOnly);
        return StartRunCoreAsync(kind, replaced);
    }

    /// <summary>
    /// Remembers the output folder of the inputs (FR-156): of all of them for "Start" and "Redo all"; of those not remembered yet for the
    /// runs that convert files for the first time ("Resume", "changed files").
    /// </summary>
    private void RememberOutputs(bool onlyNew, IReadOnlyCollection<string>? only = null)
    {
        var output = _store.Current.Output.Folder;
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        _store.Update(s =>
        {
            foreach (var input in InputPaths.Where(i => only is null || only.Contains(i)))
            {
                if (!onlyNew || s.History.OutputOf(input) is null)
                {
                    s.History.Remember(input, output, now);
                }
            }
        });
    }

    /// <summary>Forgets the output folders of the inputs (FR-156, "Clear history").</summary>
    public void ClearHistory() => _store.Update(s => s.History.Outputs.Clear());

    private Task StartRunCoreAsync(RunKind kind, IReadOnlyList<Guid> replaced) => RunAsync(async (service, snapshot, token) =>
    {
        var found = InputEnumerator.Enumerate(snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse, snapshot.Output.FolderLayout);
        var elsewhere = RunElsewhere(snapshot);
        if (elsewhere.Count > 0)
        {
            found = found with { Inputs = [.. found.Inputs.Where(i => !elsewhere.Contains(Path.GetFullPath(i.Path)))] };
            if (found.Inputs.Count == 0)
            {
                throw new ConvertStartException(UiText.Get("Status_Files_Elsewhere", elsewhere.Count));
            }
        }

        if (found.Inputs.Count == 0)
        {
            var reasons = found.Warnings.Count > 0 ? " " + string.Join(" ", found.Warnings) : string.Empty;
            throw new ConvertStartException(UiText.Get("Status_NoDocuments", reasons));
        }

        var request = new ConvertRequest
        {
            Inputs = found.Inputs,
            GivenInputs = snapshot.Input.Paths,
            InputFolder = found.InputFolder,
            OutputFolder = snapshot.Output.Folder,
            Options = WithPages(ConvertRequest.OptionsFrom(snapshot)),
        };

        request = kind switch
        {
            RunKind.ChangedOnly => PreviousOutputs.Load(snapshot.Output.Folder, snapshot.Output.FolderLayout).ChangedOnly(request)
                ?? throw new ConvertStartException(UiText.Get("Status_NothingChanged", found.Inputs.Count)),
            RunKind.SameFolders => PreviousOutputs.Load(snapshot.Output.Folder, snapshot.Output.FolderLayout).SameFolders(request),
            _ => request,
        };

        return await service.RunAsync(request, token);
    }, outputFolder => new StateStore(outputFolder).Abandon(replaced));

    /// <summary>
    /// Converts the chosen files of the list again with the conditions on the screen (FR-127). Each goes back to the folder of its earlier
    /// conversion: its Markdown and page images are replaced, so the paths stay the same.
    /// </summary>
    public Task ReconvertAsync(IReadOnlyList<FileItemViewModel> items) =>
        ReconvertAsync([.. items.Select(i => i.Source).OfType<DocumentRecord>()]);

    /// <param name="documents">The earlier conversions of the files (their folders are written again).</param>
    public Task ReconvertAsync(IReadOnlyList<DocumentRecord> documents)
    {
        if (IsRunning || documents.Count == 0 || !ConfirmSending())
        {
            return Task.CompletedTask;
        }

        // Pages that wait to be read again by Step 3 (FR-138): the conversion starts from Step 1 with the settings on the screen, so the marks are not used.
        var waiting = documents.Sum(d => RereadMarks.Count(d).Marked);
        var note = waiting > 0 ? UiText.Get("Confirm_ReconvertMarked", waiting) : string.Empty;
        if (Confirm is { } confirm && !confirm(UiText.Get("Confirm_Reconvert", documents.Count, note)))
        {
            return Task.CompletedTask;
        }

        return RunAsync((service, snapshot, token) =>
        {
            // A document whose job another process runs now is left out (FR-157).
            var locks = new StateStore(snapshot.Output.Folder);
            documents = [.. documents.Where(d => !locks.IsLocked(d.JobId))];
            if (documents.Count == 0)
            {
                throw new ConvertStartException(UiText.Get("Status_Files_Elsewhere", 1));
            }

            var previous = new Dictionary<string, PreviousOutput>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents.Where(d => Directory.Exists(d.OutputFolder)))
            {
                previous[Path.GetFullPath(document.SourcePath)] = new PreviousOutput(document.OutputFolder, document.DisplayName, document.SheetFiles);
            }

            var paths = documents.Select(d => d.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return service.RunAsync(
                new ConvertRequest
                {
                    Inputs = [.. paths.Select(p => new PlannedInput(p))],
                    // The run is one of the inputs of the screen (FR-130), so that the list shows it when the screen is opened again.
                    GivenInputs = snapshot.Input.Paths.Count > 0 ? snapshot.Input.Paths : paths,
                    OutputFolder = snapshot.Output.Folder,
                    Options = ConvertRequest.OptionsFrom(snapshot),
                    Previous = previous,
                },
                token);
        });
    }

    /// <summary>Puts the page range in the options when it can be used (one file); otherwise it is left out.</summary>
    private OptionsSnapshot WithPages(OptionsSnapshot options)
    {
        options.Pages = IsOneFile && !string.IsNullOrWhiteSpace(PageRangeText) ? PageRangeText.Trim() : null;
        return options;
    }

    // The earlier run shown on the screen is the one to resume, whatever the input list holds (FR-013f).
    private Task ResumeAsync(ResumeMode mode)
    {
        if (!ConfirmSending())
        {
            return Task.CompletedTask;
        }

        var jobId = _previous?.Job.JobId;
        return RunAsync((service, snapshot, token) =>
        service.ResumeAsync(
            new ResumeRequest
            {
                OutputFolder = snapshot.Output.Folder,
                JobId = jobId,
                Mode = mode,
                Step2 = snapshot.Steps.Step2,
                Step3 = snapshot.Steps.Step3,
                VlmSeparate = snapshot.Steps.VlmSeparate,
                Step1Tables = snapshot.Steps.Step1Tables,
                PageImage = snapshot.Steps.PageImage,
                DropRepeatedTables = snapshot.Steps.DropRepeatedTables,
                Step3Mode = snapshot.Steps.Step3Mode,
                StructureNote = snapshot.Output.StructureNote,
                Chunks = snapshot.Output.Chunks,
                ExcelPerSheet = snapshot.Output.ExcelPerSheet,
            },
            token));
    }

    /// <summary>
    /// "Resume" per file (FR-157): the files of each earlier conversion (job) go on in turn, each from where it is (going on, retrying,
    /// adding a step); then the files not converted yet are converted as one new run with the conditions of the screen. The list is one
    /// for all of them. The states are looked for again just before, so that a file another process runs now is not taken.
    /// </summary>
    private async Task ResumeFilesAsync()
    {
        // The question before sending is about the files as they are now, not as the list showed them (a file may have changed since).
        var current = _store.Current;
        IReadOnlyList<string> inputs = [.. current.Input.Paths];
        IReadOnlyList<DocumentKind> types = [.. current.Input.Types];
        var (folder, recurse, layout, step2, step3) = (current.Output.Folder, current.Input.Recurse, current.Output.FolderLayout, current.Steps.Step2, current.Steps.Step3);
        var planned = FileStates.ToResume(await Task.Run(() => FileStates.Find(folder, inputs, types, recurse, layout, step2, step3)));
        if (IsRunning)
        {
            return;
        }

        var reads3 = FileStates.ReadsStep3(planned, step3);
        if (!ConfirmSending(reads3))
        {
            return;
        }

        var asked = reads3 || !SendingNeedsQuestion();
        var notConverted = planned.Where(e => e.State == FileState.NotConverted).Select(e => e.Path).ToList();

        await RunAsync(async (service, snapshot, token) =>
        {
            var entries = FileStates.Find(
                snapshot.Output.Folder, snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse, snapshot.Output.FolderLayout, snapshot.Steps.Step2, snapshot.Steps.Step3);
            var take = FileStates.ToResume(entries);
            if (take.Count == 0)
            {
                throw new ConvertStartException(UiText.Get("Status_Files_NothingToResume"));
            }

            if (!asked && FileStates.ReadsStep3(take, snapshot.Steps.Step3))
            {
                // A file changed after the question: it would now send pages that were not asked about.
                throw new ConvertStartException(UiText.Get("Status_Files_ChangedBeforeStart"));
            }

            var options = WithPages(ConvertRequest.OptionsFrom(snapshot));
            var documents = new List<DocumentRecord>();
            ConvertResult? last = null;
            foreach (var group in take.Where(e => e.Job is not null).GroupBy(e => e.Job!.JobId))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var ids = group.Select(e => e.Document!.DocId).ToHashSet(StringComparer.Ordinal);
                last = await service.ResumeAsync(
                    new ResumeRequest
                    {
                        OutputFolder = snapshot.Output.Folder,
                        JobId = group.Key,
                        Mode = ResumeMode.Files,
                        DocumentIds = ids,
                        Step2 = snapshot.Steps.Step2,
                        Step3 = snapshot.Steps.Step3,
                        VlmSeparate = snapshot.Steps.VlmSeparate,
                        Step1Tables = snapshot.Steps.Step1Tables,
                        PageImage = snapshot.Steps.PageImage,
                        DropRepeatedTables = snapshot.Steps.DropRepeatedTables,
                        Step3Mode = snapshot.Steps.Step3Mode,
                        StructureNote = snapshot.Output.StructureNote,
                        Chunks = snapshot.Output.Chunks,
                        ExcelPerSheet = snapshot.Output.ExcelPerSheet,
                        Connection = options.Connection,
                        PromptNames = options.PromptNames,
                    },
                    token);
                documents.AddRange(last.Documents.Where(d => ids.Contains(d.DocId)));
                if (token.IsCancellationRequested || last.Job.StopReason is not null)
                {
                    return new ConvertResult(last.Job, documents); // the jobs not started yet and the new files stay as they were
                }
            }

            var fresh = take.Where(e => e.State == FileState.NotConverted).Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fresh.Count > 0 && !token.IsCancellationRequested)
            {
                var found = InputEnumerator.Enumerate(snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse, snapshot.Output.FolderLayout);
                last = await service.RunAsync(
                    new ConvertRequest
                    {
                        Inputs = [.. found.Inputs.Where(i => fresh.Contains(Path.GetFullPath(i.Path)))],
                        GivenInputs = snapshot.Input.Paths,
                        InputFolder = found.InputFolder,
                        OutputFolder = snapshot.Output.Folder,
                        Options = options,
                    },
                    token);
                documents.AddRange(last.Documents);
            }

            return last is null ? throw new OperationCanceledException(token) : new ConvertResult(last.Job, documents);
        }, fill: false, readsStep3: reads3);

        // The inputs whose files were converted for the first time remember this output folder, when they had none (FR-156).
        var newInputs = InputPaths.Where(i => notConverted.Any(p => IsSameOrUnder(p, i))).ToList();
        RememberOutputs(onlyNew: true, only: newInputs);

        // The list shows the state of each file after the run (FR-157), with the summary of the run before it.
        await RefreshPreviousAsync(showFiles: true, appendToStatus: true);
    }

    /// <summary>
    /// The files of the inputs whose last record another process converts now (FR-157): no action takes them, so that two runs do not write
    /// the same folder. Looked for again just before the run.
    /// </summary>
    private static HashSet<string> RunElsewhere(AppSettings snapshot) =>
        [.. FileStates.Find(snapshot.Output.Folder, snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse, snapshot.Output.FolderLayout, snapshot.Steps.Step2, snapshot.Steps.Step3)
            .Where(e => e.State == FileState.RunningElsewhere)
            .Select(e => e.Path)];

    private static bool IsSameOrUnder(string path, string root)
    {
        var full = HistorySettings.Normalize(root);
        return string.Equals(HistorySettings.Normalize(path), full, StringComparison.OrdinalIgnoreCase)
            || HistorySettings.Normalize(path).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A step switched on or off changes "Step2/3 not run", and the subfolders, the kinds and the folder layout change the files (FR-157): the
    /// list is made again, also when it shows the rows of the run that has just ended.
    /// </summary>
    private void RefreshEntriesForSteps()
    {
        if (!IsRunning && InputPaths.Count > 0)
        {
            _ = RefreshPreviousAsync(showFiles: true);
        }
    }

    private Task InterruptAsync()
    {
        SetStatus(() => UiText.Get("Status_Interrupting"));
        _cancellation?.Cancel();
        return Task.CompletedTask;
    }

    /// <param name="fill">The list is filled with the files of the run when it is planned; without it the rows of the list are updated (FR-157).</param>
    /// <param name="readsStep3">
    /// Whether the run reads pages with the VLM, when it is known better than by Step3 on the screen ("Resume" per file, FR-157): a run that
    /// reads none does not need a connection, though Step3 is on (its files have it done).
    /// </param>
    private async Task RunAsync(Func<ConvertService, AppSettings, CancellationToken, Task<ConvertResult>> run, Action<string>? afterPlanned = null, bool fill = true, bool? readsStep3 = null)
    {
        _store.KeepConditions();
        // The run uses a copy, so a change on the screen during the run does not change a run in progress.
        var snapshot = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(_store.Current))!;
        if (readsStep3 == false)
        {
            snapshot.Steps.Step3 = false; // nothing is read with the VLM: no connection is needed, and no step is added
        }

        var errors = snapshot.ValidateForRun();
        if (errors.Count > 0)
        {
            SetStatus(() => UiText.Get("Status_CheckSettings", string.Join(" / ", errors)));
            if (snapshot.Input.Types.Count == 0)
            {
                OpenCondition("types");
            }
            else if (snapshot.Images.Dpi is < SettingsLimits.MinDpi or > SettingsLimits.MaxDpi)
            {
                OpenCondition("dpi");
            }

            return;
        }

        // The VLM connection is made when a connection is chosen: step 3 may be on, or an earlier run may have used it.
        VlmSetup? vlm = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Vlm.Selected))
        {
            var (setup, setupError) = VlmSetupFactory.Create(snapshot);
            if (setup is null && snapshot.Steps.Step3)
            {
                var notReady = setupError ?? UiText.Get("Status_VlmNotReady");
                SetStatus(() => notReady);
                ShowStopped?.Invoke(notReady);
                return;
            }

            vlm = setup;
        }

        var engine = EngineFactory.Create(snapshot.Engine);
        var service = new ConvertService(engine, vlm: vlm, extractors: snapshot.Extractors);
        using var feed = new FileStatusFeed();
        feed.Updated += rows => _dispatcher.BeginInvoke(() => Apply(rows));
        service.Planned += documents =>
        {
            if (fill)
            {
                _dispatcher.Invoke(() => Fill(documents));
            }

            afterPlanned?.Invoke(snapshot.Output.Folder);
        };
        service.DocumentChanged += feed.Post;

        _cancellation = new CancellationTokenSource();
        _runFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanVersion++; // a search for the earlier run that is still going is out of date
        IsRunning = true;
        SetStatus(() => UiText.Get("Status_Running"));
        string? stoppedMessage = null;
        Guid? ranJob = null;
        try
        {
            var result = await Task.Run(() => run(service, snapshot, _cancellation.Token));
            ranJob = result.Job.JobId;
            feed.Flush();
            SetStatus(() => Summary(result));
            if (result.WasInterrupted && result.Job.StopReason is { } stopReason)
            {
                stoppedMessage = stopReason;
            }
        }
        catch (JobNotFoundException ex)
        {
            SetStatus(() => UiText.Get("Status_NothingToResume", ex.Message));
        }
        catch (ConvertStartException ex)
        {
            SetStatus(() => ex.Message);
            if (ex.Item == ConvertStartException.EngineItem)
            {
                OpenCondition("engine");
            }
            else if (snapshot.Steps.Step3)
            {
                stoppedMessage = ex.Message; // the check of the VLM connection (or the missing connection) is the reason
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(() => UiText.Get("Status_CouldNotStart", ex.Message));
        }
        finally
        {
            // Closing Office can take seconds (and much longer when it is stuck): not on the thread of the screen, so that the window answers.
            await Task.Run(() =>
            {
                (engine as IDisposable)?.Dispose();
                vlm?.Dispose();
            }).ConfigureAwait(true);
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            _runFinished?.TrySetResult();
        }

        if (stoppedMessage is not null)
        {
            ShowStopped?.Invoke(stoppedMessage);
        }

        // What can be resumed has changed with this run (when it did not start, nothing has changed).
        if (ranJob is { } jobId)
        {
            await RefreshPreviousAsync(showFiles: false, jobId: jobId);
        }
    }

    private bool CanStart() =>
        !IsRunning
        && InputPaths.Count > 0
        && !string.IsNullOrWhiteSpace(_store.Current.Output.Folder)
        && _store.Current.Input.Types.Count > 0;

    private void UpdateRunCommands()
    {
        RaiseEditState();
        StartCommand.RaiseCanExecuteChanged();
        StartChangedCommand.RaiseCanExecuteChanged();
        RedoAllCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RereadCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanReread));
    }

    /// <summary>
    /// Looks in the output folder for the earlier run (FR-013e). It decides whether "resume" and "retry failed only" can be
    /// pressed and, when <paramref name="showFiles"/> is set, shows the files of that run in the list.
    /// </summary>
    /// <param name="inputsChanged">The inputs changed (or the screen starts): the output folder may follow the first input (FR-156).</param>
    private async Task RefreshPreviousAsync(bool showFiles, bool appendToStatus = false, Guid? jobId = null, bool inputsChanged = false)
    {
        var version = ++_scanVersion;
        IsRefreshingFiles = true;
        try
        {
            await RefreshPreviousCoreAsync(version, showFiles, appendToStatus, jobId, inputsChanged);
        }
        finally
        {
            if (version == _scanVersion)
            {
                IsRefreshingFiles = false;
            }
        }
    }

    private async Task RefreshPreviousCoreAsync(int version, bool showFiles, bool appendToStatus, Guid? jobId, bool inputsChanged)
    {
        var folder = _store.Current.Output.Folder;
        IReadOnlyList<string> inputs = [.. InputPaths];
        var notes = new List<string>();
        if (_pendingNote is { } pending && showFiles)
        {
            notes.Add(pending);
            _pendingNote = null;
        }

        if (showFiles && jobId is null && !_restoreSuspended)
        {
            // FR-156: the first input, and the output folder it was converted into.
            var history = _store.Current.History;
            var (first, missing) = await Task.Run(() =>
            {
                var found = PreviousState.FindFirst(folder, inputs, history, out var gone);
                return (found, gone);
            });
            if (version != _scanVersion || IsRunning)
            {
                return;
            }

            if (first is { Remembered: true } && !inputsChanged)
            {
                first = null; // the output folder was chosen: it is kept until the inputs change
            }
            else if (first is { Remembered: true } remembered)
            {
                folder = remembered.Output;
                _store.Update(s => s.Output.Folder = remembered.Output);
                Raise(nameof(OutputFolder));
                notes.Add(UiText.Get("Status_OutputSwitched", remembered.Output));
            }
            else if (first is null && missing is not null && inputsChanged)
            {
                notes.Add(UiText.Get("Status_OutputMissing", missing));
            }

            // FR-158: the conditions follow the first; they come back when no file of the list has a record.
            var key = first is null ? null : HistorySettings.Normalize(first.Input) + "|" + HistorySettings.Normalize(folder);
            if (!string.Equals(key, _firstKey, StringComparison.OrdinalIgnoreCase))
            {
                _firstKey = key;
                if (first is not null)
                {
                    var input = first.Input;
                    var output = folder;
                    var recorded = await Task.Run(() => PreviousState.ConditionsOf(output, input));
                    if (version != _scanVersion || IsRunning)
                    {
                        _firstKey = null;
                        return;
                    }

                    if (recorded is not null)
                    {
                        if (!_store.ShowConditions(recorded.Conditions))
                        {
                            notes.Add(UiText.Get("Status_ConnectionMissing", recorded.Conditions.Connection));
                        }

                        if (recorded.OthersDiffer > 0)
                        {
                            notes.Add(UiText.Get("Status_OtherConditions", recorded.OthersDiffer));
                        }

                        RaiseAllConditions();
                    }
                }
                else if (_store.RevertConditions())
                {
                    RaiseAllConditions();
                }
            }
        }

        var current = _store.Current;
        IReadOnlyList<DocumentKind> types = [.. current.Input.Types];
        var (recurse, layout, step2, step3) = (current.Input.Recurse, current.Output.FolderLayout, current.Steps.Step2, current.Steps.Step3);

        // The run of the inputs of the screen (FR-130). Without a run to resume or retry, the newest run that finished is shown (FR-129).
        // "other": the output folder has a run of other inputs that can be resumed; it is not shown, but the status tells where to find it.
        var entries = await Task.Run(() => FileStates.Find(folder, inputs, types, recurse, layout, step2, step3));
        var (run, finished, other) = await Task.Run(() =>
        {
            if (jobId is { } id)
            {
                // The run that has just ended, whatever its inputs (a run of a past output is resumed with other inputs on the screen).
                var store = new StateStore(folder);
                var ran = store.LoadJob(id) is { } job ? PreviousRunFinder.ForJob(store, job) : null;
                return (ran is { CanResume: true } or { CanRetryFailed: true } ? ran : null, (PreviousRun?)null, false);
            }

            if (inputs.Count == 0)
            {
                return (null, null, false);
            }

            if (PreviousRunFinder.Find(folder, inputs) is { } found)
            {
                return (found, null, false);
            }

            var done = PreviousRunFinder.LatestCompleted(folder, inputs);
            return ((PreviousRun?)null, done, done is null && PreviousRunFinder.Find(folder) is not null);
        });
        if (version != _scanVersion || IsRunning)
        {
            return;
        }

        _previous = run;
        _entries = entries;
        ScheduleElsewhereCheck();
        if (showFiles && jobId is null && entries.Count > 0)
        {
            // FR-157: each file of the inputs with its last record.
            Files.Clear();
            foreach (var entry in entries)
            {
                Files.Add(new FileItemViewModel(entry, OutputRoot));
            }

            string? text = null;
            if (entries.Any(e => e.Document is not null))
            {
                var counted = entries.Where(e => e.State != FileState.RunningElsewhere).ToList();
                var (done, resume, fresh, changed) = (
                    counted.Count(e => e.State is FileState.Done or FileState.DonePartlyFailed),
                    counted.Count(e => e.CanResume),
                    counted.Count(e => e.State == FileState.NotConverted),
                    counted.Count(e => e.State == FileState.Changed));
                var elsewhere = entries.Count - counted.Count;
                text = UiText.Get("Status_Files", done, resume, fresh, changed)
                    + (elsewhere > 0 ? " " + UiText.Get("Status_Files_Elsewhere", elsewhere) : string.Empty);
            }
            else if (other)
            {
                text = UiText.Get("Status_Previous_Other");
            }

            if (notes.Count > 0)
            {
                text = string.Join(" ", notes) + (text is null ? string.Empty : " " + text);
            }

            if (text is not null)
            {
                var before = _statusSource;
                var keepEarlier = appendToStatus && Status != UiText.Get("Status_Ready");
                SetStatus(() => keepEarlier ? $"{before()} / {text}" : text);
            }
            else if (!appendToStatus)
            {
                SetStatus(() => UiText.Get("Status_Ready"));
            }
        }
        else if (showFiles)
        {
            Files.Clear();
            if (finished is not null)
            {
                foreach (var document in finished.Documents)
                {
                    Files.Add(new FileItemViewModel(FileStatusProjector.Project(document), OutputRoot));
                }

                var count = finished.Documents.Count;
                var before = _statusSource;
                var keepEarlier = appendToStatus && Status != UiText.Get("Status_Ready");
                SetStatus(() => keepEarlier ? $"{before()} / {UiText.Get("Status_Previous_Done", count)}" : UiText.Get("Status_Previous_Done", count));
            }
            else if (run is not null)
            {
                foreach (var document in run.Documents)
                {
                    Files.Add(new FileItemViewModel(FileStatusProjector.Project(document), OutputRoot));
                }

                string Text() => run.CanResume
                    ? UiText.Get("Status_Previous_Resume", run.DoneCount, run.FailedCount, run.UnfinishedCount)
                    : UiText.Get("Status_Previous_Retry", run.DoneCount, run.FailedCount, run.UnfinishedCount);
                var before = _statusSource;
                var keepEarlier = appendToStatus && Status != UiText.Get("Status_Ready");
                SetStatus(() => keepEarlier ? $"{before()} / {Text()}" : Text());
            }
            else if (other)
            {
                var before = _statusSource;
                var keepEarlier = appendToStatus && Status != UiText.Get("Status_Ready");
                SetStatus(() => keepEarlier ? $"{before()} / {UiText.Get("Status_Previous_Other")}" : UiText.Get("Status_Previous_Other"));
            }
            else if (!appendToStatus)
            {
                SetStatus(() => UiText.Get("Status_Ready")); // the text about the conversion that was shown is out of date
            }
        }

        UpdateRunCommands();
    }

    /// <summary>
    /// Opens files of the window of past outputs (FR-121, FR-128): their source files become the inputs, their earlier conversions fill the
    /// list of files, and the output folder of the screen becomes <paramref name="outputRoot"/>. Nothing is converted; the user starts it.
    /// When <paramref name="resumeJobId"/> is given (one file whose run can be resumed), that run is "the previous conversion", so that "resume"
    /// and "retry failed only" work on it. Returns false when a conversion is running.
    /// </summary>
    public async Task<bool> OpenPastAsync(string outputRoot, IReadOnlyList<DocumentRecord> documents, Guid? resumeJobId = null)
    {
        if (IsRunning || documents.Count == 0)
        {
            return false;
        }

        var version = ++_scanVersion;
        if (!string.Equals(_store.Current.Output.Folder, outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            _store.Update(s => s.Output.Folder = outputRoot);
            Raise(nameof(OutputFolder));
        }

        _keepList = true;
        try
        {
            InputPaths.Clear();
            foreach (var path in documents.Select(d => d.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                InputPaths.Add(path);
            }
        }
        finally
        {
            _keepList = false;
        }

        SaveInputs();
        _entries = [];
        Files.Clear();
        foreach (var document in documents)
        {
            Files.Add(new FileItemViewModel(FileStatusProjector.Project(document), OutputRoot));
        }

        // The run that "resume" acts on: the chosen one, or else the one the output folder has (as when the folder is chosen).
        var run = await Task.Run(() =>
        {
            var store = new StateStore(outputRoot);
            return resumeJobId is { } id && store.LoadJob(id) is { } job ? PreviousRunFinder.ForJob(store, job) : PreviousRunFinder.Find(outputRoot);
        });
        if (version != _scanVersion || IsRunning)
        {
            return true;
        }

        _previous = run;
        var count = documents.Count;
        SetStatus(() =>
        {
            var opened = UiText.Get("Status_PastOpened", count);
            return run switch
            {
                { CanResume: true } => opened + " / " + UiText.Get("Status_Previous_Resume", run.DoneCount, run.FailedCount, run.UnfinishedCount),
                { CanRetryFailed: true } => opened + " / " + UiText.Get("Status_Previous_Retry", run.DoneCount, run.FailedCount, run.UnfinishedCount),
                _ => opened,
            };
        });
        UpdateRunCommands();
        return true;
    }

    private static string Summary(ConvertResult result)
    {
        var done = result.Documents.Count(d => d.IsDone());
        var failed = result.Documents.Count(d => d.IsFailed());
        var rest = result.Documents.Count - done - failed;
        var head = result.WasInterrupted ? UiText.Get("Status_Head_Interrupted") : failed > 0 ? UiText.Get("Status_Head_DoneWithFailures") : UiText.Get("Status_Head_Done");
        var summary = UiText.Get("Status_Summary", head, done, failed, rest, result.Job.OutputRoot);
        return result.WasInterrupted && result.Job.StopReason is { } reason ? summary + " " + reason : summary;
    }

    /// <summary>Fills the list with every file of the run (waiting), before the first step starts.</summary>
    private void Fill(IReadOnlyList<DocumentRecord> documents)
    {
        Files.Clear();
        foreach (var document in documents)
        {
            Files.Add(new FileItemViewModel(FileStatusProjector.Project(document), OutputRoot));
        }
    }

    private string OutputRoot() => _store.Current.Output.Folder;

    private void Apply(IReadOnlyList<FileRow> rows)
    {
        foreach (var row in rows)
        {
            var item = string.IsNullOrEmpty(row.FullPath)
                ? Files.FirstOrDefault(f => f.DocId == row.DocId)
                : Files.FirstOrDefault(f => string.Equals(f.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                Files.Add(new FileItemViewModel(row, OutputRoot));
            }
            else
            {
                item.Update(row);
            }
        }

        RaiseProgress();
    }

    /// <summary>The files of the list that are finished (well or not), for the bar of the progress of a run.</summary>
    public int FinishedCount => Files.Count(f => f.IsFinished);

    /// <summary>The bar of the progress is shown while a run goes on.</summary>
    public System.Windows.Visibility ProgressVisibility => IsRunning && Files.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>"3 / 15": the files finished and the files of the run.</summary>
    public string ProgressText => $"{FinishedCount} / {Files.Count}";

    private void RaiseProgress()
    {
        Raise(nameof(FinishedCount));
        Raise(nameof(ProgressVisibility));
        Raise(nameof(ProgressText));
    }
}

/// <summary>An engine the combo box offers.</summary>
public sealed record EngineChoice(EngineKind Kind, string Name);
