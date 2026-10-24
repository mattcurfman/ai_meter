# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0
param([switch]$Test, [switch]$Publish)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products Microsoft.VisualStudio.Product.Community -version '[18.0,19.0)' -requires Microsoft.VisualStudio.Workload.ManagedDesktop -property installationPath
if (-not $visualStudio) { throw 'Install Visual Studio 2026 Community with .NET desktop development.' }
$msbuild = Join-Path $visualStudio 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'AIMeter.slnx') /restore /p:Configuration=Release /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
if ($Test) {
    $runner = Join-Path $visualStudio 'Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
    & $runner (Join-Path $PSScriptRoot 'AIMeter.Tests\bin\Release\net10.0-windows\AIMeter.Tests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed.' }
}
if ($Publish) {
    $output = Join-Path $PSScriptRoot 'build\win-x64\'
    & $msbuild (Join-Path $PSScriptRoot 'AIMeter\AIMeter.csproj') /restore /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true "/p:PublishDir=$output" /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'The publish step failed.' }
    Write-Output "Run: $($output)AIMeter.exe"
}
