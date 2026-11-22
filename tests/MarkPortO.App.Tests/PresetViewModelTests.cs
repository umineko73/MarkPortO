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

using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MarkPortO.App.ViewModels;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;

namespace MarkPortO.App.Tests;

/// <summary>
/// FR-151 on the screen: the drop-down list of the presets, "(changed)", and saving, renaming and deleting the presets of the user.
/// The drop-down list is a real WPF ComboBox bound as in MainWindow.xaml: choosing "Custom" once set the choice again and again until the stack overflowed.
/// </summary>
public sealed class PresetViewModelTests
{
    private const int Custom = 0;
    private const int Human = 1;
    private const int Rag = 2;

    [Fact]
    public void ChoosingCustom_AfterAPreset_KeepsTheSettings_AndForgetsOnlyTheName() => Sta.Run(screen =>
    {
        screen.Combo.SelectedIndex = Human;
        screen.Pump();
        Assert.Equal("human", screen.Settings.PresetSelected);
        Assert.True(screen.Settings.Input.Emphasis);
        Assert.False(screen.Settings.Output.StructureNote);

        screen.Combo.SelectedIndex = Custom;
        screen.Pump();

        Assert.Equal(string.Empty, screen.Settings.PresetSelected);
        Assert.Equal(Custom, screen.Combo.SelectedIndex);
        Assert.Equal(Custom, screen.Model.PresetIndex);
        Assert.True(screen.Settings.Input.Emphasis); // the values of the preset stay
        Assert.False(screen.Settings.Output.StructureNote);
        Assert.Equal(string.Empty, screen.Model.PresetStateText);
    });

    [Fact]
    public void ChoosingCustom_WhenNothingIsChosen_ChangesNothing() => Sta.Run(screen =>
    {
        var before = JsonDefaults.Serialize(screen.Settings);
        var changes = 0;
        screen.Model.PropertyChanged += (_, _) => changes++;

        screen.Combo.SelectedIndex = Custom;
        screen.Model.PresetIndex = Custom;
        screen.Pump();

        Assert.Equal(before, JsonDefaults.Serialize(screen.Settings));
        Assert.Equal(0, changes);
    });

    [Fact]
    public void EveryItem_CanBeChosen_InAnyOrder_WithoutChoosingAgainAndAgain() => Sta.Run(screen =>
    {
        screen.Model.SavePreset("Mine");
        screen.Pump();
        var mine = screen.Model.PresetItems.Count - 1;
        var indexChanges = 0;
        screen.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "PresetIndex" or "")
            {
                indexChanges++;
            }
        };

