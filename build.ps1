param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sourcePath = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$exePath = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot 'PinyinRussian.exe' }
$interopPath = Join-Path $PSScriptRoot 'Interop.UIAutomationClient.dll'
if (-not (Test-Path -LiteralPath $interopPath -PathType Leaf)) {
    $buildDir = Join-Path $PSScriptRoot '.build'
    New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
    $importerPath = Join-Path $buildDir 'ImportUIAutomation.exe'
    & (Join-Path $frameworkPath 'csc.exe') /nologo /target:exe /platform:x64 "/out:$importerPath" (Join-Path $PSScriptRoot 'tools\ImportUIAutomation.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Interop generator build failed.' }
    & $importerPath (Join-Path $env:WINDIR 'System32\UIAutomationCore.dll') $interopPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $interopPath -PathType Leaf)) { throw 'Interop generation failed.' }
}
& (Join-Path $frameworkPath 'csc.exe') /nologo /target:winexe /platform:x64 "/out:$exePath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "/reference:$PSScriptRoot\Interop.UIAutomationClient.dll" "/reference:$frameworkPath\WPF\UIAutomationClient.dll" "/reference:$frameworkPath\WPF\UIAutomationTypes.dll" "/reference:$frameworkPath\WPF\WindowsBase.dll" "/r:$env:WINDIR/System32/WinMetadata/Windows.Foundation.winmd" "/r:$env:WINDIR/System32/WinMetadata/Windows.Globalization.winmd" "/r:$env:WINDIR/System32/WinMetadata/Windows.Media.winmd" "/r:$env:WINDIR/System32/WinMetadata/Windows.Graphics.winmd" "/r:$env:WINDIR/System32/WinMetadata/Windows.Storage.winmd" "/r:$frameworkPath/System.Runtime.dll" "/r:$frameworkPath/System.Runtime.InteropServices.WindowsRuntime.dll" "/r:$frameworkPath/System.Runtime.WindowsRuntime.dll" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $exePath
