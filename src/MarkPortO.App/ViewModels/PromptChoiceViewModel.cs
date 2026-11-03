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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;

namespace MarkPortO.App.ViewModels;

/// <summary>The choice of the prompt of one kind of file on the main screen (FR-115): the standard one, or one of those with a name.</summary>
public sealed class PromptChoiceViewModel(DocumentKind kind, SettingsStore store) : ObservableObject
{
    public string Label => kind switch
    {
        DocumentKind.Docx => "Word",
        DocumentKind.Xlsx => "Excel",
        DocumentKind.Pptx => "PowerPoint",
        _ => "PDF",
    };

    /// <summary>The standard prompt first, then the prompts with a name.</summary>
    public IReadOnlyList<string> Items =>
        [UiText.Get("Prompt_Standard"), .. store.Current.Vlm.Prompts.VariantsOf(kind).Items.Select(p => p.Name)];

    public int SelectedIndex
    {
        get
        {
            var variants = store.Current.Vlm.Prompts.VariantsOf(kind);
            var index = variants.Items.FindIndex(p => string.Equals(p.Name, variants.Selected, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index + 1;
        }

        set
        {
            var names = store.Current.Vlm.Prompts.VariantsOf(kind).Items.Select(p => p.Name).ToList();
            if (value < 0 || value > names.Count)
            {
                return; // the list was replaced and nothing is chosen yet
            }

            store.Update(s => s.Vlm.Prompts.VariantsOf(kind).Selected = value == 0 ? string.Empty : names[value - 1]);
            Raise();
        }
    }

    /// <summary>The list or the choice was changed elsewhere (the window of the prompts).</summary>
    public void Refresh()
    {
        Raise(nameof(Items));
        Raise(nameof(SelectedIndex));
    }
}
