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

using MarkPortO.Core.Localization;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Pipeline;

/// <summary>Why Step 3 stopped starting new readings.</summary>
public enum Step3Stop
{
    None,
    UsageLimit,
    NotLoggedIn,
    RepeatedFailures,
    PageLimit,
    CostLimit,
}

/// <summary>
/// Decides whether Step 3 may start reading another page (FR-049, FR-057). It is shared by every document of a job,
/// and its counts continue over a resume. Pages that are being read are always finished, so with several readings at
/// a time a limit can be passed by up to the number of readings minus one.
/// </summary>
public sealed class Step3Governor
{
    /// <summary>How many failures of the same kind in a row stop the job (FR-049).</summary>
    public const int FailuresInARowToStop = 3;

    private readonly object _gate = new();
    private readonly int? _maxPages;
    private readonly double? _maxCostUsd;
    private int _pagesRead;
    private double _costUsd;
    private int _failuresInARow;
    private VlmFailureKind _failureKind;
    private string _failureMessage = string.Empty;

    /// <param name="pagesRead">Pages already read in the job (before a resume).</param>
    /// <param name="costUsd">Cost already spent in the job (before a resume).</param>
    public Step3Governor(int? maxPages = null, double? maxCostUsd = null, int pagesRead = 0, double costUsd = 0)
    {
        _maxPages = maxPages;
        _maxCostUsd = maxCostUsd;
        _pagesRead = pagesRead;
        _costUsd = costUsd;
    }

    public Step3Stop Stop { get; private set; }

    public int PagesRead
    {
        get { lock (_gate) { return _pagesRead; } }
    }

    public double CostUsd
    {
        get { lock (_gate) { return _costUsd; } }
    }

    /// <summary>True when a new page may be started. False once a limit is reached or the job was stopped.</summary>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (Stop == Step3Stop.None)
            {
                if (_maxPages is { } pages && _pagesRead >= pages)
                {
                    Stop = Step3Stop.PageLimit;
                }
                else if (_maxCostUsd is { } cost && _costUsd >= cost)
                {
                    Stop = Step3Stop.CostLimit;
                }
            }

            return Stop == Step3Stop.None;
        }
    }

    public void Succeeded(double? costUsd)
    {
        lock (_gate)
        {
            _pagesRead++;
            _costUsd += costUsd ?? 0;
            _failuresInARow = 0;
        }
    }

    public void Failed(VlmFailureKind kind, string message)
    {
        lock (_gate)
        {
            if (kind == VlmFailureKind.UsageLimit)
            {
                Stop = Stop == Step3Stop.None ? Step3Stop.UsageLimit : Stop;
                _failureMessage = message;
            }
            else if (kind == VlmFailureKind.NotLoggedIn)
            {
                Stop = Stop == Step3Stop.None ? Step3Stop.NotLoggedIn : Stop;
                _failureMessage = message;
            }
            else if (kind.CountsTowardsStopping())
            {
                _failuresInARow = kind == _failureKind ? _failuresInARow + 1 : 1;
                _failureKind = kind;
                _failureMessage = message;
                if (_failuresInARow >= FailuresInARowToStop && Stop == Step3Stop.None)
                {
                    Stop = Step3Stop.RepeatedFailures;
                }
            }
        }
    }

    /// <summary>The sentence that tells the user why the job stopped; <c>null</c> when it did not.</summary>
    public string? Reason
    {
        get
        {
            lock (_gate)
            {
                return Stop switch
                {
                    Step3Stop.None => null,
                    Step3Stop.UsageLimit => Loc.T("The conversion was stopped because the usage limit of the command was reached ({0}). Resume it after the limit is lifted.", _failureMessage),
                    Step3Stop.NotLoggedIn => Loc.T("The conversion was stopped because the command is not logged in ({0}). Log in and resume it.", _failureMessage),
                    Step3Stop.RepeatedFailures => Loc.T("The conversion was stopped because {0} pages in a row failed in the same way ({1}).", FailuresInARowToStop, _failureMessage),
                    Step3Stop.PageLimit => Loc.T("The conversion was stopped after {0} pages, the limit of the connection. Raise the limit and resume it.", _pagesRead),
                    Step3Stop.CostLimit => Loc.T("The conversion was stopped after the cost of {0:0.####} US dollars, the limit of the connection. Raise the limit and resume it.", _costUsd),
                    _ => null,
                };
            }
        }
    }
}
