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
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;
using MarkPort.Core.Vlm.Command;

namespace MarkPort.App.ViewModels;

/// <summary>One connection as it is edited. Every change is written to settings.json at once (FR-018).</summary>
public sealed class ConnectionEditorViewModel : ObservableObject
{
    private readonly ConnectionProfile _profile;
    private readonly SettingsStore _store;
    private readonly Action _changed;

    public ConnectionEditorViewModel(ConnectionProfile profile, SettingsStore store, Action changed)
    {
        _profile = profile;
        _store = store;
        _changed = changed;
    }

    public ConnectionProfile Profile => _profile;

    public string Name
    {
        get => _profile.Name;
        set
        {
            var old = _profile.Name;
            if (value == old)
            {
                return;
            }

            _store.Update(s =>
            {
                _profile.Name = value;
                if (string.Equals(s.Vlm.Selected, old, StringComparison.OrdinalIgnoreCase))
                {
                    s.Vlm.Selected = value; // the selection follows a rename
                }
            });
            Raise();
            _changed();
        }
    }

    public string BaseUrl
    {
        get => _profile.BaseUrl;
        set => Change(value, _profile.BaseUrl, v => _profile.BaseUrl = v);
    }

    public string Model
    {
        get => _profile.Model;
        set => Change(value, _profile.Model, v => _profile.Model = v);
    }

    // ---- the kind of connection: a server, or an external command (FR-043)

    public IReadOnlyList<string> KindItems { get; } = [UiText.Get("Conn_KindOpenAi"), UiText.Get("Conn_KindCommand")];

    public IReadOnlyList<string> CommandItems { get; } = ["claude", "codex", "opencode"];

    public IReadOnlyList<string> EffortItems { get; } = [UiText.Get("Conn_EffortDefault"), "low", "medium", "high"];

    public bool IsCommand => _profile.Kind == ConnectionKind.Command;

    public bool IsOpenAi => !IsCommand;

    public int KindIndex
    {
        get => IsCommand ? 1 : 0;
        set => Change(value == 1 ? ConnectionKind.Command : ConnectionKind.OpenAi, _profile.Kind, kind =>
        {
            _profile.Kind = kind;
            if (kind == ConnectionKind.Command)
            {
                // A model name of a server means nothing to a command: start from the usual one.
                _profile.Command ??= CommandKind.Claude;
                _profile.Model = CommandDefaults.InitialModel(_profile.Command.Value);
            }
        });
    }

    public int CommandIndex
    {
        get => (int)(_profile.Command ?? CommandKind.Claude);
        set
        {
            var command = (CommandKind)Math.Clamp(value, 0, 2);
            var before = _profile.Command;
            Change<CommandKind?>(command, before, kind =>
            {
                _profile.Command = kind;
                // The model follows the command while it is still the usual one (or empty).
                if (string.IsNullOrWhiteSpace(_profile.Model) || (before is { } old && _profile.Model == CommandDefaults.InitialModel(old)))
                {
                    _profile.Model = CommandDefaults.InitialModel(kind!.Value);
                }
            });
        }
    }

    public string Executable
    {
        get => _profile.Executable ?? string.Empty;
        set => Change(string.IsNullOrWhiteSpace(value) ? null : value.Trim(), _profile.Executable, v => _profile.Executable = v);
    }

    public int EffortIndex
    {
        get => _profile.Effort switch { "low" => 1, "medium" => 2, "high" => 3, _ => 0 };
        set => Change(value switch { 1 => "low", 2 => "medium", 3 => "high", _ => string.Empty }, _profile.Effort, v => _profile.Effort = v);
    }

