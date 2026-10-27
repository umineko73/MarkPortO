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
    private FileItemViewModel? _selected;
    private PreviousRun? _previous;
    private int _scanVersion;

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
        }

        _status = _statusSource();

        StartCommand = new RelayCommand(StartAsync, CanStart);
        InterruptCommand = new RelayCommand(InterruptAsync, () => IsRunning);
        ResumeCommand = new RelayCommand(() => ResumeAsync(ResumeMode.Unfinished), () => !IsRunning && _previous is { CanResume: true });
        RetryFailedCommand = new RelayCommand(() => ResumeAsync(ResumeMode.RetryFailed), () => !IsRunning && _previous is { CanRetryFailed: true });
        AddFilesCommand = new RelayCommand(AddFilesAsync, () => !IsRunning);
        AddFolderCommand = new RelayCommand(AddFolderAsync, () => !IsRunning);
        ClearInputsCommand = new RelayCommand(ClearInputsAsync, () => !IsRunning);
        RemoveSelectedInputsCommand = new RelayCommand(RemoveSelectedInputsAsync, () => !IsRunning && SelectedInputPaths.Count > 0);
        BrowseOutputCommand = new RelayCommand(BrowseOutputAsync, () => !IsRunning);
        ConnectionSettingsCommand = new RelayCommand(OpenConnectionSettingsAsync, () => !IsRunning);
        PromptsCommand = new RelayCommand(() => { ShowPrompts?.Invoke(); return Task.CompletedTask; }, () => !IsRunning);

        InputPaths.CollectionChanged += (_, _) => UpdateRunCommands();
        _ = RefreshPreviousAsync(showFiles: true, appendToStatus: true); // the earlier run in the output folder (FR-013e)
    }

    /// <summary>Opens the window for VLM connections (set by the window).</summary>
    public Action? ShowConnectionSettings { get; set; }

    public Action? ShowPrompts { get; set; }

    // ---- what the view needs to show dialogs (set by the window) ----

    public Func<FolderPurpose, string?>? PickFolder { get; set; }

    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

    /// <summary>Asks a yes/no question with the given text; true for yes (set by the window).</summary>
    public Func<string, bool>? Confirm { get; set; }

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

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value))
            {
                foreach (var command in new[] { StartCommand, InterruptCommand, ResumeCommand, RetryFailedCommand, AddFilesCommand, AddFolderCommand, ClearInputsCommand, RemoveSelectedInputsCommand, BrowseOutputCommand, ConnectionSettingsCommand, PromptsCommand })
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

    public bool Recurse
    {
        get => _store.Current.Input.Recurse;
        set
        {
            _store.Update(s => s.Input.Recurse = value);
            Raise();
        }
    }

    public bool IncludeHidden
    {
        get => _store.Current.Input.IncludeHidden;
        set
        {
            _store.Update(s => s.Input.IncludeHidden = value);
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
            }
        }
    }

    // ---- commands ----

    public RelayCommand StartCommand { get; }

    public RelayCommand InterruptCommand { get; }

    public RelayCommand ResumeCommand { get; }

    public RelayCommand RetryFailedCommand { get; }

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

    /// <summary>Step 3: read the page images with the VLM.</summary>
    public bool Step3
    {
        get => _store.Current.Steps.Step3;
        set
        {
            _store.Update(s => s.Steps.Step3 = value);
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

    public void Dispose()
    {
        _cancellation?.Cancel();
        _store.Dispose();
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
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

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
        UpdateRunCommands();
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
        if (new[] { prompts.Docx, prompts.Xlsx, prompts.Pptx }.Any(PromptSupplement.UsesPageText))
        {
            limits = UiText.Get("Confirm_StartExternalText") + limits;
        }

        return confirm(UiText.Get("Confirm_StartExternal", InputPaths.Count, ExternalConsent.Destination(connection), limits));
    }

    private Task StartAsync()
    {
        if (!ConfirmSending())
        {
            return Task.CompletedTask;
        }

        // An interrupted run is still waiting in the output folder: ask before a new one is started (FR-013g).
        if (_previous is { CanResume: true } previous
            && Confirm is { } confirm
            && !confirm(UiText.Get("Confirm_StartWithInterrupted", previous.DoneCount, previous.FailedCount, previous.UnfinishedCount)))
        {
            return Task.CompletedTask;
        }

        // The interrupted runs that this one replaces are given up once it has really started (FR-013g).
        var replaced = _previous is { CanResume: true }
            ? new StateStore(_store.Current.Output.Folder).FindResumableJobs(null, includeFinishedWithErrors: false).Select(j => j.JobId).ToList()
            : [];
        return StartRunAsync(replaced);
    }

    private Task StartRunAsync(IReadOnlyList<Guid> replaced) => RunAsync(async (service, snapshot, token) =>
    {
        var found = InputEnumerator.Enumerate(snapshot.Input.Paths, snapshot.Input.Types, snapshot.Input.Recurse);
        if (found.Inputs.Count == 0)
        {
            var reasons = found.Warnings.Count > 0 ? " " + string.Join(" ", found.Warnings) : string.Empty;
            throw new ConvertStartException(UiText.Get("Status_NoDocuments", reasons));
        }

        return await service.RunAsync(
            new ConvertRequest
            {
                Inputs = found.Inputs,
                GivenInputs = snapshot.Input.Paths,
                InputFolder = found.InputFolder,
                OutputFolder = snapshot.Output.Folder,
                Options = ConvertRequest.OptionsFrom(snapshot),
            },
            token);
    }, outputFolder => new StateStore(outputFolder).Abandon(replaced));

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
            return;
        }

        // The VLM connection is made when a connection is chosen: step 3 may be on, or an earlier run may have used it.
        VlmSetup? vlm = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Vlm.Selected))
        {
            var (setup, setupError) = VlmSetupFactory.Create(snapshot);
            if (setup is null && snapshot.Steps.Step3)
            {
                SetStatus(() => setupError ?? UiText.Get("Status_VlmNotReady"));
                return;
            }

            vlm = setup;
        }

        var engine = EngineFactory.Create(snapshot.Engine);
        var service = new ConvertService(engine, vlm: vlm);
        using var feed = new FileStatusFeed();
        feed.Updated += rows => _dispatcher.BeginInvoke(() => Apply(rows));
        service.Planned += documents =>
        {
            _dispatcher.Invoke(() => Fill(documents));
            afterPlanned?.Invoke(snapshot.Output.Folder);
        };
        service.DocumentChanged += feed.Post;

        _cancellation = new CancellationTokenSource();
        _scanVersion++; // a search for the earlier run that is still going is out of date
        IsRunning = true;
        SetStatus(() => UiText.Get("Status_Running"));
        try
        {
            var result = await Task.Run(() => run(service, snapshot, _cancellation.Token));
            feed.Flush();
            SetStatus(() => Summary(result));
        }
        catch (JobNotFoundException ex)
        {
            SetStatus(() => UiText.Get("Status_NothingToResume", ex.Message));
        }
        catch (ConvertStartException ex)
        {
            SetStatus(() => ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(() => UiText.Get("Status_CouldNotStart", ex.Message));
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
            vlm?.Dispose();
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
        }

        // What can be resumed has changed with this run.
        await RefreshPreviousAsync(showFiles: false);
    }

    private bool CanStart() =>
        !IsRunning
        && InputPaths.Count > 0
        && !string.IsNullOrWhiteSpace(_store.Current.Output.Folder)
        && _store.Current.Input.Types.Count > 0;

    private void UpdateRunCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Looks in the output folder for the earlier run (FR-013e). It decides whether "resume" and "retry failed only" can be
    /// pressed and, when <paramref name="showFiles"/> is set, shows the files of that run in the list.
    /// </summary>
    private async Task RefreshPreviousAsync(bool showFiles, bool appendToStatus = false)
    {
        var version = ++_scanVersion;
        var folder = _store.Current.Output.Folder;
        var run = await Task.Run(() => PreviousRunFinder.Find(folder));
        if (version != _scanVersion || IsRunning)
        {
            return;
        }

        _previous = run;
        if (showFiles)
        {
            Files.Clear();
            if (run is not null)
            {
                foreach (var document in run.Documents)
                {
                    Files.Add(new FileItemViewModel(FileStatusProjector.Project(document)));
                }

                string Text() => run.CanResume
                    ? UiText.Get("Status_Previous_Resume", run.DoneCount, run.FailedCount, run.UnfinishedCount)
                    : UiText.Get("Status_Previous_Retry", run.DoneCount, run.FailedCount, run.UnfinishedCount);
                var before = _statusSource;
                var keepEarlier = appendToStatus && Status != UiText.Get("Status_Ready");
                SetStatus(() => keepEarlier ? $"{before()} / {Text()}" : Text());
            }
        }

        UpdateRunCommands();
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
            Files.Add(new FileItemViewModel(FileStatusProjector.Project(document)));
        }
    }

    private void Apply(IReadOnlyList<FileRow> rows)
    {
        foreach (var row in rows)
        {
            var item = Files.FirstOrDefault(f => f.DocId == row.DocId);
            if (item is null)
            {
                Files.Add(new FileItemViewModel(row));
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
