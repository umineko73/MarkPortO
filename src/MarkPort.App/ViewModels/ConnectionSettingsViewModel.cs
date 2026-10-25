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

using MarkPort.App.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

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

        var key = ApiKeyResolver.Resolve(editor.Profile);
        Busy = true;
        try
        {
            string text;
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

            // The time of the last run is shown with the result.
            text += UiText.Get("Conn_LastRun", DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
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
            Busy = false;
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
