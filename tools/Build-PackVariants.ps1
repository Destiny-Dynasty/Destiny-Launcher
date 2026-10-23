[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PackDirectory,
    [string]$ContentDirectory,
    [string]$SourceOverridesPath,
    [string]$ReleaseMetadataPath,
    [string]$Repository = 'Destiny-Dynasty/Modpack'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepositoryRoot).Path)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($Version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'Use a version such as 1.4.9.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'OutputDirectory must be empty.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$basePath = Join-Path $repo 'base/manifest.json'
$base = Get-Content -LiteralPath $basePath -Raw | ConvertFrom-Json
$tag = "modpack-$Version"
$releaseBase = "https://github.com/$Repository/releases/download/$tag"
if ($PackDirectory) {
    $parameters = @{ PackId='destiny';PackDirectory=$PackDirectory;Version=$Version;ReleaseBaseUrl=$releaseBase;
        OutputDirectory="$output/base-build";PreviousManifestPath=$basePath;MinecraftVersion=$base.minecraftVersion;
        FabricLoaderVersion=$base.fabricLoaderVersion;JavaVersion=$base.javaVersion }
    if ($SourceOverridesPath) { $parameters.SourceOverridesPath = $SourceOverridesPath }
    & (Join-Path $PSScriptRoot 'New-PackManifest.ps1') @parameters
    $basePath = "$output/base-build/manifests/destiny.json"
}
$parameters = @{ BaseManifestPath=$basePath;DefinitionsDirectory=(Join-Path $repo 'variants');
    ContentDirectory=$(if($ContentDirectory){$ContentDirectory}else{$repo});ReleaseBaseUrl=$releaseBase;
    OutputDirectory="$output/variants-build";Version=$Version }
if ($ReleaseMetadataPath) { $parameters.ReleaseMetadataPath = $ReleaseMetadataPath }
& (Join-Path $PSScriptRoot 'New-PackVariants.ps1') @parameters
New-Item -ItemType Directory -Force -Path "$output/manifests", "$output/release-assets" | Out-Null
Copy-Item -LiteralPath $basePath -Destination "$output/base-manifest.json"
$nextBase = Get-Content -LiteralPath "$output/base-manifest.json" -Raw | ConvertFrom-Json -AsHashtable
$nextBase.version = $Version
[IO.File]::WriteAllText("$output/base-manifest.json", ($nextBase | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Get-ChildItem -LiteralPath "$output/variants-build/manifests" -File | Copy-Item -Destination "$output/manifests"
foreach ($folder in @("$output/base-build/release-assets", "$output/variants-build/release-assets")) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $folder -File) {
        $target = "$output/release-assets/$($file.Name)"
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Release asset name collision.' }
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$build = @{repository=$Repository;tag=$tag;version=$Version;variants=@(Get-ChildItem -LiteralPath "$output/manifests" -File | ForEach-Object {$_.BaseName});assets=@()}
$build.assets = @(Get-ChildItem -LiteralPath "$output/release-assets" -File | ForEach-Object { @{name=$_.Name;size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
[IO.File]::WriteAllText("$output/build.json", ($build | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Prepared $($build.variants.Count) variants and $($build.assets.Count) shared assets at $output"
