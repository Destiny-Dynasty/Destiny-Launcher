[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$PreparedDirectory
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepositoryRoot).Path)
$prepared = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PreparedDirectory).Path)
$build = Get-Content -LiteralPath "$prepared/build.json" -Raw | ConvertFrom-Json
if (@($build.variants).Count -eq 0) { throw 'Prepared output contains no variants.' }
if ($build.repository -ne 'Destiny-Dynasty/Modpack' -or $build.tag -ne "modpack-$($build.version)") { throw 'Prepared output does not target the official modpack repository.' }
if ((& git -C $repo branch --show-current).Trim() -ne 'main' -or (& git -C $repo status --porcelain)) { throw 'Publish from a clean main checkout.' }
$remote = (& git -C $repo remote get-url origin).Trim()
if ($remote -notmatch '(github\.com[:/])Destiny-Dynasty/Modpack(\.git)?$') { throw 'Checkout origin is not the official modpack repository.' }
& git -C $repo fetch origin main
if ($LASTEXITCODE -ne 0 -or (& git -C $repo rev-parse HEAD) -ne (& git -C $repo rev-parse origin/main)) { throw 'Synchronize main before publishing.' }
$manifests = @()
foreach ($id in $build.variants) {
    if ($id -notin @('destiny','destiny-lite','destiny-staff')) { throw 'Invalid variant in prepared output.' }
    $file = "$prepared/manifests/$id.json"
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($manifest.packId -ne $id -or $manifest.version -ne $build.version -or $manifest.files.Count -eq 0) { throw "Invalid prepared variant: $id" }
    $manifests += $file
}
foreach ($asset in $build.assets) {
    if ($asset.name -ne [IO.Path]::GetFileName($asset.name)) { throw 'Unsafe asset name.' }
    $file = Get-Item -LiteralPath "$prepared/release-assets/$($asset.name)"
    if ($file.Length -ne $asset.size -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $asset.sha256) { throw 'Prepared assets changed after building.' }
}
$existingText = & gh release view $build.tag --repo $build.repository --json isDraft,assets 2>$null
if ($LASTEXITCODE -eq 0) {
    $existing = $existingText | ConvertFrom-Json
    if (-not $existing.isDraft) { throw 'This release is already published; never replace its assets.' }
} else {
    & gh release create $build.tag --repo $build.repository --target main --draft --title "Destiny Modpacks $($build.version)" --notes "Variantes: $($build.variants -join ', ')."
    if ($LASTEXITCODE) { throw 'Could not create release draft.' }
}
foreach ($asset in $build.assets) {
    $previous = @($existing.assets | Where-Object {$_.name -eq $asset.name})
    if ($previous.Count) {
        # Resume an interrupted draft only after checking GitHub's own digest.
        $info = & gh release view $build.tag --repo $build.repository --json apiUrl | ConvertFrom-Json
        $releaseAssets = & gh api $info.apiUrl --jq '.assets' | ConvertFrom-Json
        $match = @($releaseAssets | Where-Object {$_.name -eq $asset.name -and $_.digest -eq "sha256:$($asset.sha256)"})
        if ($match.Count -ne 1) { throw 'An existing draft asset differs; use a new version instead of clobbering it.' }
    } else {
        & gh release upload $build.tag "$prepared/release-assets/$($asset.name)" --repo $build.repository
        if ($LASTEXITCODE) { throw 'Upload interrupted. The release remains a draft; rerun with the same prepared directory.' }
    }
}
$info = & gh release view $build.tag --repo $build.repository --json apiUrl | ConvertFrom-Json
$assets = @(& gh api $info.apiUrl --jq '.assets' | ConvertFrom-Json | Where-Object { $null -ne $_ })
if ($LASTEXITCODE -or @($assets).Count -ne @($build.assets).Count) { throw 'Release asset verification failed.' }
foreach ($asset in $build.assets) {
    if (@($assets | Where-Object {$_.name -eq $asset.name -and $_.size -eq $asset.size -and $_.digest -eq "sha256:$($asset.sha256)"}).Count -ne 1) { throw 'GitHub asset digest mismatch.' }
}
& gh release edit $build.tag --repo $build.repository --draft=false
if ($LASTEXITCODE) { throw 'Could not publish release; manifests remain unchanged.' }
# Commit all variant manifests and the shared base together, after assets are public.
Copy-Item -LiteralPath "$prepared/base-manifest.json" -Destination "$repo/base/manifest.json"
foreach ($file in $manifests) { Copy-Item -LiteralPath $file -Destination "$repo/manifests" }
$user = & gh api user | ConvertFrom-Json
& git -C $repo add -- base/manifest.json manifests
& git -C $repo -c "user.name=$($user.login)" -c "user.email=$($user.id)+$($user.login)@users.noreply.github.com" commit -m "release: all modpack variants $($build.version)"
if ($LASTEXITCODE) { throw 'Assets are published but committing manifests failed. Prepared output was retained.' }
& git -C $repo push origin main
if ($LASTEXITCODE) { throw 'Assets are published and manifests are committed locally. Retry git push origin main; no files were deleted.' }
