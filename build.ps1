[CmdletBinding()]
param(
    [string]$GamePath,
    [string]$ManagedPath,
    [string]$BepInExCorePath,
    [string]$CompilerPath,
    [string]$Name = 'LoadProfiler',
    [string]$PluginFolder = 'LoadProfiler',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$sourceDir = Join-Path $repoRoot 'Source'
$outDir = Join-Path $repoRoot 'build'

function Find-FirstExistingPath([string[]]$Candidates) {
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

if (-not $ManagedPath -and $GamePath) { $ManagedPath = Join-Path $GamePath 'UltimateChickenHorse_Data\Managed' }
if (-not $BepInExCorePath -and $GamePath) { $BepInExCorePath = Join-Path $GamePath 'BepInEx\core' }
if (-not $ManagedPath) {
    $ManagedPath = Find-FirstExistingPath @(
        (Join-Path $repoRoot 'refs\Managed'),
        (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Ultimate Chicken Horse\UltimateChickenHorse_Data\Managed'),
        (Join-Path $env:ProgramFiles 'Steam\steamapps\common\Ultimate Chicken Horse\UltimateChickenHorse_Data\Managed')
    )
}
if (-not $BepInExCorePath) {
    $BepInExCorePath = Find-FirstExistingPath @(
        (Join-Path $repoRoot 'refs\BepInEx\core'),
        (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Ultimate Chicken Horse\BepInEx\core'),
        (Join-Path $env:ProgramFiles 'Steam\steamapps\common\Ultimate Chicken Horse\BepInEx\core')
    )
}
if (-not $ManagedPath -or -not (Test-Path -LiteralPath (Join-Path $ManagedPath 'Assembly-CSharp.dll'))) {
    throw 'Could not find game Managed assemblies. Pass -GamePath, -ManagedPath, or place local references in refs\Managed.'
}
if (-not $BepInExCorePath -or -not (Test-Path -LiteralPath (Join-Path $BepInExCorePath 'BepInEx.dll'))) {
    throw 'Could not find BepInEx 5 core assemblies. Pass -GamePath, -BepInExCorePath, or place references in refs\BepInEx\core.'
}
if (-not (Test-Path -LiteralPath $sourceDir)) { throw "Source directory not found: $sourceDir" }
$sources = @(Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -File)
if ($sources.Count -eq 0) { throw "No .cs files found in $sourceDir" }

if (-not $CompilerPath) {
    $CompilerPath = Find-FirstExistingPath @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    if (-not $CompilerPath) {
        $command = Get-Command 'csc.exe' -ErrorAction SilentlyContinue
        if ($command) { $CompilerPath = $command.Source }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'C# compiler csc.exe not found. Install .NET Framework 4.x Developer Pack/Build Tools, or pass -CompilerPath.'
}
$harmonyPath = Join-Path $BepInExCorePath '0Harmony.dll'
if (-not (Test-Path -LiteralPath $harmonyPath)) { throw "Harmony assembly not found: $harmonyPath" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$output = Join-Path $outDir ($Name + '.dll')
$arguments = @('/nologo','/noconfig','/langversion:5','/nostdlib+','/target:library','/optimize+','/debug-','/warn:4',('/out:"' + $output + '"'))
foreach ($reference in Get-ChildItem -LiteralPath $ManagedPath -Filter '*.dll' -File) { $arguments += ('/reference:"' + $reference.FullName + '"') }
foreach ($referenceName in @('BepInEx.dll','0Harmony.dll')) { $arguments += ('/reference:"' + (Join-Path $BepInExCorePath $referenceName) + '"') }
foreach ($source in $sources) { $arguments += ('"' + $source.FullName + '"') }
$result = & $CompilerPath $arguments 2>&1
if ($LASTEXITCODE -ne 0) { throw ($result -join [Environment]::NewLine) }
Write-Output "Built $output"

if ($Install) {
    if (-not $GamePath) { throw 'The -Install switch requires -GamePath <game folder>.' }
    $target = Join-Path $GamePath ('BepInEx\plugins\' + $PluginFolder)
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath $output -Destination $target -Force
    Write-Output "Installed to $target"
}
