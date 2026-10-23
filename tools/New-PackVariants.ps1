[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseManifestPath,
    [Parameter(Mandatory)][string]$DefinitionsDirectory,
    [Parameter(Mandatory)][string]$ContentDirectory,
    [Parameter(Mandatory)][string]$ReleaseBaseUrl,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version,
    [string]$ReleaseMetadataPath,
    [switch]$IncludeDisabled
)
$ErrorActionPreference = 'Stop'
$base = Get-Content -LiteralPath $BaseManifestPath -Raw | ConvertFrom-Json -AsHashtable
$content = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ContentDirectory).Path)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'OutputDirectory must be empty.' }
if ($ReleaseBaseUrl -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/download/[^?#]+$') { throw 'ReleaseBaseUrl must identify a GitHub release.' }
$roots = @('mods','config','defaultconfigs','resourcepacks','shaderpacks','kubejs','scripts')
$definitions = @{}; $resolved = @{}; $resolving = @{}; $metadata = @{}
if ($ReleaseMetadataPath) { $metadata = Get-Content -LiteralPath $ReleaseMetadataPath -Raw | ConvertFrom-Json -AsHashtable }
foreach ($file in Get-ChildItem -LiteralPath $DefinitionsDirectory -File -Filter '*.json') {
    $definition = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($definition.id -notin @('destiny','destiny-lite','destiny-staff') -or $definitions.ContainsKey($definition.id)) { throw "Invalid or repeated variant: $($definition.id)" }
    $definitions[$definition.id] = $definition
}
New-Item -ItemType Directory -Force -Path "$output/manifests", "$output/release-assets" | Out-Null

function Test-ManagedPath([string]$Path) {
    $parts = $Path.Replace('\','/').Split('/')
    if ($parts.Count -lt 2 -or $parts[0] -notin $roots -or @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -or $Path -match '[:\x00-\x1f*?]') { throw "Unsafe managed path: $Path" }
}
function Get-LocalPath([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $content $Relative))
    if (-not $path.StartsWith($content + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Content path escapes its root: $Relative" }
    for ($current = $path; $current -and $current -ne $content; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Content links are not supported: $Relative" }
    }
    return $path
}
function Get-LocalEntry([string]$Source, [string]$Target) {
    Test-ManagedPath $Target
    $file = Get-Item -LiteralPath (Get-LocalPath $Source)
    if ($file.PSIsContainer) { throw "Expected a file: $Source" }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = $hash + $file.Extension.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath "$output/release-assets/$asset")) { Copy-Item -LiteralPath $file.FullName -Destination "$output/release-assets/$asset" }
    $entry = @{path=$Target;url="$($ReleaseBaseUrl.TrimEnd('/'))/$asset";size=[long]$file.Length;sha256=$hash}
    if ($file.Extension -eq '.jar' -and $Target.StartsWith('mods/')) {
        $zip = $null
        try {
            $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
            $fabric = $zip.GetEntry('fabric.mod.json')
            if ($fabric) {
                $reader = [IO.StreamReader]::new($fabric.Open())
                try { $mod = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable; $entry.modId = $mod.id }
                finally { $reader.Dispose() }
            }
        } catch [IO.InvalidDataException] { throw "Invalid mod JAR: $Source" }
        finally { if ($zip) { $zip.Dispose() } }
    }
    return $entry
}
function Get-GitHubEntry($Entry) {
    $source = $Entry.github
    if ($source.repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or @($source.repository.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -or
        [string]::IsNullOrWhiteSpace($source.release) -or $source.release.Length -gt 200 -or $source.release -match '[\x00-\x1f]' -or
        [string]::IsNullOrWhiteSpace($source.asset) -or $source.asset -match '[/\\:\x00-\x1f]' -or $source.asset.Length -gt 200 -or
        $Entry.path -notmatch '^mods/.+\.jar$') { throw "Invalid GitHub mod source: $($Entry.path)" }
    if ($source.channel -and $source.channel -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') { throw 'Invalid mod channel.' }
    $key = "$($source.repository)@$($source.release)$(if($source.channel){'/' + $source.channel})"
    if (-not $metadata.ContainsKey($key)) {
        if ($ReleaseMetadataPath) { throw "Offline release metadata missing: $key" }
        $selector = if ($source.release -eq 'latest') { 'latest' } else { 'tags/' + [Uri]::EscapeDataString($source.release) }
        $endpoint = if ($source.release -eq 'latest' -and $source.channel) { "https://raw.githubusercontent.com/$($source.repository)/main/channels/$($source.channel).json?check=$([guid]::NewGuid().ToString('N'))" } else { "https://api.github.com/repos/$($source.repository)/releases/$selector" }
        $metadata[$key] = Invoke-RestMethod $endpoint -Headers @{'User-Agent'='Destiny-Pack-Publisher';Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'} -TimeoutSec 30 | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
    }
    $release = $metadata[$key]
    if ($release.draft -or ($source.release -eq 'latest' -and $release.prerelease)) { throw "Release is not published/stable: $key" }
    $pattern = [Management.Automation.WildcardPattern]::new($source.asset, [Management.Automation.WildcardOptions]::IgnoreCase)
    $assets = @($release.assets | Where-Object { $pattern.IsMatch($_.name) -and $_.name -notmatch '-(sources|javadoc)\.jar$' })
    if ($assets.Count -ne 1) { throw "Asset selector must match exactly one asset: $key / $($source.asset)" }
    $asset = $assets[0]
    if ([string]::IsNullOrWhiteSpace($release.tag_name) -or ($source.release -ne 'latest' -and $release.tag_name -cne $source.release)) { throw "Release tag does not match the requested version: $key" }
    $prefix = 'https://github.com/' + $source.repository + '/releases/download/' + [Uri]::EscapeDataString($release.tag_name) + '/'
    if ($asset.digest -notmatch '^sha256:[a-fA-F0-9]{64}$' -or [long]$asset.size -le 0 -or -not $asset.browser_download_url.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Release asset lacks a valid URL, size or SHA-256: $key" }
    $Entry.url = $asset.browser_download_url; $Entry.size = [long]$asset.size; $Entry.sha256 = $asset.digest.Substring(7).ToLowerInvariant()
    if ($source.release -ne 'latest') { $Entry.Remove('github') }
    return $Entry
}
function Get-Entry($Value) {
    if ($Value -is [string]) {
        $parts = $Value.Replace('\','/').Split('/')
        if ($parts.Count -lt 4 -or $parts[0] -ne 'extras') { throw "Local includes use extras/<variant>/<managed-root>/file: $Value" }
        return Get-LocalEntry $Value ($parts[2..($parts.Length-1)] -join '/')
    }
    $entry = $Value | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
    if (-not $entry.path) { throw 'A file entry needs a target path.' }
    $entry.path = $entry.path.Replace('\','/')
    Test-ManagedPath $entry.path
    if ($entry.sourceFile) { return Get-LocalEntry $entry.sourceFile $entry.path }
    if ($entry.github) { $entry = Get-GitHubEntry $entry }
    if ($entry.url -notmatch '^https://' -or [long]$entry.size -lt 0 -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "Invalid URL, size or checksum: $($entry.path)" }
    return $entry
}
function Get-Variant([string]$Id) {
    if ($resolved.ContainsKey($Id)) { return $resolved[$Id] }
    if ($resolving.ContainsKey($Id)) { throw "Variant inheritance cycle: $Id" }
    if (-not $definitions.ContainsKey($Id)) { throw "Unknown parent variant: $Id" }
    $resolving[$Id] = $true; $definition = $definitions[$Id]; $files = @{}
    $parent = if ($definition.extends) { (Get-Variant $definition.extends).files } else { $base.files }
    foreach ($file in $parent) {
        Test-ManagedPath $file.path
        if ($files.ContainsKey($file.path)) { throw "Duplicate base target: $($file.path)" }
        $files[$file.path] = $file
    }
    foreach ($exclude in @($definition.exclude)) {
        if (-not $exclude) { continue }
        $matches = @($files.Keys | Where-Object { if ($exclude.StartsWith('mod:')) { $files[$_].modId -eq $exclude.Substring(4) } else { $_ -like $exclude } })
        if ($matches.Count -eq 0) { throw "Exclusion does not match any inherited file ($Id): $exclude" }
        foreach ($path in $matches) { $files.Remove($path) }
    }
    foreach ($include in @($definition.include)) {
        if ($null -eq $include) { continue }
        $entry = Get-Entry $include
        if ($files.ContainsKey($entry.path)) { throw "Include collides with an inherited file; use overrides ($Id): $($entry.path)" }
        $files[$entry.path] = $entry
    }
    if ($definition.overrides -is [string]) {
        $folder = Get-LocalPath $definition.overrides
        foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -File) {
            $target = [IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\','/')
            $source = [IO.Path]::GetRelativePath($content, $file.FullName)
            $files[$target] = Get-LocalEntry $source $target
        }
    } else {
        foreach ($override in @($definition.overrides)) {
            if ($null -eq $override) { continue }
            $entry = Get-Entry $override
            if (-not $files.ContainsKey($entry.path)) { throw "Override target does not exist; use include ($Id): $($entry.path)" }
            $files[$entry.path] = $entry
        }
    }
    $ids = @{}
    foreach ($file in $files.Values) {
        if ($file.modId) {
            if ($ids.ContainsKey($file.modId)) { throw "Duplicate mod ID in ${Id}: $($file.modId)" }
            $ids[$file.modId] = $true
        }
    }
    foreach ($file in $files.Values) {
        foreach ($required in @($file.requires)) {
            if ($required -and -not $ids.ContainsKey($required)) { throw "Missing declared dependency in ${Id}: $required" }
        }
    }
    $result = @{ schemaVersion=1;packId=$Id;version=$(if($Version){$Version}elseif($definition.version){$definition.version}else{$base.version});
        minecraftVersion=$base.minecraftVersion;fabricLoaderVersion=$base.fabricLoaderVersion;javaVersion=$base.javaVersion;
        files=@($files.Values | Sort-Object path) }
    if (@($result.files | Where-Object { $_.github }).Count) { $result.schemaVersion = 2 }
    if ($result.files.Count -eq 0) { throw "Variant contains no files: $Id" }
    $resolved[$Id] = $result; $resolving.Remove($Id)
    return $result
}
foreach ($id in @($definitions.Keys | Sort-Object)) {
    if ($definitions[$id].enabled -eq $false -and -not $IncludeDisabled) { continue }
    $manifest = Get-Variant $id
    [IO.File]::WriteAllText("$output/manifests/$id.json", ($manifest | ConvertTo-Json -Depth 30) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host "$id : $($manifest.files.Count) files; schema $($manifest.schemaVersion); version $($manifest.version)"
}
