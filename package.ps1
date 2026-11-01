# Construit le mod et produit dist/PWAAT_QoL-<version>.zip (structure prête à extraire dans le dossier du jeu).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet build -c Release | Out-Null
$version = (Select-String -Path Plugin.cs -Pattern 'BepInPlugin\("[^"]+", "[^"]+", "([^"]+)"\)').Matches[0].Groups[1].Value
$stage = Join-Path $env:TEMP "pwaat_qol_pkg"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$stage\BepInEx\plugins" | Out-Null
Copy-Item "bin\Release\net35\PWAAT_QoL.dll" "$stage\BepInEx\plugins\"
Copy-Item README.md, LICENSE $stage
New-Item -ItemType Directory dist -Force | Out-Null
$zip = Join-Path (Resolve-Path dist) "AceAttorneyTrilogy-EvidenceTracker-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
# Compress-Archive (PS 5.1) écrit des « \ » dans les chemins : illisibles sous Linux / Steam Deck. On construit l'archive à la main.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
Write-Host "Archive : $zip"
