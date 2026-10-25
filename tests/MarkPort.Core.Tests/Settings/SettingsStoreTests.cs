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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Settings;

public class SettingsStoreTests
{
    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Load_WhenFileIsMissing_ReturnsDefaultsWithoutWarnings()
    {
        using var dir = new TempDir();
        using var store = new SettingsStore(dir.Combine("settings.json"));

        var result = store.Load();

        Assert.Empty(result.Warnings);
        Assert.Empty(result.ValidationErrors);
        Assert.Equal(150, result.Settings.Images.Dpi);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void AllSettingsSurviveSaveAndReload()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using (var store = new SettingsStore(path, NoWait))
        {
            store.Load();
            store.Update(s =>
            {
                s.Input.Paths = [@"D:\in", @"D:\in2\a.docx"];
                s.Input.Recurse = true;
                s.Input.Types = [DocumentKind.Xlsx, DocumentKind.Pptx];
                s.Input.IncludeHidden = true;
                s.Output.Folder = @"D:\出力";
                s.Engine.Kind = EngineKind.LibreOffice;
                s.Engine.LibreOfficePath = @"C:\LO\soffice.exe";
                s.Images.Dpi = 300;
                s.Steps.Step2 = false;
                s.Steps.Step3 = true;
                s.Vlm.Selected = "b";
                s.Vlm.Connections =
                [
                    new ConnectionProfile { Name = "a", BaseUrl = "http://a/v1", Model = "m1", ApiKeyEnv = "KEY_A", TimeoutSeconds = 30 },
                    new ConnectionProfile { Name = "b", BaseUrl = "https://b/v1", Model = "m2", ApiKey = "dpapi:AAAA", TimeoutSeconds = 600 },
                ];
                s.Vlm.Prompts = new PromptSettings { Docx = "D", Xlsx = "X", Pptx = "P" };
            });
            store.Flush();
        }

        using var reloaded = new SettingsStore(path);
        var s = reloaded.Load().Settings;

        Assert.Equal([@"D:\in", @"D:\in2\a.docx"], s.Input.Paths);
        Assert.True(s.Input.Recurse);
        Assert.Equal([DocumentKind.Xlsx, DocumentKind.Pptx], s.Input.Types);
        Assert.True(s.Input.IncludeHidden);
        Assert.Equal(@"D:\出力", s.Output.Folder);
        Assert.Equal(EngineKind.LibreOffice, s.Engine.Kind);
        Assert.Equal(@"C:\LO\soffice.exe", s.Engine.LibreOfficePath);
        Assert.Equal(300, s.Images.Dpi);
        Assert.False(s.Steps.Step2);
        Assert.True(s.Steps.Step3);
        Assert.Equal("b", s.Vlm.Selected);
        Assert.Equal(2, s.Vlm.Connections.Count);
        Assert.Equal("KEY_A", s.Vlm.Connections[0].ApiKeyEnv);
        Assert.Null(s.Vlm.Connections[0].ApiKey);
        Assert.Equal("dpapi:AAAA", s.Vlm.Connections[1].ApiKey);
        Assert.Equal(600, s.Vlm.Connections[1].TimeoutSeconds);
        Assert.Equal("D", s.Vlm.Prompts.Docx);
        Assert.Equal("X", s.Vlm.Prompts.Xlsx);
        Assert.Equal("P", s.Vlm.Prompts.Pptx);
    }

    [Fact]
    public void SavedFile_UsesLowercaseEnumNames_AndKeepsJapaneseReadable()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using var store = new SettingsStore(path, NoWait);
        store.Load();
        store.Update(s =>
        {
            s.Engine.Kind = EngineKind.LibreOffice;
            s.Output.Folder = @"D:\出力";
        });
        store.Flush();

        var text = File.ReadAllText(path);

        Assert.Contains("\"libreoffice\"", text);
        Assert.Contains("D:\\\\出力", text);
        Assert.Contains("\"schemaVersion\": 1", text);
    }

    [Fact]
    public void Update_SavesAfterTheDebounceInterval_AsOneWrite()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using var store = new SettingsStore(path, TimeSpan.FromMilliseconds(150));
        store.Load();

        store.Update(s => s.Images.Dpi = 200);
        store.Update(s => s.Images.Dpi = 250);
        store.Update(s => s.Images.Dpi = 300);

        Assert.False(File.Exists(path)); // not yet: changes are batched
        SpinWait.SpinUntil(() => File.Exists(path), TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(path));
        Assert.Contains("\"dpi\": 300", File.ReadAllText(path));
    }

    [Fact]
    public void Dispose_FlushesPendingChanges()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var store = new SettingsStore(path, TimeSpan.FromMinutes(10));
        store.Load();
        store.Update(s => s.Images.Dpi = 400);

        store.Dispose();

        Assert.Contains("\"dpi\": 400", File.ReadAllText(path));
    }

    [Fact]
    public void Load_WhenFileIsCorrupt_MovesItAsideAndUsesDefaults()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ this is not json");
        using var store = new SettingsStore(path, NoWait);

        var result = store.Load();

        Assert.NotNull(result.CorruptBackupPath);
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.Equal("{ this is not json", File.ReadAllText(result.CorruptBackupPath!));
        Assert.False(File.Exists(path)); // the broken file is not overwritten or kept under the name
        Assert.Single(result.Warnings);
        Assert.Equal(150, result.Settings.Images.Dpi);

        // After the next change, a new file is written; the backup stays.
        store.Update(s => s.Images.Dpi = 200);
        store.Flush();
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(result.CorruptBackupPath));
    }

    [Fact]
    public void Load_WhenSchemaIsNewer_WarnsAndNeverOverwrites()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 99, \"images\": { \"dpi\": 200 } }");
        var before = File.ReadAllText(path);
        using var store = new SettingsStore(path, NoWait);

        var result = store.Load();
        var dpiAsLoaded = result.Settings.Images.Dpi;
        store.Update(s => s.Images.Dpi = 300);
        store.Flush();

        Assert.True(store.IsReadOnly);
        Assert.Single(result.Warnings);
        Assert.Equal(200, dpiAsLoaded);
        Assert.Equal(before, File.ReadAllText(path)); // the newer file is never overwritten
    }

    [Fact]
    public void Load_ReportsRuleViolationsInLoadedValues()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ \"images\": { \"dpi\": 9999 } }");
        using var store = new SettingsStore(path);

        var result = store.Load();

        Assert.Contains(result.ValidationErrors, e => e.Contains("images.dpi"));
    }

    [Fact]
    public void Load_AcceptsHandWrittenPlainApiKey()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """
            { "vlm": { "connections": [ { "name": "ci", "baseUrl": "http://x/v1", "model": "m", "apiKey": "plain-key" } ] } }
            """);
        using var store = new SettingsStore(path);

        var result = store.Load();

        Assert.Equal("plain-key", result.Settings.Vlm.Connections[0].ApiKey);
        Assert.Equal(120, result.Settings.Vlm.Connections[0].TimeoutSeconds);
    }
}
