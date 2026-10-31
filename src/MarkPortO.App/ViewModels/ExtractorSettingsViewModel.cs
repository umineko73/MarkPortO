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

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using MarkPortO.App.Services;
using MarkPortO.Core.Extraction;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.App.ViewModels;

/// <summary>One extractor as it is edited. Every change is written to settings.json at once (FR-110; experimental).</summary>
public sealed class ExtractorEditorViewModel : ObservableObject
{
    /// <summary>What is shown in place of the value of an environment variable; the value itself is never shown.</summary>
    public const string Mask = "********";

    private readonly SettingsStore _store;
    private readonly Action _changed;
    private string _tryResult = string.Empty;
    private bool _busy;

    public ExtractorEditorViewModel(ExtractorProfile profile, SettingsStore store, Action changed)
    {
        Profile = profile;
        _store = store;
        _changed = changed;
    }

    public ExtractorProfile Profile { get; }

    public string Name
    {
        get => Profile.Name;
        set
        {
            var old = Profile.Name;
            value = value.Trim();
            if (value == old || value.Length == 0 || _store.Current.Extractors.Any(e => !ReferenceEquals(e, Profile) && string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase)))
            {
                Raise(); // an empty or a used name is not taken
                return;
            }

            _store.Update(s =>
            {
                Profile.Name = value;
                foreach (var kind in new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf })
                {
                    if (string.Equals(s.Input.Extractors.For(kind), old, StringComparison.OrdinalIgnoreCase))
                    {
                        s.Input.Extractors.Set(kind, value); // the choice follows a rename
                    }
                }
            });
            Raise();
            _changed();
        }
    }

    public string Command
    {
        get => Profile.Command;
        set => Change(value.Trim(), Profile.Command, v => Profile.Command = v);
    }

    /// <summary>One argument for each line.</summary>
    public string ArgsText
    {
        get => string.Join(Environment.NewLine, Profile.Args);
        set => Change(value, ArgsText, v => Profile.Args = [.. v.Split(["\r\n", "\n"], StringSplitOptions.None).Where(a => a.Length > 0)]);
    }

    public IReadOnlyList<string> OutputItems { get; } = [UiText.Get("Ext_OutputFile"), UiText.Get("Ext_OutputStdout")];

    public int OutputIndex
    {
        get => Profile.Output == ExtractorOutput.Stdout ? 1 : 0;
        set => Change(value == 1 ? ExtractorOutput.Stdout : ExtractorOutput.File, Profile.Output, v => Profile.Output = v);
    }

    public string TimeoutText
    {
        get => Profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 1)
            {
                Change(seconds, Profile.TimeoutSeconds, v => Profile.TimeoutSeconds = v);
            }

            Raise();
        }
    }

    public string WorkingDirectory
    {
        get => Profile.WorkingDirectory ?? string.Empty;
        set => Change(string.IsNullOrWhiteSpace(value) ? null : value.Trim(), Profile.WorkingDirectory, v => Profile.WorkingDirectory = v);
    }

    /// <summary>One <c>NAME=value</c> for each line. The values are shown masked, and are kept encrypted (FR-023).</summary>
    public string EnvText
    {
        get => string.Join(Environment.NewLine, Profile.Env.Select(p => p.Key + "=" + Mask));
        set
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                var at = line.IndexOf('=', StringComparison.Ordinal);
                if (at < 1)
                {
                    continue;
                }

                var key = line[..at].Trim();
                var text = line[(at + 1)..];
                if (text == Mask && Profile.Env.TryGetValue(key, out var kept))
                {
                    env[key] = kept; // not changed
                }
                else if (text.Length > 0)
                {
                    env[key] = DpapiProtector.IsProtected(text) ? text : DpapiProtector.Protect(text);
                }
            }

            if (!env.OrderBy(p => p.Key).SequenceEqual(Profile.Env.OrderBy(p => p.Key)))
            {
                _store.Update(_ => Profile.Env = env);
            }

            Raise();
        }
    }

    public bool SendsExternally
    {
        get => Profile.SendsExternally;
        set
        {
            if (value == Profile.SendsExternally)
            {
                return;
            }

            _store.Update(_ =>
            {
                Profile.SendsExternally = value;
                if (!value)
                {
                    Profile.ExternalConsent = null;
                }
            });
            RaiseConsent();
        }
    }

    public bool ConsentRequired => Profile.SendsExternally;

    public bool CanAgree => ConsentRequired && !IsGranted;

    public bool CanWithdraw => ConsentRequired && Profile.ExternalConsent is not null;

    private bool IsGranted => Profile.ExternalConsent is { NoticeVersion: var v } && v == ExternalConsent.CurrentNoticeVersion;

    public string ConsentText =>
        !ConsentRequired ? UiText.Get("Ext_ConsentNotNeeded")
        : IsGranted ? UiText.Get("Conn_ConsentGranted", Profile.ExternalConsent!.At.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
        : Profile.ExternalConsent is null ? UiText.Get("Ext_ConsentMissing")
        : UiText.Get("Conn_ConsentOutdated");

    public string ConsentNotice => UiText.Get("Ext_ConsentNotice", Profile.Name);

    public void Agree()
    {
        _store.Update(_ => Profile.ExternalConsent = new ConsentRecord { At = DateTimeOffset.Now, NoticeVersion = ExternalConsent.CurrentNoticeVersion });
        RaiseConsent();
    }

    public void Withdraw()
    {
        _store.Update(_ => Profile.ExternalConsent = null);
        RaiseConsent();
    }

    /// <summary>What is wrong with the settings of this extractor, in words for the user; empty when nothing.</summary>
    public string Problem
    {
        get
        {
            if (ExtractorRunner.Validate(Profile) is { } problem)
            {
                return problem;
            }

            return !string.IsNullOrWhiteSpace(Profile.Command) && ExtractorRunner.FindProgram(Profile.Command) is null
                ? UiText.Get("Ext_NotFound", Profile.Command)
                : string.Empty;
        }
    }

    public string TryResult
    {
        get => _tryResult;
        private set => Set(ref _tryResult, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(NotBusy));
            }
        }
    }

    public bool NotBusy => !_busy;

    /// <summary>Runs the extractor on one file in a temporary folder and shows the head of the Markdown (FR-113).</summary>
    public async Task TryAsync(string file)
    {
        if (Problem.Length > 0)
        {
            TryResult = Problem;
            return;
        }

        if (Profile.SendsExternally && !IsGranted)
        {
            TryResult = UiText.Get("Ext_ConsentMissing");
            return;
        }

        Busy = true;
        TryResult = UiText.Get("Ext_Trying");
        var folder = Path.Combine(Path.GetTempPath(), "MarkPortO-try-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Task.Run(() => ExtractorRunner.RunAsync(Profile, file, folder, CancellationToken.None));
            var lines = result.Markdown.Split('\n');
            var head = string.Join(Environment.NewLine, lines.Take(30).Select(l => l.TrimEnd('\r')));
            TryResult = UiText.Get("Ext_TryDone", result.Seconds.ToString("0.0", CultureInfo.InvariantCulture), result.Markdown.Length, lines.Length)
                + Environment.NewLine + head + (lines.Length > 30 ? Environment.NewLine + "…" : string.Empty);
        }
        catch (ExtractorException ex)
        {
            TryResult = UiText.Get("Ext_TryFailed") + Environment.NewLine + ex.Message;
        }
        finally
        {
            Busy = false;
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // a temporary folder that cannot be removed now is left for the system
            }
        }
    }

    private void RaiseConsent()
    {
        Raise(nameof(SendsExternally));
        Raise(nameof(ConsentRequired));
        Raise(nameof(CanAgree));
        Raise(nameof(CanWithdraw));
        Raise(nameof(ConsentText));
    }

    private void Change<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(value, current))
        {
            _store.Update(_ => apply(value));
            _changed();
        }

        Raise(name);
        Raise(nameof(Problem));
    }
}

