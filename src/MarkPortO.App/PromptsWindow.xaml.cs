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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;

namespace MarkPortO.App;

/// <summary>Edits the prompt sent to the VLM for each document type. Each box saves when it loses focus.</summary>
public partial class PromptsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly string _language;

    public PromptsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;
        // A prompt that was never edited is shown in the display language.
        _language = LanguageResolver.Resolve(store.Current.Ui.Language);
        DocxBox.Text = store.Current.Vlm.Prompts.For(DocumentKind.Docx, _language);
        XlsxBox.Text = store.Current.Vlm.Prompts.For(DocumentKind.Xlsx, _language);
        PptxBox.Text = store.Current.Vlm.Prompts.For(DocumentKind.Pptx, _language);
        PdfBox.Text = store.Current.Vlm.Prompts.For(DocumentKind.Pdf, _language);
        DocxBox.LostFocus += (_, _) => Save(DocumentKind.Docx, DocxBox);
        XlsxBox.LostFocus += (_, _) => Save(DocumentKind.Xlsx, XlsxBox);
        PptxBox.LostFocus += (_, _) => Save(DocumentKind.Pptx, PptxBox);
        PdfBox.LostFocus += (_, _) => Save(DocumentKind.Pdf, PdfBox);
        Closing += (_, _) =>
        {
            Save(DocumentKind.Docx, DocxBox);
            Save(DocumentKind.Xlsx, XlsxBox);
            Save(DocumentKind.Pptx, PptxBox);
            Save(DocumentKind.Pdf, PdfBox);
        };
    }

    private string DefaultFor(DocumentKind kind) => PromptSettings.DefaultFor(kind, _language);

    private void Save(DocumentKind kind, TextBox box)
    {
        // A prompt must not be empty (settings validation): an empty box means "use the default".
        var text = string.IsNullOrWhiteSpace(box.Text) ? DefaultFor(kind) : box.Text;
        if (text != box.Text)
        {
            box.Text = text;
        }

        _store.Update(s =>
        {
            switch (kind)
            {
                case DocumentKind.Docx: s.Vlm.Prompts.Docx = text; break;
                case DocumentKind.Xlsx: s.Vlm.Prompts.Xlsx = text; break;
                case DocumentKind.Pptx: s.Vlm.Prompts.Pptx = text; break;
                default: s.Vlm.Prompts.Pdf = text; break;
            }
        });
    }

    // Puts {pagetext} at the caret of the box that is shown (in place of the selected text), and keeps the focus in the box.
    private void OnInsertPageText(object sender, RoutedEventArgs e)
    {
        var (kind, box) = Tabs.SelectedIndex switch
        {
            0 => (DocumentKind.Docx, DocxBox),
            1 => (DocumentKind.Xlsx, XlsxBox),
            2 => (DocumentKind.Pptx, PptxBox),
            _ => (DocumentKind.Pdf, PdfBox),
        };
        var at = box.SelectionStart;
        box.SelectedText = MarkPortO.Core.Vlm.PromptSupplement.PageTextPlaceholder;
        box.CaretIndex = at + MarkPortO.Core.Vlm.PromptSupplement.PageTextPlaceholder.Length;
        box.Focus();
        Save(kind, box);
    }

    private void OnResetCurrent(object sender, RoutedEventArgs e)
    {
        var (kind, box) = Tabs.SelectedIndex switch
        {
            0 => (DocumentKind.Docx, DocxBox),
            1 => (DocumentKind.Xlsx, XlsxBox),
            2 => (DocumentKind.Pptx, PptxBox),
            _ => (DocumentKind.Pdf, PdfBox),
        };
        box.Text = DefaultFor(kind);
        Save(kind, box);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