        foreach (var index in new[] { Custom, Human, Custom, Rag, Human, Rag, mine, Custom, mine, Custom, Custom })
        {
            indexChanges = 0;
            screen.Combo.SelectedIndex = index;
            screen.Pump();

            Assert.Equal(index, screen.Combo.SelectedIndex);
            Assert.Equal(index, screen.Model.PresetIndex);
            Assert.Equal(screen.Model.PresetItems[index].Name, screen.Settings.PresetSelected);
            Assert.True(indexChanges <= 3, $"the choice of {index} was shown again {indexChanges} times");
        }
    });

    [Fact]
    public void TheListOfPresets_IsOneList_WhoseItemsFollowThePresetsOfTheUser() => Sta.Run(screen =>
    {
        var list = screen.Model.PresetItems;
        Assert.Same(list, screen.Combo.ItemsSource);
        Assert.Equal(["", "human", "rag"], list.Select(i => i.Name));
        var resets = 0;
        list.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        screen.Combo.SelectedIndex = Rag;
        screen.Pump();
        screen.Model.SavePreset("Mine");
        screen.Pump();
        Assert.Equal(["", "human", "rag", "Mine"], list.Select(i => i.Name));
        var mine = list[3];

        screen.Model.SavePreset("Mine"); // replaced with the same name: the same item
        screen.Model.RenamePreset("Mine", "Ours");
        screen.Pump();
        Assert.Equal(["", "human", "rag", "Ours"], list.Select(i => i.Name));
        Assert.Same(mine, list[3]); // renamed in place
        Assert.Equal("Ours", list[3].Display);

        screen.Model.DeletePreset("Ours");
        screen.Pump();
        Assert.Equal(["", "human", "rag"], list.Select(i => i.Name));
        Assert.Same(list, screen.Model.PresetItems);
        Assert.Same(list, screen.Combo.ItemsSource);
        Assert.Equal(0, resets);
    });

    [Fact]
    public void DeletingAPresetBeforeTheChosenOne_KeepsTheChosenOne() => Sta.Run(screen =>
    {
        screen.Model.DpiText = "200";
        screen.Model.SavePreset("A");
        screen.Model.DpiText = "250";
        screen.Model.SavePreset("B");
        screen.Pump();
        Assert.Equal(4, screen.Combo.SelectedIndex);

        screen.Model.DeletePreset("A");
        screen.Pump();

        Assert.Equal("B", screen.Settings.PresetSelected);
        Assert.Equal(3, screen.Combo.SelectedIndex);
        Assert.Equal("B", ((MainViewModel.PresetItem)screen.Combo.SelectedItem).Display);
        Assert.Equal(250, screen.Settings.Images.Dpi);
    });

    [Fact]
    public void AChangeAfterThePreset_ShowsChanged_AndChoosingItAgainPutsItsValuesBack() => Sta.Run(screen =>
    {
        screen.Combo.SelectedIndex = Rag;
        screen.Pump();
        Assert.Equal(string.Empty, screen.Model.PresetStateText);

        screen.Model.Emphasis = true;
        screen.Pump();
        Assert.Equal("Preset_Modified", screen.Model.PresetStateText); // the key: the test has no resources of the screen
        Assert.Equal(Rag, screen.Combo.SelectedIndex); // the name stays, with "(changed)"
        Assert.Equal("rag (modified)", ConversionPreset.RecordText(screen.Settings));

        screen.Model.PresetIndex = Rag; // the same item again, as a click on it in the list
        screen.Pump();
        Assert.False(screen.Settings.Input.Emphasis);
        Assert.Equal(string.Empty, screen.Model.PresetStateText);

        screen.Model.StructureNote = false;
        Assert.Equal("Preset_Modified", screen.Model.PresetStateText);
        screen.Model.StructureNote = true;
        Assert.Equal(string.Empty, screen.Model.PresetStateText); // back to the values of the preset
    });

    [Fact]
    public void ThePresetsValues_AreShownByEveryItemOfTheScreen() => Sta.Run(screen =>
    {
        screen.Model.DpiText = "300";
        screen.Model.Emphasis = false;
        screen.Model.StructureNote = true;

        screen.Combo.SelectedIndex = Human;
        screen.Pump();

        Assert.Equal("150", screen.Model.DpiText);
        Assert.Equal(150, screen.Settings.Images.Dpi);
        Assert.True(screen.Model.Emphasis);
        Assert.False(screen.Model.StructureNote);
        Assert.False(screen.Model.Step2);
        Assert.True(screen.Model.DropRepeatedTables);
        Assert.Equal(string.Empty, screen.Model.PresetStateText);
    });

    [Fact]
    public void WithoutAConnection_APresetLeavesStep3Off_AndIsNotChanged() => Sta.Run(screen =>
    {
        screen.Combo.SelectedIndex = Human;
        screen.Pump();

        Assert.False(screen.Settings.Steps.Step3);
        Assert.False(screen.Model.Step3);
        Assert.Equal(string.Empty, screen.Model.PresetStateText);
    });

    [Fact]
    public void WithAConnection_APresetTurnsStep3On_AndTheScreenShowsIt() => Sta.Run(
        screen =>
        {
            screen.Combo.SelectedIndex = Rag;
            screen.Pump();

            Assert.True(screen.Model.Step3);
            Assert.Equal(0, screen.Model.Step3ModeIndex); // supplement
            Assert.True(screen.Model.Step2);
            Assert.Equal(string.Empty, screen.Model.PresetStateText);
        },
        settings =>
        {
            settings.Vlm.Connections = [new ConnectionProfile { Name = "local", BaseUrl = "http://localhost:1234/v1", Model = "m" }];
            settings.Vlm.Selected = "local";
        });

    [Fact]
    public void SavingAPreset_ChoosesIt_AndAPresetOfTheSameNameIsReplaced() => Sta.Run(screen =>
    {
        screen.Model.DpiText = "200";
        screen.Model.SavePreset("  Scans  ");
        screen.Pump();
        Assert.Equal("Scans", screen.Settings.PresetSelected);
        Assert.Equal(screen.Model.PresetItems.Count - 1, screen.Combo.SelectedIndex);
        Assert.Equal("Scans", ((MainViewModel.PresetItem)screen.Combo.SelectedItem).Display);

        screen.Model.DpiText = "300";
        screen.Pump();
        Assert.Equal("Preset_Modified", screen.Model.PresetStateText);
        screen.Model.SavePreset("scans");
        screen.Pump();

        var preset = Assert.Single(screen.Settings.Presets);
        Assert.Equal("scans", preset.Name);
        Assert.Equal(300, preset.Dpi);
        Assert.Equal(string.Empty, screen.Model.PresetStateText);
    });

    [Fact]
    public void RenamingTheChosenPreset_KeepsItChosen_AndDeletingItLeavesTheSettings() => Sta.Run(screen =>
    {
        screen.Model.DpiText = "200";
        screen.Model.SavePreset("A");
        screen.Model.DpiText = "250";
        screen.Model.SavePreset("B");
        screen.Combo.SelectedIndex = 3; // A
        screen.Pump();
        Assert.Equal("A", screen.Settings.PresetSelected);
        Assert.Equal(200, screen.Settings.Images.Dpi);

        screen.Model.RenamePreset("A", "First");
        screen.Pump();
        Assert.Equal("First", screen.Settings.PresetSelected);
        Assert.Equal(3, screen.Combo.SelectedIndex);
        Assert.Equal("First", ((MainViewModel.PresetItem)screen.Combo.SelectedItem).Display);

        screen.Model.RenamePreset("B", "Second"); // not the chosen one: the choice stays
        screen.Pump();
        Assert.Equal("First", screen.Settings.PresetSelected);

        screen.Model.DeletePreset("first");
        screen.Pump();
        Assert.Equal(string.Empty, screen.Settings.PresetSelected);
        Assert.Equal(Custom, screen.Combo.SelectedIndex);
        Assert.Equal(200, screen.Settings.Images.Dpi);
        Assert.Equal(["Second"], screen.Model.UserPresetNames);
        Assert.True(screen.Model.PresetExists("second"));
        Assert.True(screen.Model.PresetExists("RAG"));
        Assert.False(screen.Model.PresetExists("first"));
    });

    [Fact]
    public void APresetThatIsNotThereAnyMore_IsShownAsCustom() => Sta.Run(
        screen =>
        {
            Assert.Equal(Custom, screen.Model.PresetIndex);
            Assert.Equal(Custom, screen.Combo.SelectedIndex);
            Assert.Equal(string.Empty, screen.Model.PresetStateText);
            Assert.Null(ConversionPreset.RecordText(screen.Settings));
        },
        settings => settings.PresetSelected = "deleted in the file");

    [Fact]
    public void ThePresetsAreSavedInTheSettingsFile() => Sta.Run(screen =>
    {
        screen.Combo.SelectedIndex = Human;
        screen.Pump();
        screen.Model.SavePreset("Mine");
        screen.Store.Flush();

        var read = new SettingsStore(screen.Store.FilePath);
        read.Load();
        Assert.Equal("Mine", read.Current.PresetSelected);
        Assert.Equal("Mine", Assert.Single(read.Current.Presets).Name);
        Assert.True(read.Current.Input.Emphasis);
        read.Dispose();
    });

    /// <summary>The main view model, a ComboBox bound to it as in the main window, and their settings in a file of their own.</summary>
    private sealed class Screen(SettingsStore store, MainViewModel model, ComboBox combo)
    {
        public SettingsStore Store { get; } = store;

        public MainViewModel Model { get; } = model;

        public ComboBox Combo { get; } = combo;

        public AppSettings Settings => Store.Current;

        /// <summary>Lets the bindings do what they put off.</summary>
        public void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
    }

    /// <summary>Runs a test on a thread of its own with a dispatcher, as WPF needs.</summary>
    private static class Sta
    {
        public static void Run(Action<Screen> test, Action<AppSettings>? prepare = null)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "MarkPortO.App.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var settings = AppSettings.CreateDefault();
                    settings.Output.Folder = Path.Combine(folder, "out");
                    settings.Steps.Step3 = false;
                    prepare?.Invoke(settings);
                    var path = Path.Combine(folder, "settings.json");
                    File.WriteAllText(path, JsonDefaults.Serialize(settings));

                    var store = new SettingsStore(path, TimeSpan.FromMilliseconds(1));
                    var model = new MainViewModel(store, Dispatcher.CurrentDispatcher);
                    // As in MainWindow.xaml.
                    var combo = new ComboBox { DataContext = model, DisplayMemberPath = "Display" };
                    combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.PresetItems)));
                    combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedPreset)));
                    var screen = new Screen(store, model, combo);
                    screen.Pump();
                    try
                    {
                        test(screen);
                    }
                    finally
                    {
                        model.Dispose();
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (IOException)
                    {
                        // a file still open is left in the temporary folder
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
