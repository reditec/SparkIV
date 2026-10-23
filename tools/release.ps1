# Builds a SparkIV release: publishes the app (x64, self-contained, so no .NET install is needed) and packs it
# with Velopack into release\out. Upload every file from there to the GitHub release of the same version
# (tag v<version>; mark alpha and beta versions as pre-release). The version comes from src\Directory.Build.props.
# See docs/versioning.md.
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) {
  $Version = ([xml](Get-Content src\Directory.Build.props)).Project.PropertyGroup.Version
}
$publish = Join-Path $root 'release\publish'
$out = Join-Path $root 'release\out'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet tool restore
dotnet publish src\SparkIV\SparkIV.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The id names the install folder (%LocalAppData%\<id>), which an uninstall removes; it must not be "SparkIV",
# where the settings are kept. It can't change later, or installed versions no longer find their updates.
dotnet vpk pack --packId reditec.SparkIV --packVersion $Version --packTitle SparkIV --packAuthors reditec `
  --packDir $publish --mainExe SparkIV.exe --icon src\GTAIV.ico --outputDir $out
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

"Release $Version is in $out"
