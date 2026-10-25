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

using MarkPort.Core.Tests.Localization;

namespace MarkPort.Core.Tests.Localization;

/// <summary>A test class whose tests check the Japanese messages: the language of the messages is Japanese while each test runs.</summary>
public abstract class JapaneseUiTests : IDisposable
{
    private readonly IDisposable _culture = TestCulture.Use("ja");

    public void Dispose()
    {
        _culture.Dispose();
        GC.SuppressFinalize(this);
    }
}
