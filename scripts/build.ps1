# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 umineko73
#
# This file is part of MarkPortO.
#
# MarkPortO is free software: you can redistribute it and/or modify it under the terms of
# the GNU General Public License, version 3 only, as published by the Free Software Foundation.
#
# MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
# without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License along with MarkPortO.
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
# The script is in scripts/; everything is relative to the repository root.
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Step([string] $Name, [scriptblock] $Body) {
    Write-Host "== $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed (exit code $LASTEXITCODE)."
    }
}

Invoke-Step 'restore' { dotnet restore MarkPortO.slnx }
# TreatWarningsAsErrors is on in Directory.Build.props: a warning fails the build.
Invoke-Step 'build' { dotnet build MarkPortO.slnx -c $Configuration --no-restore }

if ($Task -in 'Test', 'Publish') {
    $filter = @()
    if ($SkipEngines) {
        $filter = @('--filter', 'Engine!=Office&Engine!=LibreOffice')
    }
    elseif ($SkipOffice) {
        $filter = @('--filter', 'Engine!=Office')
    }

    Invoke-Step 'test' { dotnet test MarkPortO.slnx -c $Configuration --no-build @filter }
}

if ($Task -eq 'Publish') {
    $out = Join-Path $root 'artifacts'
    if (Test-Path $out) {
        Remove-Item $out -Recurse -Force
    }

    # Framework-dependent: the .NET 10 Desktop Runtime must be installed on the computer that runs it.
    # The names MarkPortO.exe (app) and markporto.exe (command line) differ only in case, which is the same name on
    # Windows, so the command line goes to its own folder. The app also copies LICENSE.txt and THIRD-PARTY-NOTICES.md.
    $root = Join-Path $out 'MarkPortO'
    Invoke-Step 'publish app' { dotnet publish src/MarkPortO.App -c $Configuration --no-build -o $root }
    Invoke-Step 'publish cli' { dotnet publish src/MarkPortO.Cli -c $Configuration --no-build -o (Join-Path $root 'cli') }
    Copy-Item README.md, README.ja.md -Destination $root

    $zip = Join-Path $out 'MarkPortO-win-x64.zip'
    Compress-Archive -Path (Join-Path $out 'MarkPortO\*') -DestinationPath $zip
    Write-Host "Wrote $zip"
}
