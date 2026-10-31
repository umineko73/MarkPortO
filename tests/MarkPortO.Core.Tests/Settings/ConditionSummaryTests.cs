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

using System.Globalization;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Tests.Settings;

/// <summary>The line that says what is chosen in the folded part of the conditions (FR-102).</summary>
public class ConditionSummaryTests
{
    private static string Build(AppSettings settings, string culture)
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return ConditionSummary.Build(settings);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    private static AppSettings Settings(Action<AppSettings>? change = null)
    {
        var settings = AppSettings.CreateDefault();
        settings.Input.Types = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf];
        settings.Input.Recurse = true;
        settings.Engine.Kind = EngineKind.LibreOffice;
        settings.Images.Dpi = 150;
        settings.Input.Emphasis = true;
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void TheChoices_AreInOneLine_InJapanese()
    {
        Assert.Equal("docx xlsx pptx pdf | サブフォルダ | LibreOffice | 150 DPI", Build(Settings(), "ja"));
    }

    [Fact]
    public void TheChoices_AreInOneLine_InEnglish()
    {
        Assert.Equal("docx xlsx pptx pdf | Sub-folders | LibreOffice | 150 DPI", Build(Settings(), "en"));
    }

    [Fact]
    public void ThingsThatAreOff_AreLeftOutOrSaid()
    {
        var settings = Settings(s =>
        {
            s.Input.Types = [DocumentKind.Pdf, DocumentKind.Docx];
            s.Input.Recurse = false;
            s.Engine.Kind = EngineKind.Office;
            s.Images.Dpi = 300;
            s.Input.Emphasis = false;
        });

        Assert.Equal("docx pdf | Microsoft Office | 300 DPI | 太字・斜体なし", Build(settings, "ja"));
    }

    [Fact]
    public void AKindWithAnExtractor_ShowsItsName()
    {
        var settings = Settings(s =>
        {
            s.Input.Extractors.Docx = "pandoc";
            s.Input.Extractors.Pdf = " docling ";
        });

        Assert.Equal("docx(pandoc) xlsx pptx pdf(docling) | Sub-folders | LibreOffice | 150 DPI", Build(settings, "en"));
    }

    [Fact]
    public void AnExtractorOfAKindThatIsNotChosen_IsNotShown()
    {
        var settings = Settings(s =>
        {
            s.Input.Types = [DocumentKind.Docx];
            s.Input.Extractors.Pdf = "docling";
        });

        Assert.Equal("docx | Sub-folders | LibreOffice | 150 DPI", Build(settings, "en"));
    }

    [Fact]
    public void NoKindOfFile_IsSaid()
    {
        Assert.StartsWith("対象の種類なし | ", Build(Settings(s => s.Input.Types = []), "ja"));
    }

    [Fact]
    public void TheStepsTheConnectionAndThePageRange_AreNotInTheLine()
    {
        var line = Build(Settings(s => { s.Steps.Step3 = true; s.Vlm.Selected = "haiku"; }), "en");

        Assert.DoesNotContain("haiku", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Step", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFoldedPart_IsOpenByDefault_AndItIsInTheSettingsFile()
    {
        Assert.True(AppSettings.CreateDefault().Ui.ConditionsExpanded);
        var closed = AppSettings.CreateDefault();
        closed.Ui.ConditionsExpanded = false;

        var json = MarkPortO.Core.Jobs.Models.JsonDefaults.Serialize(closed);
        Assert.Contains("\"conditionsExpanded\": false", json.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
        Assert.False(MarkPortO.Core.Jobs.Models.JsonDefaults.Deserialize<AppSettings>(json)!.Ui.ConditionsExpanded);
    }
}
