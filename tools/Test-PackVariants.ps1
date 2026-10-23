[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/variant-system/publisher-tests')
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path "$root/definitions","$root/content/extras/lite/mods","$root/content/overrides/lite/config" | Out-Null
$checks=[Collections.Generic.List[string]]::new()
function Assert($Value,[string]$Name){if(-not $Value){throw $Name};$checks.Add($Name)}
function Json($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))}
function Entry($Path,$Text,$Id){@{path=$Path;url="https://fixture.invalid/$Path";size=[Text.Encoding]::UTF8.GetByteCount($Text);sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)));modId=$Id}}
$base=@{schemaVersion=1;packId='destiny';version='1.4.8';minecraftVersion='1.20.1';fabricLoaderVersion='0.16.9';javaVersion=21;files=@((Entry 'mods/common.jar' 'common' 'common'),(Entry 'mods/visual-1.0.jar' 'visual' 'visual'),(Entry 'config/quality.txt' 'high' $null))}
Json "$root/base.json" $base
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('release')))
function Release($Tag){@{draft=$false;prerelease=$false;tag_name=$Tag;assets=@(@{name="custom-$Tag.jar";size=7;digest="sha256:$hash";browser_download_url="https://github.com/Fixture/Custom/releases/download/$Tag/custom-$Tag.jar"})}}
Json "$root/releases.json" @{'Fixture/Custom@v1'=(Release 'v1');'Fixture/Custom@latest'=(Release 'v2')}
$fixed=@{path='mods/pinned.jar';github=@{repository='Fixture/Custom';release='v1';asset='custom-*.jar'}}
$latest=@{path='mods/live.jar';github=@{repository='Fixture/Custom';release='latest';asset='custom-*.jar'}}
$normal=@{id='destiny';include=@($fixed,$latest)}
$lite=@{id='destiny-lite';extends='destiny';exclude=@('mods/visual-*.jar');include=@('extras/lite/mods/performance.jar');overrides='overrides/lite'}
$staff=@{id='destiny-staff';extends='destiny';fabricLoaderVersion='0.16.14';include=@((Entry 'mods/staff.jar' 'staff' 'staff'))}
function Write-Definitions {Json "$root/definitions/destiny.json" $normal;Json "$root/definitions/destiny-lite.json" $lite;Json "$root/definitions/destiny-staff.json" $staff}
Write-Definitions
$zip=[IO.Compression.ZipFile]::Open("$root/content/extras/lite/mods/performance.jar",[IO.Compression.ZipArchiveMode]::Create)
try{$writer=[IO.StreamWriter]::new($zip.CreateEntry('fabric.mod.json').Open());try{$writer.Write('{"id":"performance","version":"1.0.0"}')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
[IO.File]::WriteAllText("$root/content/overrides/lite/config/quality.txt",'low')
$generator=Join-Path $PSScriptRoot 'New-PackVariants.ps1'
$parameters=@{BaseManifestPath="$root/base.json";DefinitionsDirectory="$root/definitions";ContentDirectory="$root/content";ReleaseBaseUrl='https://github.com/Fixture/Pack/releases/download/v1';ReleaseMetadataPath="$root/releases.json"}
function Generate([string]$Name){& $generator @parameters -OutputDirectory "$root/$Name"}
function Reject([string]$Name){try{Generate $Name}catch{$checks.Add("Rejected: $Name");return};throw "Expected rejection: $Name"}
Generate 'valid'
$a=Get-Content "$root/valid/manifests/destiny.json" -Raw|ConvertFrom-Json
$b=Get-Content "$root/valid/manifests/destiny-lite.json" -Raw|ConvertFrom-Json
$c=Get-Content "$root/valid/manifests/destiny-staff.json" -Raw|ConvertFrom-Json
Assert ($a.files.Count -eq 5 -and $b.files.Count -eq 5 -and $c.files.Count -eq 6) 'Inheritance composes normal, Lite and Staff from one base'
Assert (-not ($b.files.path -contains 'mods/visual-1.0.jar') -and $b.files.path -contains 'mods/performance.jar') 'Lite combines wildcard exclusions and exclusive included mods'
Assert (($b.files|Where-Object path -eq 'config/quality.txt').sha256 -ne ($a.files|Where-Object path -eq 'config/quality.txt').sha256) 'Variant-specific configuration overrides replace only that variant'
Assert (($c.files|Where-Object path -eq 'mods/common.jar').url -eq ($a.files|Where-Object path -eq 'mods/common.jar').url) 'Shared base files reuse one URL across variants'
Assert ($a.fabricLoaderVersion -eq '0.16.9' -and $b.fabricLoaderVersion -eq '0.16.9' -and $c.fabricLoaderVersion -eq '0.16.14') 'A variant can require a newer Fabric Loader without changing other variants'
$staff.fabricLoaderVersion='bad';Write-Definitions;Reject 'invalid-loader-version';$staff.fabricLoaderVersion='0.16.14';Write-Definitions
$pin=$a.files|Where-Object path -eq 'mods/pinned.jar';$live=$a.files|Where-Object path -eq 'mods/live.jar'
Assert ($pin.url -like '*/v1/*' -and -not $pin.github -and $pin.sha256 -eq $hash) 'Pinned releases compile into fixed, checksummed asset URLs'
Assert ($a.schemaVersion -eq 2 -and $live.github.release -eq 'latest' -and $live.url -like '*/v2/*') 'Latest keeps its runtime policy alongside a verifiable publishing snapshot'
Assert ((Get-ChildItem "$root/valid/release-assets" -File).Count -eq 2) 'Only exclusive local files are uploaded; inherited and external assets are not duplicated'
$calendarRepo="$root/calendar-repo"
New-Item -ItemType Directory -Force -Path "$calendarRepo/base","$calendarRepo/variants"|Out-Null
Copy-Item "$root/base.json" "$calendarRepo/base/manifest.json"
Copy-Item "$root/definitions/*.json" "$calendarRepo/variants"
foreach($calendarVersion in @('2026.0.1-alpha','2026.0.1-beta','2026.0.1')){
    & (Join-Path $PSScriptRoot 'Build-PackVariants.ps1') -RepositoryRoot $calendarRepo -Version $calendarVersion -OutputDirectory "$root/calendar-$calendarVersion" -ContentDirectory "$root/content" -ReleaseMetadataPath "$root/releases.json"
    $calendarManifests=@(Get-ChildItem "$root/calendar-$calendarVersion/manifests" -File|ForEach-Object {Get-Content $_.FullName -Raw|ConvertFrom-Json})
    Assert ($calendarManifests.Count -eq 3 -and @($calendarManifests|Where-Object version -ne $calendarVersion).Count -eq 0) "All variants publish with calendar version $calendarVersion"
}
$staff.enabled=$false;Write-Definitions;Generate 'disabled'
Assert (-not (Test-Path "$root/disabled/manifests/destiny-staff.json")) 'Disabled Staff is not published as a fake working pack'
$staff.Remove('enabled');$normal.extends='destiny-staff';Write-Definitions;Reject 'cycle';$normal.Remove('extends')
$lite.include=@(@{path='mods/common.jar';sourceFile='extras/lite/mods/performance.jar'});Write-Definitions;Reject 'collision'
$lite.include=@(@{path='mods/new.jar';sourceFile='../outside.jar'});Write-Definitions;Reject 'traversal'
$lite.include=@((Entry 'mods/duplicate.jar' 'duplicate' 'common'));Write-Definitions;Reject 'duplicate-mod-id'
$lite.include=@((@{path='mods/dependent.jar';url='https://fixture.invalid/dependent.jar';size=1;sha256=('0'*64);requires=@('missing')}));Write-Definitions;Reject 'missing-declared-dependency'
$lite.include=@('extras/lite/mods/performance.jar');$lite.exclude=@('mod:visual');Write-Definitions;Generate 'mod-id-exclusion'
Assert (-not ((Get-Content "$root/mod-id-exclusion/manifests/destiny-lite.json" -Raw|ConvertFrom-Json).files.path -contains 'mods/visual-1.0.jar')) 'Stable mod ID exclusions are supported when metadata is declared'
Json "$root/releases.json" @{'Fixture/Custom@v1'=(Release 'v1');'Fixture/Custom@latest'=@{draft=$false;prerelease=$false;assets=@()}}
Reject 'missing-release-asset'
Json "$root/releases.json" @{'Fixture/Custom@v1'=(Release 'v1');'Fixture/Custom@latest'=(Release 'v2');'Fixture/Custom@latest/custom'=(Release 'v3')}
$latest.github.channel='custom';Write-Definitions;Generate 'public-channel'
$channel=(Get-Content "$root/public-channel/manifests/destiny.json" -Raw|ConvertFrom-Json).files|Where-Object path -eq 'mods/live.jar'
Assert ($channel.github.channel -eq 'custom' -and $channel.url -like '*/v3/*') 'Public channels distinguish latest releases of mods hosted in one repository'
$broken=Release 'v3';$broken.assets[0].browser_download_url=$broken.assets[0].browser_download_url.Replace('/v3/','/untagged-draft/')
Json "$root/releases.json" @{'Fixture/Custom@v1'=(Release 'v1');'Fixture/Custom@latest/custom'=$broken}
Reject 'stale-draft-asset-url'
Json "$root/results.json" @{passed=$checks.Count;checks=@($checks)}
Write-Host "PASS: $($checks.Count) variant publisher assertions; $root"
