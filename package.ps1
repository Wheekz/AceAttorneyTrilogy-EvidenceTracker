# Construit le mod et produit deux archives dans dist/ (chemins en « / », lisibles sous Linux / Steam Deck) :
#  - AceAttorneyTrilogy-EvidenceTracker-<v>-AIO.zip      : BepInEx 5 x86 + mod, une seule extraction
#  - AceAttorneyTrilogy-EvidenceTracker-<v>-ModOnly.zip  : le mod seul, pour qui a déjà BepInEx
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$BepVersion = "5.4.23.6"
$BepZipName = "BepInEx_win_x86_$BepVersion.zip"
$BepSha256  = "a4024dafaca5d3784097e34b3c2921415030c0b461e52f30053ed8914be9622d"

dotnet build -c Release | Out-Null
$version = (Select-String -Path Plugin.cs -Pattern 'BepInPlugin\("[^"]+", "[^"]+", "([^"]+)"\)').Matches[0].Groups[1].Value
New-Item -ItemType Directory dist -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-PortableZip([string]$sourceDir, [string]$zipPath) {
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    $archive = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        Get-ChildItem $sourceDir -Recurse -File -Force | ForEach-Object {
            $entry = $_.FullName.Substring($sourceDir.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
}

function New-Stage([string]$name) {
    $dir = Join-Path $env:TEMP $name
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory "$dir\BepInEx\plugins" | Out-Null
    Copy-Item "bin\Release\net35\PWAAT_QoL.dll" "$dir\BepInEx\plugins\"
    Copy-Item README.md, LICENSE $dir
    return $dir
}

# 1) Mod seul
$stage = New-Stage "pwaat_qol_modonly"
$modOnly = Join-Path (Resolve-Path dist) "AceAttorneyTrilogy-EvidenceTracker-$version-ModOnly.zip"
New-PortableZip $stage $modOnly
Remove-Item $stage -Recurse -Force

# 2) Tout-en-un : BepInEx officiel (empreinte vérifiée), extrait tel quel, + le mod
$cache = Join-Path $env:TEMP $BepZipName
if (-not (Test-Path $cache) -or (Get-FileHash $cache -Algorithm SHA256).Hash.ToLower() -ne $BepSha256) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$BepVersion/$BepZipName" -OutFile $cache -UseBasicParsing
}
if ((Get-FileHash $cache -Algorithm SHA256).Hash.ToLower() -ne $BepSha256) { throw "$BepZipName : empreinte SHA256 incorrecte, abandon." }
$stage = New-Stage "pwaat_qol_aio"
Expand-Archive $cache -DestinationPath $stage -Force
New-Item -ItemType Directory "$stage\third_party" -Force | Out-Null
Copy-Item third_party\* "$stage\third_party\"
$aio = Join-Path (Resolve-Path dist) "AceAttorneyTrilogy-EvidenceTracker-$version-AIO.zip"
New-PortableZip $stage $aio
Remove-Item $stage -Recurse -Force

Write-Host "Archives :"; Write-Host "  $aio"; Write-Host "  $modOnly"
