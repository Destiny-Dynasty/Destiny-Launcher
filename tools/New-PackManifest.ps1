[CmdletBinding(DefaultParameterSetName = 'Release')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('destiny', 'destiny-lite')]
    [string]$PackId,

    [Parameter(Mandatory = $true)]
    [string]$PackDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true, ParameterSetName = 'Release')]
    [string]$ReleaseBaseUrl,

    [Parameter(Mandatory = $true, ParameterSetName = 'Repository')]
    [string]$RepositoryFilesBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$MinecraftVersion = '1.20.1',
    [string]$FabricLoaderVersion = '0.16.9'
)

$ErrorActionPreference = 'Stop'
$packRoot = (Resolve-Path -LiteralPath $PackDirectory).Path
$outRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$repositoryMode = $PSCmdlet.ParameterSetName -eq 'Repository'
$assetDirectoryName = if ($repositoryMode) { 'repository-files' } else { 'release-assets' }
$assetRoot = Join-Path $outRoot $assetDirectoryName
$manifestRoot = Join-Path $outRoot 'manifests'
$manifestPath = Join-Path $manifestRoot "$PackId.json"
$fileBaseUrl = if ($repositoryMode) { $RepositoryFilesBaseUrl.TrimEnd('/') } else { $ReleaseBaseUrl.TrimEnd('/') }

function ConvertTo-GitHubReleaseAssetName([string]$Name) {
    # GitHub normalizes some release-asset filenames (diacritics, spaces,
    # parentheses, and apostrophes). Predict that normalization so manifest
    # URLs match the names GitHub actually serves.
    $normalized = $Name.Normalize([Text.NormalizationForm]::FormD)
    $normalized = [regex]::Replace($normalized, '\p{Mn}', '')
    $normalized = [regex]::Replace($normalized, "[\s()']+", '.')
    return [regex]::Replace($normalized, '\.{2,}', '.')
}

if (Test-Path -LiteralPath $outRoot) {
    $existingOutput = Get-ChildItem -LiteralPath $outRoot -Force | Select-Object -First 1
    if ($null -ne $existingOutput) {
        throw "OutputDirectory must be empty. Use a new versioned directory to avoid replacing or mixing release assets: $outRoot"
    }
}

$fileBaseUri = $null
if (-not [System.Uri]::TryCreate($fileBaseUrl, [System.UriKind]::Absolute, [ref]$fileBaseUri) -or
    $fileBaseUri.Scheme -ne [System.Uri]::UriSchemeHttps) {
    throw 'The file base URL must be an absolute HTTPS URL.'
}
if ($repositoryMode -and $fileBaseUri.Host -ne 'raw.githubusercontent.com') {
    throw 'RepositoryFilesBaseUrl must point to raw.githubusercontent.com.'
}

$managedRoots = @('mods', 'config', 'defaultconfigs', 'resourcepacks', 'shaderpacks', 'kubejs', 'scripts')
$files = [System.Collections.Generic.List[object]]::new()

New-Item -ItemType Directory -Force -Path $assetRoot, $manifestRoot | Out-Null

foreach ($rootName in $managedRoots) {
    $sourceRoot = Join-Path $packRoot $rootName
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { continue }

    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName) {
        $relative = [System.IO.Path]::GetRelativePath($packRoot, $sourceFile.FullName).Replace('\', '/')
        $fileHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($repositoryMode -and $sourceFile.Length -ge 100MB) {
            throw "GitHub blocks regular repository files at 100 MiB; use Release mode or another file host for '$relative'."
        }
        if ($repositoryMode -and $sourceFile.Length -gt 50MB) {
            Write-Warning "'$relative' exceeds GitHub's 50 MiB warning threshold. Consider hosting this file as a Release asset."
        }

        if ($repositoryMode) {
            # Content-addressed names are immutable: a changed mod gets a new URL,
            # while unchanged files can be reused across pack versions.
            $assetName = "$fileHash-$($sourceFile.Name)"
        }
        else {
            $pathHash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($relative))
            $prefix = [Convert]::ToHexString($pathHash).Substring(0, 12).ToLowerInvariant()
            $assetName = ConvertTo-GitHubReleaseAssetName "$prefix-$($sourceFile.Name)"
        }

        $assetPath = Join-Path $assetRoot $assetName
        if (-not (Test-Path -LiteralPath $assetPath)) {
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $assetPath
        }
        $downloadUrl = "$fileBaseUrl/$([System.Uri]::EscapeDataString($assetName))"
        $files.Add([ordered]@{
            path = $relative
            url = $downloadUrl
            size = [long]$sourceFile.Length
            sha256 = $fileHash
        })
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    packId = $PackId
    version = $Version
    minecraftVersion = $MinecraftVersion
    fabricLoaderVersion = $FabricLoaderVersion
    files = @($files)
}
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Manifest: $manifestPath"
Write-Host "Payload directory: $assetRoot"
Write-Host "Managed files: $($files.Count)"