/// <summary>The window that lists the extractors (external commands that write the Markdown) and edits them.</summary>
public sealed class ExtractorSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private ExtractorEditorViewModel? _selected;

    public ExtractorSettingsViewModel(SettingsStore store)
    {
        _store = store;
        Extractors = [.. store.Current.Extractors.Select(Wrap)];
        _selected = Extractors.FirstOrDefault();
        AddCommand = new RelayCommand(() => AddAsync(null));
        AddMarkItDownCommand = new RelayCommand(() => AddAsync(MarkItDown));
        AddDoclingCommand = new RelayCommand(() => AddAsync(Docling));
        RemoveCommand = new RelayCommand(RemoveAsync, () => Selected is not null);
    }

    public ObservableCollection<ExtractorEditorViewModel> Extractors { get; }

    public ExtractorEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Raise(nameof(HasSelection));
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand AddMarkItDownCommand { get; }

    public RelayCommand AddDoclingCommand { get; }

    // The usual way to call two tools that are known (they are not bundled: the user installs them). The arguments differ between versions; "Try" shows it.
    private static ExtractorProfile MarkItDown() => new() { Name = "markitdown", Command = "markitdown", Args = ["{input}", "-o", "{output}"] };

    private static ExtractorProfile Docling() => new()
    {
        Name = "docling",
        Command = "docling",
        Args = ["{input}", "--to", "md", "--image-export-mode", "placeholder", "--output", "{outdir}"],
        TimeoutSeconds = 600,
    };

    private ExtractorEditorViewModel Wrap(ExtractorProfile profile) => new(profile, _store, () => { });

    private Task AddAsync(Func<ExtractorProfile>? template)
    {
        var names = _store.Current.Extractors.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The usual shape: the file in, the file out. The command is typed by the user. A template has its own name; a number is added when it is used.
        var profile = template?.Invoke() ?? new ExtractorProfile { Name = "extractor", Args = ["{input}", "-o", "{output}"] };
        var baseName = profile.Name;
        var number = 1;
        while (names.Contains(profile.Name) || (template is null && number == 1))
        {
            profile.Name = baseName + "-" + number++;
        }

        _store.Update(s => s.Extractors.Add(profile));
        var editor = Wrap(profile);
        Extractors.Add(editor);
        Selected = editor;
        return Task.CompletedTask;
    }

    private Task RemoveAsync()
    {
        if (Selected is not { } editor)
        {
            return Task.CompletedTask;
        }

        var index = Extractors.IndexOf(editor);
        _store.Update(s =>
        {
            s.Extractors.Remove(editor.Profile);
            foreach (var kind in new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf })
            {
                if (string.Equals(s.Input.Extractors.For(kind), editor.Profile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    s.Input.Extractors.Set(kind, string.Empty); // back to the built-in one
                }
            }
        });
        Extractors.Remove(editor);
        Selected = Extractors.Count == 0 ? null : Extractors[Math.Min(index, Extractors.Count - 1)];
        return Task.CompletedTask;
    }
}
