$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot 'dictionary-source'
$revision = 'b78ae5ea1e62beaf138bed1865cd8c3b0b5ca855'
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
foreach ($name in @('accents', 'omographs', 'yo_words', 'yo_homographs')) {
    $fileName = $name + '.json.gz'
    $uri = 'https://huggingface.co/ruaccent/accentuator/resolve/' + $revision + '/' + $fileName
    Invoke-WebRequest -Uri $uri -OutFile (Join-Path $sourceDir $fileName) -UseBasicParsing
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'data\RUAccent-LICENSE.txt') -Destination (Join-Path $sourceDir 'RUAccent-LICENSE.txt') -Force
[IO.File]::WriteAllText((Join-Path $sourceDir 'source-revision.txt'), $revision, (New-Object Text.UTF8Encoding($false)))
Write-Output 'Downloaded pinned dictionary sources. Run scripts/build_lexicon.py to build data.'
