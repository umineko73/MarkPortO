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
    private string _dpiText;
    private string _pageRangeText = string.Empty;
    private FileItemViewModel? _selected;
    private PreviousRun? _previous;
    private int _scanVersion;

    // The inputs are being set together with the list of files (a past output is opened): the change of the inputs does not refill the list.
    private bool _keepList;

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
            if (e.PropertyName is nameof(Recurse) or nameof(TypeDocx) or nameof(TypeXlsx) or nameof(TypePptx) or nameof(TypePdf)
                or nameof(SelectedEngine) or nameof(DpiText) or nameof(Emphasis)
                or nameof(ExtractorDocx) or nameof(ExtractorXlsx) or nameof(ExtractorPptx) or nameof(ExtractorPdf))
            {
                Raise(nameof(ConditionSummaryText));
            }
        };

        // The four ways to convert; the list of files decides which can be pressed (FR-129):
        // empty: "Start". Something not finished: "Resume" and "Redo all". Everything done: "Redo all" and "Changed files only".
        StartCommand = new RelayCommand(StartAllAsync, () => CanStart() && Files.Count == 0);
        ResumeCommand = new RelayCommand(
            () => ResumeAsync(_previous is { CanResume: true } ? ResumeMode.Unfinished : ResumeMode.RetryFailed),
            () => !IsRunning && HasPrevious);
        RedoAllCommand = new RelayCommand(StartAllAsync, () => CanStart() && Files.Count > 0);
        StartChangedCommand = new RelayCommand(() => StartAsync(RunKind.ChangedOnly), () => CanStart() && Files.Count > 0 && Files.All(f => f.IsDone));
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
                _ = RefreshPreviousAsync(showFiles: true);
            }
        };
        Files.CollectionChanged += (_, _) => UpdateRunCommands();
        _ = RefreshPreviousAsync(showFiles: true, appendToStatus: true); // the earlier run in the output folder (FR-013e)
    }

    /// <summary>Opens the window for VLM connections (set by the window).</summary>
    public Action? ShowConnectionSettings { get; set; }

    public Action? ShowExtractorSettings { get; set; }

    public Action? ShowPrompts { get; set; }

    /// <summary>The choice of the prompt for each kind of file (FR-115).</summary>
    public IReadOnlyList<PromptChoiceViewModel> PromptChoices { get; } = [];

    /// <summary>The prompts were changed in their window: the choices are shown again.</summary>
    public void RefreshPrompts()
    {
        foreach (var choice in PromptChoices)
        {
            choice.Refresh();
        }
    }

    public bool CanEditPrompts => CanEdit && Step3;

    // ---- what the view needs to show dialogs (set by the window) ----

    public Func<FolderPurpose, string?>? PickFolder { get; set; }

    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>Asks a yes/no question with the given text; true for yes (set by the window).</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Tells the user, in a dialog, why a conversion did not start or stopped because of the VLM (the status bar alone is easy to miss).</summary>
    public Action<string>? ShowStopped { get; set; }

    // ---- state ----

    public ObservableCollection<string> InputPaths { get; }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public FileItemViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
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
    public bool HasPrevious => _previous is { CanResume: true } or { CanRetryFailed: true };

    /// <summary>
    /// The types, the range and the conversion (engine, DPI). A resume does not use them (it goes on with those of its run), but they can be
    /// changed while it can be done: "Redo all" uses them (FR-129).
    /// </summary>
    public bool CanEditNewRun => !IsRunning;

    /// <summary>"Write the results to a separate file" has a meaning only when Step 3 is on.</summary>
    public bool CanEditVlmSeparate => !IsRunning && Step3;

    public bool CanEditStep1Tables => !IsRunning && Step3;

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
        Raise(nameof(CanEditPrompts));
        Raise(nameof(CanEditPages));
        Raise(nameof(PagesTip));
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value))
            {
                RaiseEditState();
                foreach (var command in new[] { StartCommand, StartChangedCommand, RedoAllCommand, InterruptCommand, ResumeCommand, AddFilesCommand, AddFolderCommand, ClearInputsCommand, RemoveSelectedInputsCommand, BrowseOutputCommand, ConnectionSettingsCommand, ExtractorSettingsCommand, PromptsCommand })
                {
                    command.RaiseCanExecuteChanged();
                }
            }
        }
    }

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

    // The extractor for each kind of file (FR-111; experimental): the external commands are written in settings.json. The choices are shown only when there are some.
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
        Raise(nameof(ConnectionInUse));
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

    /// <summary>Write the VLM results to a separate file (FR-058).</summary>
    public bool VlmSeparate
    {
        get => _store.Current.Steps.VlmSeparate;
        set
        {
            _store.Update(s => s.Steps.VlmSeparate = value);
            Raise();
        }
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
    /// The two radio buttons beside the buttons are the two values.
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
                Raise(nameof(NewFolder));
            }
        }
    }

    public bool NewFolder
    {
        get => !SameFolder;
        set
        {
            if (value)
            {
                SameFolder = false;
            }
        }
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

    /// <summary>Step 3: read the page images with the VLM.</summary>
    public bool Step3
    {
        get => _store.Current.Steps.Step3;
        set
        {
            _store.Update(s => s.Steps.Step3 = value);
            Raise(nameof(CanEditVlmSeparate));
            Raise(nameof(CanEditStep1Tables));
            Raise(nameof(CanEditPrompts));
            Raise(nameof(PagesTip));
            Raise(nameof(PdfHintVisibility));
            Raise();
        }
    }

    /// <summary>The name of the VLM connection in use, for display.</summary>
    public string ConnectionInUse => string.IsNullOrEmpty(_store.Current.Vlm.Selected) ? UiText.Get("Conn_None") : _store.Current.Vlm.Selected;

    private Task OpenConnectionSettingsAsync()
    {
        ShowConnectionSettings?.Invoke();
        Raise(nameof(ConnectionInUse)); // the window may have changed the connection in use
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
        _store.Dispose();
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
        foreach (var file in PickFiles?.Invoke() ?? [])
        {
            AddInput(file);
        }

        return Task.CompletedTask;
    }

    private Task AddFolderAsync()
    {
        if (PickFolder?.Invoke(FolderPurpose.Input) is { } folder)
        {
            AddInput(folder);
        }

        return Task.CompletedTask;
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

        foreach (var path in paths)
        {
            if (System.IO.Directory.Exists(path) || (System.IO.File.Exists(path) && IsSupportedFile(path)))
            {
                AddInput(path);
            }
        }
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

    private void AddInput(string path)
    {
        if (!InputPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            InputPaths.Add(path);
            SaveInputs();
        }
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
        RaiseOptions();
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
    private bool ConfirmSending()
    {
        var settings = _store.Current;
        if (!settings.Steps.Step3
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

    private Task StartRunAsync(RunKind kind, IReadOnlyList<Guid> replaced) => RunAsync(async (service, snapshot, token) =>
    {
        var found = InputEnumerator.Enumerate(snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse);
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
            RunKind.ChangedOnly => PreviousOutputs.Load(snapshot.Output.Folder).ChangedOnly(request)
                ?? throw new ConvertStartException(UiText.Get("Status_NothingChanged", found.Inputs.Count)),
            RunKind.SameFolders => PreviousOutputs.Load(snapshot.Output.Folder).SameFolders(request),
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

        if (Confirm is { } confirm && !confirm(UiText.Get("Confirm_Reconvert", documents.Count)))
        {
            return Task.CompletedTask;
        }

        return RunAsync((service, snapshot, token) =>
        {
            var previous = new Dictionary<string, PreviousOutput>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents.Where(d => Directory.Exists(d.OutputFolder)))
            {
                previous[Path.GetFullPath(document.SourcePath)] = new PreviousOutput(document.OutputFolder, document.DisplayName);
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
                DropRepeatedTables = snapshot.Steps.DropRepeatedTables,
            },
            token));
    }

    private Task InterruptAsync()
    {
        SetStatus(() => UiText.Get("Status_Interrupting"));
        _cancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<ConvertService, AppSettings, CancellationToken, Task<ConvertResult>> run, Action<string>? afterPlanned = null)
    {
        // The run uses a copy, so a change on the screen during the run does not change a run in progress.
        var snapshot = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(_store.Current))!;
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
            _dispatcher.Invoke(() => Fill(documents));
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
    }

    /// <summary>
    /// Looks in the output folder for the earlier run (FR-013e). It decides whether "resume" and "retry failed only" can be
    /// pressed and, when <paramref name="showFiles"/> is set, shows the files of that run in the list.
    /// </summary>
    private async Task RefreshPreviousAsync(bool showFiles, bool appendToStatus = false, Guid? jobId = null)
    {
        var version = ++_scanVersion;
        var folder = _store.Current.Output.Folder;
        IReadOnlyList<string> inputs = [.. InputPaths];

        // The run of the inputs of the screen (FR-130). Without a run to resume or retry, the newest run that finished is shown (FR-129).
        // "other": the output folder has a run of other inputs that can be resumed; it is not shown, but the status tells where to find it.
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
        if (showFiles)
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
            var item = Files.FirstOrDefault(f => f.DocId == row.DocId);
            if (item is null)
            {
                Files.Add(new FileItemViewModel(row, OutputRoot));
            }
            else
            {
                item.Update(row);
            }
        }
    }
}

/// <summary>An engine the combo box offers.</summary>
public sealed record EngineChoice(EngineKind Kind, string Name);