    public string ParallelText
    {
        get => _profile.Parallel.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                Change(n, _profile.Parallel, v => _profile.Parallel = v);
            }
        }
    }

    // ---- the limits (FR-057): empty is no limit

    public string MaxPagesText
    {
        get => _profile.MaxPages?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Change<int?>(null, _profile.MaxPages, v => _profile.MaxPages = v);
            }
            else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                Change<int?>(n, _profile.MaxPages, v => _profile.MaxPages = v);
            }
        }
    }

    public string MaxCostText
    {
        get => _profile.MaxCostUsd?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Change<double?>(null, _profile.MaxCostUsd, v => _profile.MaxCostUsd = v);
            }
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                Change<double?>(n, _profile.MaxCostUsd, v => _profile.MaxCostUsd = v);
            }
        }
    }

    public string MaxLinesText
    {
        get => _profile.MaxLinesPerRead.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                Change(n, _profile.MaxLinesPerRead, v => _profile.MaxLinesPerRead = v);
            }
        }
    }

    // ---- the consent to send page images outside the local network (FR-046, FR-052)

    public bool ConsentRequired => ExternalConsent.IsRequired(_profile);

    public bool CanAgree => ConsentRequired && ExternalConsent.StateOf(_profile) != ConsentState.Granted;

    public bool CanWithdraw => ConsentRequired && _profile.ExternalConsent is not null;

    public string ConsentText => ExternalConsent.StateOf(_profile) switch
    {
        ConsentState.NotRequired => UiText.Get("Conn_ConsentNotNeeded"),
        ConsentState.Granted => UiText.Get("Conn_ConsentGranted", _profile.ExternalConsent!.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        ConsentState.Outdated => UiText.Get("Conn_ConsentOutdated"),
        _ => UiText.Get("Conn_ConsentMissing"),
    };

    /// <summary>What the user is asked to agree to.</summary>
    public string ConsentNotice => string.Join(Environment.NewLine, ExternalConsent.Notice(_profile));

    public void Agree()
    {
        _store.Update(_ => ExternalConsent.Grant(_profile, DateTimeOffset.Now));
        Raise(string.Empty);
        _changed();
    }

    public void Withdraw()
    {
        _store.Update(_ => ExternalConsent.Revoke(_profile));
        Raise(string.Empty);
        _changed();
    }

    public string TimeoutText
    {
        get => _profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                Change(seconds, _profile.TimeoutSeconds, v => _profile.TimeoutSeconds = v);
            }
        }
    }

    /// <summary>The name of the environment variable that holds the key. It wins over a stored key.</summary>
    public string ApiKeyEnv
    {
        get => _profile.ApiKeyEnv ?? string.Empty;
        set => Change(string.IsNullOrWhiteSpace(value) ? null : value.Trim(), _profile.ApiKeyEnv, v => _profile.ApiKeyEnv = v);
    }

    public string KeyState => ApiKeyResolver.Describe(_profile);

    /// <summary>Stores a key typed on the screen, encrypted for this Windows user (Decision 002). An empty text removes it.</summary>
    public void SetApiKey(string plain)
    {
        var stored = string.IsNullOrEmpty(plain) ? null : DpapiProtector.Protect(plain);
        Change(stored, _profile.ApiKey, v => _profile.ApiKey = v);
        Raise(nameof(KeyState));
    }

    public override string ToString() => Name;

    private void Change<T>(T value, T current, Action<T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        _store.Update(_ => apply(value));
        Raise(string.Empty);
        _changed();
    }
}

