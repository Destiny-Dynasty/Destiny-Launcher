[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$SigningKeyPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Usa una versión estable X.Y.Z.' }
$setup = Get-Item -LiteralPath $SetupPath
if (([version]$setup.VersionInfo.ProductVersion).ToString(3) -ne $Version) { throw 'La versión del instalador no coincide.' }
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$manifest = [ordered]@{
    schema=1; version=$Version; platform='win-x64'
    installerUrl="https://github.com/Destiny-Dynasty/Destiny-Launcher/releases/download/v$Version/DestinyLauncherSetup.exe"
    size=$setup.Length; sha256=(Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$bytes=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($SigningKeyPath)))
    $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    $publicFile=Join-Path (Split-Path -Parent $PSScriptRoot) 'DestinyLauncher\Assets\Signing\launcher-update-public.pem'
    $public=[Security.Cryptography.RSA]::Create()
    try {
        $public.ImportFromPem([IO.File]::ReadAllText($publicFile))
        if(-not $public.VerifyData($bytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw 'La clave privada no corresponde a la clave pública del launcher.'}
    } finally { $public.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $OutputPath 'launcher-update.json'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $OutputPath 'launcher-update.sig'),$signature)
} finally { $rsa.Dispose() }
Write-Output "Manifiesto de actualización firmado: $Version"
