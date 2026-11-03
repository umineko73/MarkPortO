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

using System.Windows;
using System.Windows.Controls;
using MarkPortO.App.Services;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

/// <summary>
/// Edits the prompts sent to the VLM. For each kind of file there is the standard prompt and prompts with a name (FR-115); the one that is
/// chosen in the list is the one that is used. Each box saves when it loses focus.
/// </summary>
public partial class PromptsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly string _language;
    private readonly List<KindEditor> _editors = [];

    public PromptsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;
        // A prompt that was never edited is shown in the display language.
        _language = LanguageResolver.Resolve(store.Current.Ui.Language);
        foreach (var (kind, header) in new[]
        {
            (DocumentKind.Docx, "Word (docx)"),
            (DocumentKind.Xlsx, "Excel (xlsx)"),
            (DocumentKind.Pptx, "PowerPoint (pptx)"),
            (DocumentKind.Pdf, "PDF (pdf)"),
        })
        {
            var editor = new KindEditor(this, kind);
            _editors.Add(editor);
            Tabs.Items.Add(new TabItem { Header = header, Content = editor.Root });
        }

        Closing += (_, _) =>
        {
            foreach (var editor in _editors)
            {
                editor.Commit();
            }
        };
    }

    private KindEditor Current => _editors[Math.Max(0, Tabs.SelectedIndex)];

    // Puts {pagetext} at the caret of the box that is shown (in place of the selected text), and keeps the focus in the box.
    private void OnInsertPageText(object sender, RoutedEventArgs e)
    {
        var box = Current.Box;
        var at = box.SelectionStart;
        box.SelectedText = MarkPortO.Core.Vlm.PromptSupplement.PageTextPlaceholder;
        box.CaretIndex = at + MarkPortO.Core.Vlm.PromptSupplement.PageTextPlaceholder.Length;
        box.Focus();
        Current.Commit();
    }

    private void OnResetCurrent(object sender, RoutedEventArgs e) => Current.Reset();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>The name of a prompt asked in a small window; <c>null</c> when it is cancelled.</summary>
    private string? AskName(DocumentKind kind, string title, string initial, string? current)
    {
        var box = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 0, 0, 6) };
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 6) };
        var ok = new Button { Content = UiText.Get("Prompts_Ok"), IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = UiText.Get("Prompts_Cancel"), IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(12), Children = { new TextBlock { Text = UiText.Get("Prompts_NameLabel"), Margin = new Thickness(0, 0, 0, 4) }, box, error, buttons } };
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            var others = _store.Current.Vlm.Prompts.VariantsOf(kind).Items.Where(p => !string.Equals(p.Name, current, StringComparison.OrdinalIgnoreCase));
            if (name.Length == 0 || PromptPresets.IsStandardName(name) || name.Contains('\n', StringComparison.Ordinal) || name.Contains('\r', StringComparison.Ordinal) || name.Contains("--", StringComparison.Ordinal))
            {
                error.Text = UiText.Get("Prompts_NameInvalid");
            }
            else if (others.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error.Text = UiText.Get("Prompts_NameDuplicate");
            }
            else
            {
                result = name;
                dialog.DialogResult = true;
            }
        };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? result : null;
    }

    /// <summary>The editor of one kind of file: the list of its prompts, the buttons, and the box of the text.</summary>
    private sealed class KindEditor
    {
        private readonly PromptsWindow _window;
        private readonly DocumentKind _kind;
        private readonly ComboBox _list = new() { MinWidth = 240, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _add = new() { Content = UiText.Get("Prompts_Add"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _rename = new() { Content = UiText.Get("Prompts_Rename"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
        private readonly Button _delete = new() { Content = UiText.Get("Prompts_Delete"), Padding = new Thickness(8, 2, 8, 2) };
        private bool _loading;
        private int _shown = -1; // the index in the list of the prompt that the box shows; 0 is the standard prompt

        public KindEditor(PromptsWindow window, DocumentKind kind)
        {
            _window = window;
            _kind = kind;
            Box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 0) };
            bar.Children.Add(new TextBlock { Text = UiText.Get("Prompts_UsedLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            bar.Children.Add(_list);
            bar.Children.Add(_add);
            bar.Children.Add(_rename);
            bar.Children.Add(_delete);
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bar, 0);
            Grid.SetRow(Box, 1);
            grid.Children.Add(bar);
            grid.Children.Add(Box);
            Root = grid;

            Box.LostFocus += (_, _) => Commit();
            _list.SelectionChanged += (_, _) =>
            {
                if (_loading || _list.SelectedIndex < 0)
                {
                    return;
                }

                Commit();
                var names = Variants.Items.Select(p => p.Name).ToList();
                var index = _list.SelectedIndex;
                _window._store.Update(s => s.Vlm.Prompts.VariantsOf(_kind).Selected = index == 0 ? string.Empty : names[index - 1]);
                Show(index);
            };
            _add.Click += (_, _) => OnAdd();
            _rename.Click += (_, _) => OnRename();
            _delete.Click += (_, _) => OnDelete();
            Reload();
        }

        public UIElement Root { get; }

        public TextBox Box { get; }

        private PromptVariants Variants => _window._store.Current.Vlm.Prompts.VariantsOf(_kind);

        private string Language => _window._language;

        private string Standard => PromptSettings.DefaultFor(_kind, Language);

        private int SelectedIndexInSettings
        {
            get
            {
                var index = Variants.Items.FindIndex(p => string.Equals(p.Name, Variants.Selected, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? 0 : index + 1;
            }
        }

        private void Reload()
        {
            _loading = true;
            _list.Items.Clear();
            _list.Items.Add(UiText.Get("Prompt_Standard"));
            foreach (var item in Variants.Items)
            {
                _list.Items.Add(item.Name);
            }

            _list.SelectedIndex = SelectedIndexInSettings;
            _loading = false;
            Show(SelectedIndexInSettings);
        }

        private void Show(int index)
        {
            _shown = index;
            Box.Text = index == 0
                ? _window._store.Current.Vlm.Prompts.For(_kind, Language)
                : Variants.Items[index - 1].TextIn(_kind, Language);
            _rename.IsEnabled = _delete.IsEnabled = index > 0;
        }

        /// <summary>Saves the text of the box into the prompt that it shows.</summary>
        public void Commit()
        {
            if (_shown < 0)
            {
                return;
            }

            if (_shown == 0)
            {
                // A prompt must not be empty (settings validation): an empty box means "use the default".
                var text = string.IsNullOrWhiteSpace(Box.Text) ? Standard : Box.Text;
                if (text != Box.Text)
                {
                    Box.Text = text;
                }

                _window._store.Update(s =>
                {
                    switch (_kind)
                    {
                        case DocumentKind.Docx: s.Vlm.Prompts.Docx = text; break;
                        case DocumentKind.Xlsx: s.Vlm.Prompts.Xlsx = text; break;
                        case DocumentKind.Pptx: s.Vlm.Prompts.Pptx = text; break;
                        default: s.Vlm.Prompts.Pdf = text; break;
                    }
                });
                return;
            }

            if (_shown - 1 >= Variants.Items.Count)
            {
                return;
            }

            var index = _shown - 1;
            _window._store.Update(s =>
            {
                var item = s.Vlm.Prompts.VariantsOf(_kind).Items[index];
                var preset = item.Preset is null ? null : PromptPresets.TextOf(_kind, item.Preset, Language);
                var text = string.IsNullOrWhiteSpace(Box.Text) ? preset ?? Standard : Box.Text;
                // A built-in example that is not changed keeps no text: it follows the language.
                item.Text = preset is not null && text == preset ? string.Empty : text;
                if (text != Box.Text)
                {
                    Box.Text = text;
                }
            });
        }

        public void Reset()
        {
            if (_shown == 0)
            {
                Box.Text = Standard;
            }
            else if (_shown > 0 && Variants.Items[_shown - 1].Preset is { } preset)
            {
                Box.Text = PromptPresets.TextOf(_kind, preset, Language);
            }
            else
            {
                Box.Text = Standard;
            }

            Commit();
        }

        private void OnAdd()
        {
            Commit();
            var name = _window.AskName(_kind, UiText.Get("Prompts_Add"), string.Empty, null);
            if (name is null)
            {
                return;
            }

            var text = Box.Text;
            _window._store.Update(s =>
            {
                var variants = s.Vlm.Prompts.VariantsOf(_kind);
                variants.Items.Add(new NamedPrompt { Name = name, Text = text });
                variants.Selected = name;
            });
            Reload();
        }

        private void OnRename()
        {
            if (_shown <= 0)
            {
                return;
            }

            Commit();
            var oldName = Variants.Items[_shown - 1].Name;
            var name = _window.AskName(_kind, UiText.Get("Prompts_Rename"), oldName, oldName);
            if (name is null || name == oldName)
            {
                return;
            }

            var index = _shown - 1;
            _window._store.Update(s =>
            {
                var variants = s.Vlm.Prompts.VariantsOf(_kind);
                variants.Items[index].Name = name;
                variants.Selected = name;
            });
            Reload();
        }

        private void OnDelete()
        {
            if (_shown <= 0)
            {
                return;
            }

            var name = Variants.Items[_shown - 1].Name;
            if (MessageBox.Show(_window, UiText.Get("Prompts_DeleteConfirm", name), UiText.Get("Prompts_Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var index = _shown - 1;
            _shown = -1; // nothing to save
            _window._store.Update(s =>
            {
                var variants = s.Vlm.Prompts.VariantsOf(_kind);
                variants.Items.RemoveAt(index);
                variants.Selected = string.Empty;
            });
            Reload();
        }
    }
}
