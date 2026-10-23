[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$ModId,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+[0-9A-Za-z.+_-]*$')][string]$Version,
    [Parameter(Mandatory)][string]$JarPath
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepositoryRoot).Path)
$jar=Get-Item -LiteralPath $JarPath
$repository='Destiny-Dynasty/Mod-Releases'
if ($jar.Extension -ne '.jar' -or $jar.Name -match '-(sources|javadoc)\.jar$') { throw 'Only a compiled mod JAR may be published.' }
$zip=[IO.Compression.ZipFile]::OpenRead($jar.FullName)
try {
    if (-not $zip.GetEntry('fabric.mod.json') -or @($zip.Entries|Where-Object {$_.FullName.EndsWith('.class')}).Count -eq 0) { throw 'The JAR must contain Fabric metadata and compiled classes; never upload a source archive.' }
} finally { $zip.Dispose() }
if ((& git -C $repo remote get-url origin) -notmatch 'github\.com[:/]Destiny-Dynasty/Mod-Releases(\.git)?$' -or
    (& git -C $repo branch --show-current).Trim() -ne 'main' -or (& git -C $repo status --porcelain)) { throw 'Use a clean main checkout of Destiny-Dynasty/Mod-Releases.' }
& git -C $repo fetch origin main
if ($LASTEXITCODE -or (& git -C $repo rev-parse HEAD) -ne (& git -C $repo rev-parse origin/main)) { throw 'Synchronize main before publishing a mod.' }
$tag="mod-$ModId-$Version"
$assetName="$ModId-$($Version.Replace('+','-')).jar"
$hash=(Get-FileHash -LiteralPath $jar.FullName).Hash.ToLowerInvariant()
$existing = & gh release view $tag --repo $repository --json apiUrl 2>$null
if ($LASTEXITCODE) {
    & gh release create $tag --repo $repository --target main --draft --title "$ModId $Version" --notes "JAR compilado de $ModId $Version. Código fuente mantenido en su repositorio privado."
    if ($LASTEXITCODE) { throw 'Could not create distribution release.' }
    $existing = & gh release view $tag --repo $repository --json apiUrl
}
$releaseInfo = $existing | ConvertFrom-Json
$release = & gh api $releaseInfo.apiUrl | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Could not inspect release draft.' }
$asset=@($release.assets | Where-Object {$_.name -eq $assetName -and $_.digest -eq "sha256:$hash" -and $_.size -eq $jar.Length})
if ($release.assets.Count -gt 1 -or ($release.assets.Count -eq 1 -and $asset.Count -ne 1)) { throw 'Existing release has different assets. Use a new version; do not overwrite published JARs.' }
if ($asset.Count -eq 0) {
    if (-not $release.draft) { throw 'Published release is incomplete; use a new version.' }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('destiny-mod-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $upload = Join-Path $temporary $assetName
    try {
        Copy-Item -LiteralPath $jar.FullName -Destination $upload
        & gh release upload $tag $upload --repo $repository
        if ($LASTEXITCODE) { throw 'Could not upload compiled mod.' }
    } finally {
        if (Test-Path -LiteralPath $upload) { Remove-Item -LiteralPath $upload }
        Remove-Item -LiteralPath $temporary
    }
    $release = & gh api $releaseInfo.apiUrl | ConvertFrom-Json
    $asset=@($release.assets | Where-Object {$_.name -eq $assetName -and $_.digest -eq "sha256:$hash" -and $_.size -eq $jar.Length})
    if ($asset.Count -ne 1) { throw 'GitHub did not confirm the compiled JAR checksum.' }
}
if ($release.draft) {
    & gh release edit $tag --repo $repository --draft=false --prerelease=false
    if ($LASTEXITCODE) { throw 'Could not publish mod release. The channel remains unchanged.' }
}
# Draft asset URLs may contain GitHub's temporary untagged-* name. Resolve the
# final public URL after publication, before exposing the channel to launchers.
$release = & gh api $releaseInfo.apiUrl | ConvertFrom-Json
if ($LASTEXITCODE -or $release.draft) { throw 'Could not verify the public mod release.' }
$asset=@($release.assets | Where-Object {$_.name -eq $assetName -and $_.digest -eq "sha256:$hash" -and $_.size -eq $jar.Length})
if ($asset.Count -ne 1 -or $asset[0].browser_download_url -notlike "https://github.com/$repository/releases/download/$tag/*") { throw 'Public release asset verification failed.' }
# Idempotent API updates coexist safely with the release-published workflow.
& (Join-Path $PSScriptRoot 'Sync-ModChannel.ps1') -ReleaseTag $tag -Repository $repository
& git -C $repo pull --ff-only origin main
if ($LASTEXITCODE) { throw 'Channel published; refresh the local checkout before publishing another mod.' }
Write-Host "Published only compiled JAR: https://github.com/$repository/releases/tag/$tag"
