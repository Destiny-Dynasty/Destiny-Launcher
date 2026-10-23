[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseTag,
    [string]$Repository = 'Destiny-Dynasty/Mod-Releases'
)
$ErrorActionPreference='Stop'
if($Repository -ne 'Destiny-Dynasty/Mod-Releases' -or $ReleaseTag -notmatch '^mod-(?<id>[a-z0-9][a-z0-9-]*)-(?<version>\d+\.\d+\.\d+[0-9A-Za-z.+_-]*)$'){throw 'Use a Mod-Releases tag such as mod-destiny-realms-0.1.1.'}
$modId=$Matches.id
$release=& gh api "repos/$Repository/releases/tags/$ReleaseTag"|ConvertFrom-Json
if($LASTEXITCODE){throw 'Could not read published release.'}
if($release.draft -or $release.prerelease){Write-Output 'Draft/pre-release: stable channel unchanged.';return}
$jars=@($release.assets|Where-Object {$_.name -like "$modId-*.jar" -and $_.name -notmatch '-(sources|javadoc|dev)\.jar$'})
if($jars.Count -ne 1){throw 'A mod release must contain exactly one matching production JAR.'}
$asset=$jars[0]
$prefix="https://github.com/$Repository/releases/download/$([uri]::EscapeDataString($ReleaseTag))/"
if($asset.digest -notmatch '^sha256:[a-f0-9]{64}$' -or $asset.size -le 0 -or -not $asset.browser_download_url.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Missing or invalid URL, SHA-256 or size.'}
$channel=[ordered]@{draft=$false;prerelease=$false;tag_name=$release.tag_name;published_at=$release.published_at;assets=@([ordered]@{
    name=$asset.name;size=[long]$asset.size;digest=$asset.digest;browser_download_url=$asset.browser_download_url
})}
$content=($channel|ConvertTo-Json -Depth 8).Replace("`r`n","`n")+"`n"
$encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($content))
$endpoint="repos/$Repository/contents/channels/$modId.json"
for($attempt=1;$attempt -le 3;$attempt++){
    $existingText=& gh api "$($endpoint)?ref=main" 2>$null
    $readCode=$LASTEXITCODE
    $existing=$existingText|ConvertFrom-Json
    if($readCode -ne 0 -and $existing.status -ne '404'){throw 'Cannot read channel; no write attempted.'}
    if($readCode -eq 0 -and ($existing.content -replace '\s','') -eq $encoded){Write-Output "Channel already current: $modId";return}
    $payload=@{message="mod: $modId channel -> $ReleaseTag";content=$encoded;branch='main'}
    if($readCode -eq 0){$payload.sha=$existing.sha}
    $resultText=$payload|ConvertTo-Json -Compress|& gh api --method PUT $endpoint --input - 2>$null
    if($LASTEXITCODE -eq 0){Write-Output "Channel synchronized: $modId";return}
    $result=$resultText|ConvertFrom-Json
    if($result.status -ne '409' -or $attempt -eq 3){throw 'Channel write failed; published JAR retained for retry.'}
}
