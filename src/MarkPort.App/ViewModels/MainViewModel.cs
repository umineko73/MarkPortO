// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
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

using MarkPort.App.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using MarkPort.Core.Convert;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Localization;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

namespace MarkPort.App.ViewModels;

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
    private bool _running;
    private string _dpiText;
    private FileItemViewModel? _selected;

    public MainViewModel(SettingsStore store, Dispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;

        var loaded = store.Load();
        var messages = loaded.Warnings.Concat(loaded.ValidationErrors).ToList();
        _status = messages.Count > 0 ? string.Join(" / ", messages) : UiText.Get("Status_Ready");

        var settings = store.Current;
        InputPaths = new ObservableCollection<string>(settings.Input.Paths);
        _dpiText = settings.Images.Dpi.ToString(CultureInfo.InvariantCulture);
        if (DetectEngines() is { } engineNote)
        {
            _status = messages.Count > 0 ? $"{_status} / {engineNote}" : engineNote;
        }

        StartCommand = new RelayCommand(StartAsync, () => !IsRunning);
        InterruptCommand = new RelayCommand(InterruptAsync, () => IsRunning);
        ResumeCommand = new RelayCommand(() => ResumeAsync(ResumeMode.Unfinished), () => !IsRunning);
        RetryFailedCommand = new RelayCommand(() => ResumeAsync(ResumeMode.RetryFailed), () => !IsRunning);
        AddFilesCommand = new RelayCommand(AddFilesAsync, () => !IsRunning);
        AddFolderCommand = new RelayCommand(AddFolderAsync, () => !IsRunning);
        ClearInputsCommand = new RelayCommand(ClearInputsAsync, () => !IsRunning);
        BrowseOutputCommand = new RelayCommand(BrowseOutputAsync, () => !IsRunning);
        ConnectionSettingsCommand = new RelayCommand(OpenConnectionSettingsAsync, () => !IsRunning);
        PromptsCommand = new RelayCommand(() => { ShowPrompts?.Invoke(); return Task.CompletedTask; }, () => !IsRunning);
    }

    /// <summary>Opens the window for VLM connections (set by the window).</summary>
    public Action? ShowConnectionSettings { get; set; }

    public Action? ShowPrompts { get; set; }

    // ---- what the view needs to show dialogs (set by the window) ----

    public Func<string?>? PickFolder { get; set; }

    public Func<IReadOnlyList<string>>? PickFiles { get; set; }

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

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value))
            {
                foreach (var command in new[] { StartCommand, InterruptCommand, ResumeCommand, RetryFailedCommand, AddFilesCommand, AddFolderCommand, ClearInputsCommand, BrowseOutputCommand, ConnectionSettingsCommand, PromptsCommand })
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
    private string? DetectEngines()
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
            return UiText.Get("Status_NoEngine");
        }
        else if (found.All(c => c.Kind != engine.Kind))
        {
            var fallback = found[0];
            _store.Update(s => s.Engine.Kind = fallback.Kind);
            return UiText.Get("Status_EngineChanged", engine.Kind == EngineKind.Office ? "Microsoft Office" : "LibreOffice", fallback.Name);
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
                Status = UiText.Get("Status_DpiSet", dpi);
            }
            else
            {
                Status = UiText.Get("Status_DpiInvalid", SettingsLimits.MinDpi, SettingsLimits.MaxDpi, _store.Current.Images.Dpi);
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

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand ConnectionSettingsCommand { get; }

    public RelayCommand PromptsCommand { get; }

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
        if (PickFolder?.Invoke() is { } folder)
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

    private Task BrowseOutputAsync()
    {
        if (PickFolder?.Invoke() is { } folder)
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
    }

    // ---- running ----

    private Task StartAsync() => RunAsync(async (service, snapshot, token) =>
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
    });

    private Task ResumeAsync(ResumeMode mode) => RunAsync((service, snapshot, token) =>
        service.ResumeAsync(
            new ResumeRequest
            {
                OutputFolder = snapshot.Output.Folder,
                GivenInputs = snapshot.Input.Paths,
                Mode = mode,
                Step2 = snapshot.Steps.Step2,
                Step3 = snapshot.Steps.Step3,
            },
            token));

    private Task InterruptAsync()
    {
        Status = UiText.Get("Status_Interrupting");
        _cancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<ConvertService, AppSettings, CancellationToken, Task<ConvertResult>> run)
    {
        // The run uses a copy, so a change on the screen during the run does not change a run in progress.
        var snapshot = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(_store.Current))!;
        var errors = snapshot.ValidateForRun();
        if (errors.Count > 0)
        {
            Status = UiText.Get("Status_CheckSettings", string.Join(" / ", errors));
            return;
        }

        // The VLM connection is made when a connection is chosen: step 3 may be on, or an earlier run may have used it.
        VlmSetup? vlm = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Vlm.Selected))
        {
            var (setup, setupError) = VlmSetupFactory.Create(snapshot);
            if (setup is null && snapshot.Steps.Step3)
            {
                Status = setupError ?? UiText.Get("Status_VlmNotReady");
                return;
            }

            vlm = setup;
        }

        var engine = EngineFactory.Create(snapshot.Engine);
        var service = new ConvertService(engine, vlm: vlm);
        using var feed = new FileStatusFeed();
        feed.Updated += rows => _dispatcher.BeginInvoke(() => Apply(rows));
        service.Planned += documents => _dispatcher.Invoke(() => Fill(documents));
        service.DocumentChanged += feed.Post;

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Status = UiText.Get("Status_Running");
        try
        {
            var result = await Task.Run(() => run(service, snapshot, _cancellation.Token));
            feed.Flush();
            Status = Summary(result);
        }
        catch (JobNotFoundException ex)
        {
            Status = UiText.Get("Status_NothingToResume", ex.Message);
        }
        catch (ConvertStartException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = UiText.Get("Status_CouldNotStart", ex.Message);
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
            vlm?.Dispose();
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    private static string Summary(ConvertResult result)
    {
        var done = result.Documents.Count(d => d.IsDone());
        var failed = result.Documents.Count(d => d.IsFailed());
        var rest = result.Documents.Count - done - failed;
        var head = result.WasInterrupted ? UiText.Get("Status_Head_Interrupted") : failed > 0 ? UiText.Get("Status_Head_DoneWithFailures") : UiText.Get("Status_Head_Done");
        return UiText.Get("Status_Summary", head, done, failed, rest, result.Job.OutputRoot);
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
