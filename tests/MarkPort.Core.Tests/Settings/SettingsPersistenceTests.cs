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

using System.Collections;
using System.Reflection;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Settings;

/// <summary>FR-018, SC-015: every value that can be set on the screen is written to settings.json and comes back.</summary>
public class SettingsPersistenceTests
{
    /// <summary>Gives every setting a value that is not its default.</summary>
    private static void FillWithNonDefaults(object target, ref int counter)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var type = property.PropertyType;
            counter++;
            if (type == typeof(string))
            {
                property.SetValue(target, property.Name switch
                {
                    "Language" => "ja",
                    "Theme" => "dark",
                    _ => $"value-{property.Name}-{counter}",
                });
            }
            else if (type == typeof(bool))
            {
                property.SetValue(target, !(bool)property.GetValue(target)!);
            }
            else if (type == typeof(int))
            {
                property.SetValue(target, property.Name switch
                {
                    "Dpi" => 333,
                    "TimeoutSeconds" => 77,
                    "SchemaVersion" => AppSettings.CurrentSchemaVersion,
                    _ => counter,
                });
            }
            else if (type.IsEnum)
            {
                var values = Enum.GetValues(type).Cast<object>().ToList();
                var current = property.GetValue(target)!;
                property.SetValue(target, values.First(v => !v.Equals(current)));
            }
            else if (type == typeof(List<string>))
            {
                property.SetValue(target, new List<string> { $"a-{counter}", $"b-{counter}" });
            }
            else if (type == typeof(List<DocumentKind>))
            {
                property.SetValue(target, new List<DocumentKind> { DocumentKind.Pptx });
            }
            else if (type == typeof(List<ConnectionProfile>))
            {
                var connection = new ConnectionProfile();
                FillWithNonDefaults(connection, ref counter);
                connection.BaseUrl = "https://example.test/v1"; // must stay a valid URL
                property.SetValue(target, new List<ConnectionProfile> { connection });
            }
            else if (property.PropertyType.IsClass && property.GetValue(target) is { } child)
            {
                FillWithNonDefaults(child, ref counter);
            }
        }
    }

    [Fact]
    public void EveryPropertyOfTheSettings_SurvivesSaveAndReload()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var changed = AppSettings.CreateDefault();
        var counter = 0;
        FillWithNonDefaults(changed, ref counter);
        changed.Vlm.Selected = changed.Vlm.Connections[0].Name; // the selection names an existing connection

        using (var store = new SettingsStore(path, TimeSpan.FromMilliseconds(1)))
        {
            store.Load();
            store.Update(s =>
            {
                s.Input = changed.Input;
                s.Output = changed.Output;
                s.Engine = changed.Engine;
                s.Images = changed.Images;
                s.Steps = changed.Steps;
                s.Ui = changed.Ui;
                s.Vlm = changed.Vlm;
            });
        } // disposing saves

        using var reloaded = new SettingsStore(path);
        var loaded = reloaded.Load().Settings;

        Assert.True(counter > 20, "the test reached the settings it should");
        Assert.Equal(JsonDefaults.Serialize(changed), JsonDefaults.Serialize(loaded));
    }

    [Fact]
    public void NoPropertyOfTheSettings_IsExcludedFromTheFile()
    {
        // A property that is never written would be lost at the next start (FR-018): none has [JsonIgnore].
        var ignored = new List<string>();
        void Walk(Type type)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttributes().Any(a => a.GetType().Name == "JsonIgnoreAttribute"))
                {
                    ignored.Add($"{type.Name}.{property.Name}");
                }

                var element = property.PropertyType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(property.PropertyType)
                    ? property.PropertyType.GetGenericArguments()[0]
                    : property.PropertyType;
                if (element.Namespace == typeof(AppSettings).Namespace && element.IsClass)
                {
                    Walk(element);
                }
            }
        }

        Walk(typeof(AppSettings));

        Assert.Empty(ignored);
    }

    [Fact]
    public void TheSettingsTheScreenOffers_AreAllInTheFile()
    {
        // The values of the main window and the connection window, by their place in settings.json.
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using (var store = new SettingsStore(path, TimeSpan.FromMilliseconds(1)))
        {
            store.Load();
            store.Update(s => s.Vlm.Connections.Add(new ConnectionProfile { Name = "c", BaseUrl = "http://x/v1", Model = "m", ApiKeyEnv = "K", ApiKey = "dpapi:AA", TimeoutSeconds = 9 }));
        }

        var json = File.ReadAllText(path);

        foreach (var key in new[]
        {
            "\"paths\"", "\"recurse\"", "\"types\"", "\"includeHidden\"", "\"folder\"", "\"kind\"", "\"libreOfficePath\"", "\"dpi\"",
            "\"step2\"", "\"step3\"", "\"selected\"", "\"connections\"", "\"name\"", "\"baseUrl\"", "\"model\"", "\"apiKeyEnv\"",
            "\"apiKey\"", "\"timeoutSeconds\"", "\"prompts\"", "\"docx\"", "\"xlsx\"", "\"pptx\"",
        })
        {
            Assert.Contains(key, json);
        }
    }
}
