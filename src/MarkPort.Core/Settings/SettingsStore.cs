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

using System.Text.Json;
using MarkPort.Core.IO;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Settings;

/// <summary>The outcome of reading <c>settings.json</c>.</summary>
/// <param name="Settings">The settings to use (defaults when the file is missing or unusable).</param>
/// <param name="Warnings">Problems to show to the user.</param>
/// <param name="ValidationErrors">Rule violations in the loaded values (see <see cref="AppSettings.Validate"/>).</param>
/// <param name="CorruptBackupPath">Where an unreadable file was moved to, if it was.</param>
public sealed record SettingsLoadResult(
    AppSettings Settings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ValidationErrors,
    string? CorruptBackupPath);

/// <summary>
/// Reads and writes <c>settings.json</c>. Changes are saved a short time after the last change
/// (so a burst of edits is one write) and always atomically (FR-018).
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _dirty;
    private bool _disposed;

    public SettingsStore(string path, TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path);
        _debounce = debounce ?? DefaultDebounce;
        Current = AppSettings.CreateDefault();
    }

    /// <summary>The name of the environment variable that names another settings file (for a test run or a second copy of the settings).</summary>
    public const string PathVariable = "MARKPORT_SETTINGS";

    /// <summary>
    /// The default location: <c>%APPDATA%\MarkPort\settings.json</c>, or the file named by the environment variable
    /// <see cref="PathVariable"/> when it is set.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkPort", "settings.json");

    public string FilePath => _path;

    public AppSettings Current { get; private set; }

    /// <summary>True when the file was written by a newer version. Saving is then suppressed so it is never overwritten.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Raised when a background save fails (for example the disk is full).</summary>
    public event EventHandler<Exception>? SaveFailed;

    public SettingsLoadResult Load()
    {
        var warnings = new List<string>();
        string? corruptBackup = null;

        if (!File.Exists(_path))
        {
            Current = AppSettings.CreateDefault();
            return new SettingsLoadResult(Current, warnings, Current.Validate(), null);
        }

        AppSettings? loaded = null;
        string? failure = null;
        try
        {
            var json = File.ReadAllText(_path, AtomicFile.Utf8NoBom);
            loaded = JsonDefaults.Deserialize<AppSettings>(json);
            if (loaded is null)
            {
                failure = "the file is empty or null";
            }
        }
        catch (JsonException ex)
        {
            failure = ex.Message;
        }
        catch (IOException ex)
        {
            // The file exists but cannot be read now: do not move it, just report and use defaults.
            Current = AppSettings.CreateDefault();
            IsReadOnly = true;
            warnings.Add($"settings.json could not be read ({ex.Message}). Defaults are used and the file is left unchanged.");
            return new SettingsLoadResult(Current, warnings, Current.Validate(), null);
        }

        if (loaded is null)
        {
            corruptBackup = MoveAside(_path);
            Current = AppSettings.CreateDefault();
            warnings.Add(
                $"settings.json is not valid ({failure}). It was moved to '{corruptBackup}' and defaults are used.");
            return new SettingsLoadResult(Current, warnings, Current.Validate(), corruptBackup);
        }

        if (loaded.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            IsReadOnly = true;
            warnings.Add(
                $"settings.json has schemaVersion {loaded.SchemaVersion}, which is newer than this version understands ({AppSettings.CurrentSchemaVersion}). It is read but never overwritten.");
        }

        Current = loaded;
        return new SettingsLoadResult(Current, warnings, Current.Validate(), null);
    }

    /// <summary>Changes the settings and schedules a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            change(Current);
            _dirty = true;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Saves now if there are unsaved changes.</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            if (IsReadOnly)
            {
                return;
            }

            json = JsonDefaults.Serialize(Current);
        }

        try
        {
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }

            SaveFailed?.Invoke(this, ex);
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        Flush();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private static string MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = $"{path}.corrupt-{stamp}";
        var counter = 1;
        while (File.Exists(target))
        {
            target = $"{path}.corrupt-{stamp}-{counter++}";
        }

        File.Move(path, target);
        return target;
    }
}
