[CmdletBinding()]
param([string]$Directory)
$ErrorActionPreference='Stop'
if(-not $Directory){$Directory=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\tooling\InnoSetup'}
$Directory=[IO.Path]::GetFullPath($Directory)
if(Test-Path -LiteralPath (Join-Path $Directory 'ISCC.exe')){return (Join-Path $Directory 'ISCC.exe')}
$setup=Join-Path ([IO.Path]::GetDirectoryName($Directory)) 'innosetup-6.5.4.exe'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($setup)) -Force|Out-Null
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_5_4/innosetup-6.5.4.exe' -OutFile $setup
if((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne 'FA73BF47A4DA250D185D07561C2BFDA387E5E20DB77E4570004CF6A133CC10B1'){throw 'El compilador no coincide con el hash oficial fijado.'}
if((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid'){throw 'Firma del compilador inválida.'}
$install=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/NOICONS',('"/DIR='+$Directory+'"')) -WindowStyle Hidden -PassThru -Wait
if($install.ExitCode -ne 0){throw "No se pudo preparar Inno Setup: $($install.ExitCode)"}
return (Join-Path $Directory 'ISCC.exe')