/// <summary>
/// The window for VLM connections: add, edit, delete, choose the one in use, and check it (FR-019 to FR-020).
/// The access key is never shown after it is typed; the screen shows only where it comes from.
/// </summary>
public sealed class ConnectionSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private ConnectionEditorViewModel? _selected;
    private string _connectionResult = string.Empty;
    private string _imageResult = string.Empty;
    private string _problems = string.Empty;
    private bool _busy;

    public ConnectionSettingsViewModel(SettingsStore store)
    {
        _store = store;
        Connections = new ObservableCollection<ConnectionEditorViewModel>(
            store.Current.Vlm.Connections.Select(c => new ConnectionEditorViewModel(c, store, Validate)));
        _selected = Connections.FirstOrDefault(c => IsInUse(c)) ?? Connections.FirstOrDefault();

        AddCommand = new RelayCommand(AddAsync, () => !Busy);
        RemoveCommand = new RelayCommand(RemoveAsync, () => !Busy && Selected is not null);
        UseCommand = new RelayCommand(UseAsync, () => !Busy && Selected is not null);
        CheckConnectionCommand = new RelayCommand(() => CheckAsync(image: false), () => !Busy && Selected is not null);
        CheckImageCommand = new RelayCommand(() => CheckAsync(image: true), () => !Busy && Selected is not null);
        Validate();
    }

    public ObservableCollection<ConnectionEditorViewModel> Connections { get; }

    public ConnectionEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                ConnectionResult = string.Empty;
                ImageResult = string.Empty;
                Raise(nameof(HasSelection));
                RaiseCommands();
            }
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>The name of the connection in use (shown in the title of the list).</summary>
    public string InUse => string.IsNullOrEmpty(_store.Current.Vlm.Selected) ? UiText.Get("Conn_None") : _store.Current.Vlm.Selected;

    public string ConnectionResult
    {
        get => _connectionResult;
        private set => Set(ref _connectionResult, value);
    }

    public string ImageResult
    {
        get => _imageResult;
        private set => Set(ref _imageResult, value);
    }

    /// <summary>Problems in the connections (an empty name, a name used twice, a bad URL ...).</summary>
    public string Problems
    {
        get => _problems;
        private set => Set(ref _problems, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                RaiseCommands();
            }
        }
    }

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand UseCommand { get; }

    public RelayCommand CheckConnectionCommand { get; }

    public RelayCommand CheckImageCommand { get; }

    private bool IsInUse(ConnectionEditorViewModel c) =>
        string.Equals(c.Name, _store.Current.Vlm.Selected, StringComparison.OrdinalIgnoreCase);

    private Task AddAsync()
    {
        var number = Connections.Count + 1;
        var name = UiText.Get("Conn_DefaultName", number);
        while (Connections.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = UiText.Get("Conn_DefaultName", ++number);
        }

        var profile = new ConnectionProfile { Name = name, BaseUrl = "http://localhost:1234/v1", Model = "model-name" };
        _store.Update(s => s.Vlm.Connections.Add(profile));
        var editor = new ConnectionEditorViewModel(profile, _store, Validate);
        Connections.Add(editor);
        Selected = editor;
        Validate();
        return Task.CompletedTask;
    }

    private Task RemoveAsync()
    {
        if (Selected is not { } editor)
        {
            return Task.CompletedTask;
        }

        _store.Update(s =>
        {
            s.Vlm.Connections.Remove(editor.Profile);
            if (string.Equals(s.Vlm.Selected, editor.Name, StringComparison.OrdinalIgnoreCase))
            {
                s.Vlm.Selected = string.Empty;
            }
        });
        Connections.Remove(editor);
        Selected = Connections.FirstOrDefault();
        Raise(nameof(InUse));
        Validate();
        return Task.CompletedTask;
    }

    private Task UseAsync()
    {
        if (Selected is { } editor)
        {
            _store.Update(s => s.Vlm.Selected = editor.Name);
            Raise(nameof(InUse));
        }

        return Task.CompletedTask;
    }

    private async Task CheckAsync(bool image)
    {
        if (Selected is not { } editor)
        {
            return;
        }

        Busy = true;
        using var ticker = new CancellationTokenSource();
        var waiting = ShowWaitingAsync(image, ticker.Token);
        try
        {
            string text;
            if (VlmSetupFactory.ConsentMissing(editor.Profile) is { } missing)
            {
                text = UiText.Get("Conn_Failed") + missing.Message;
            }
            else if (editor.Profile.Kind == ConnectionKind.Command)
            {
                var checker = new CommandConnectionChecker(editor.Profile, new CommandVlmReader(editor.Profile));
                var result = image
                    ? await checker.CheckImageAsync(CancellationToken.None)
                    : await checker.CheckConnectionAsync(CancellationToken.None);
                text = (result.Ok ? UiText.Get("Conn_Ok") : UiText.Get("Conn_Failed")) + result.Message;
            }
            else
            {
                var key = ApiKeyResolver.Resolve(editor.Profile);
                if (key.Error is not null)
                {
                    text = UiText.Get("Conn_Failed") + key.Error;
                }
                else
                {
                    using var client = new VlmClient(editor.Profile, key.Key);
                    var checker = new ConnectionChecker(client, editor.Profile);
                    var result = image
                        ? await checker.CheckImageAsync(CancellationToken.None)
                        : await checker.CheckConnectionAsync(CancellationToken.None);
                    text = (result.Ok ? UiText.Get("Conn_Ok") : UiText.Get("Conn_Failed")) + result.Message;
                }
            }

            await ticker.CancelAsync();
            await waiting;

            // The time of the last run is shown with the result.
            text +=UiText.Get("Conn_LastRun", DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            if (image)
            {
                ImageResult = text;
            }
            else
            {
                ConnectionResult = text;
            }
        }
        finally
        {
            await ticker.CancelAsync();
            Busy = false;
        }
    }

    /// <summary>Shows the seconds waited in the place of the result, because a local model can take a minute or more to answer.</summary>
    private async Task ShowWaitingAsync(bool image, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var text = UiText.Get("Conn_Waiting", (int)(DateTime.UtcNow - started).TotalSeconds);
                if (image)
                {
                    ImageResult = text;
                }
                else
                {
                    ConnectionResult = text;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Validate()
    {
        var problems = _store.Current.Validate().Where(e => e.StartsWith("vlm.connections", StringComparison.Ordinal)).ToList();
        Problems = string.Join(Environment.NewLine, problems);
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { AddCommand, RemoveCommand, UseCommand, CheckConnectionCommand, CheckImageCommand })
        {
            command.RaiseCanExecuteChanged();
        }
    }
}
