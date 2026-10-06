param(
    [string]$Version = '1.1.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.build\release')
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$zipPath = Join-Path $outputPath "PinyinRussian-v$Version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { throw 'Release archive already exists. Choose a new output directory.' }
& (Join-Path $repoPath 'build.ps1') | Out-Null
# Explicit public-file allowlist. Never recurse through an installed program directory.
$publicFiles = @(
    'PinyinRussian.exe',
    'Interop.UIAutomationClient.dll',
    'README.md',
    'CHANGELOG.md',
    '使用说明.md',
    'data/russian-stress.bin',
    'data/russian-stress-ambiguous.txt',
    'data/来源.md',
    'data/RUAccent-LICENSE.txt',
    'docs/selected-candidate.png',
    'docs/transparency-settings.png',
    'docs/deepseek-settings.png',
    'docs/deepseek-balance-settings.png'
)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($relative in $publicFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoPath $relative) -PathType Leaf)) { throw "Missing release file: $relative" }
}
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $publicFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $repoPath $relative), $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $outputPath 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
Write-Output $zipPath
