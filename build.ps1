# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 umineko73
#
# This file is part of MarkPort.
#
# MarkPort is free software: you can redistribute it and/or modify it under the terms of
# the GNU General Public License, version 3 only, as published by the Free Software Foundation.
#
# MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
# without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License along with MarkPort.
# If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

[CmdletBinding()]
param(
    # Build: restore and build.  Test: build and run the tests.  Publish: build, test and write the programs to artifacts/.
    [ValidateSet('Build', 'Test', 'Publish')]
    [string] $Task = 'Test',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    # Skip the tests that drive Microsoft Office (a machine without Office, such as a CI runner).
    [switch] $SkipOffice,

    # Skip the tests that need a conversion engine at all (neither Office nor LibreOffice is installed).
    [switch] $SkipEngines
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host "== $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE)."
    }
}

Invoke-Step 'restore' { dotnet restore MarkPort.slnx }
# TreatWarningsAsErrors is on in Directory.Build.props: a warning fails the build.
Invoke-Step 'build' { dotnet build MarkPort.slnx -c $Configuration --no-restore }

if ($Task -in 'Test', 'Publish') {
    $filter = @()
    if ($SkipEngines) {
        $filter = @('--filter', 'Engine!=Office&Engine!=LibreOffice')
    }
    elseif ($SkipOffice) {
        $filter = @('--filter', 'Engine!=Office')
    }

    Invoke-Step 'test' { dotnet test MarkPort.slnx -c $Configuration --no-build @filter }
}

if ($Task -eq 'Publish') {
    $out = Join-Path $PSScriptRoot 'artifacts'
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }

    # Framework-dependent: the .NET 10 Desktop Runtime must be installed on the computer that runs it.
    # The names MarkPort.exe (app) and markport.exe (command line) differ only in case, which is the same name on
    # Windows, so the command line goes to its own folder. The app also copies LICENSE.txt and THIRD-PARTY-NOTICES.md.
    $root = Join-Path $out 'MarkPort'
    Invoke-Step 'publish app' { dotnet publish src/MarkPort.App -c $Configuration --no-build -o $root }
    Invoke-Step 'publish cli' { dotnet publish src/MarkPort.Cli -c $Configuration --no-build -o (Join-Path $root 'cli') }
    Copy-Item README.md, README.ja.md -Destination $root

    $zip = Join-Path $out 'MarkPort-win-x64.zip'
    Compress-Archive -Path (Join-Path $out 'MarkPort\*') -DestinationPath $zip
    Write-Host "Wrote $zip"
}
