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

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Tests.Settings;

/// <summary>"Hidden sheets" and "hidden slides" are two settings; the one setting of the earlier versions sets both.</summary>
public class HiddenSettingsTests
{
    [Fact]
    public void AnEarlierSettingsFile_WithOneIncludeHidden_SetsBothSettings()
    {
        var settings = JsonDefaults.Deserialize<AppSettings>("{\"schemaVersion\":1,\"input\":{\"includeHidden\":true}}")!;

        Assert.True(settings.Input.IncludeHiddenSheets);
        Assert.True(settings.Input.IncludeHiddenSlides);
    }

    [Fact]
    public void ANewSettingsFile_KeepsTheTwoApart_AndDoesNotWriteTheOldOne()
    {
        var settings = new AppSettings();
        settings.Input.IncludeHiddenSlides = true;

        var json = JsonDefaults.Serialize(settings);
        var back = JsonDefaults.Deserialize<AppSettings>(json)!;

        Assert.DoesNotContain("\"includeHidden\"", json);
        Assert.False(back.Input.IncludeHiddenSheets);
        Assert.True(back.Input.IncludeHiddenSlides);
    }

    [Fact]
    public void AnEarlierJob_WithOneIncludeHidden_SetsBothOptions()
    {
        var options = JsonDefaults.Deserialize<OptionsSnapshot>("{\"includeHidden\":true}")!;

        Assert.True(options.IncludeHiddenSheets);
        Assert.True(options.IncludeHiddenSlides);
    }
}
