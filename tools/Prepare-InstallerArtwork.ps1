[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackgroundPath, [Parameter(Mandatory)][string]$OutputPath, [string]$LogoPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = [IO.Path]::GetFullPath($BackgroundPath)
$targetPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
$sourceImage = [Drawing.Image]::FromFile($sourcePath)
$canvasImage = [Drawing.Bitmap]::new(1680,1040)
$graphics = [Drawing.Graphics]::FromImage($canvasImage)
try {
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    # Fill without stretching the scene. The original screenshot is never modified.
    $ratio = [Math]::Max(1680.0 / $sourceImage.Width,1040.0 / $sourceImage.Height)
    $width = [single]($sourceImage.Width * $ratio); $height = [single]($sourceImage.Height * $ratio)
    $graphics.DrawImage($sourceImage,[single]((1680-$width)/2),[single]((1040-$height)/2),$width,$height)
    $veil = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,0),[Drawing.Point]::new(0,1040),[Drawing.Color]::FromArgb(70,16,16,15),[Drawing.Color]::FromArgb(245,16,16,15))
    try { $graphics.FillRectangle($veil,0,0,1680,1040) } finally { $veil.Dispose() }
    $canvasImage.Save($targetPath,[Drawing.Imaging.ImageFormat]::Png)
} finally { $graphics.Dispose(); $canvasImage.Dispose(); $sourceImage.Dispose() }
if ($LogoPath) {
    $sourceLogo = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($LogoPath))
    $smallLogo = [Drawing.Bitmap]::new(520,302,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $logoGraphics = [Drawing.Graphics]::FromImage($smallLogo)
    try {
        $logoGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $logoGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $logoRatio = [Math]::Min(520.0 / $sourceLogo.Width,302.0 / $sourceLogo.Height)
        $logoWidth = [single]($sourceLogo.Width * $logoRatio)
        $logoHeight = [single]($sourceLogo.Height * $logoRatio)
        $logoGraphics.DrawImage($sourceLogo,[single]((520-$logoWidth)/2),[single]((302-$logoHeight)/2),$logoWidth,$logoHeight)
        $smallLogo.Save((Join-Path (Split-Path -Parent $targetPath) 'setup-logo.png'),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $logoGraphics.Dispose(); $smallLogo.Dispose(); $sourceLogo.Dispose() }
}
# Title-bar controls are transparent glyphs, drawn at 2x for high-DPI displays.
foreach ($controlName in @('close','minimize')) {
    $controlImage = [Drawing.Bitmap]::new(88,76,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $controlGraphics = [Drawing.Graphics]::FromImage($controlImage)
    $controlPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255,179,172,158),2.5)
    try {
        $controlGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $controlPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $controlPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        if ($controlName -eq 'close') {
            $controlGraphics.DrawLine($controlPen,28,22,60,54)
            $controlGraphics.DrawLine($controlPen,60,22,28,54)
        } else {
            $controlGraphics.DrawLine($controlPen,32,38,56,38)
        }
        $controlImage.Save((Join-Path (Split-Path -Parent $targetPath) "setup-$controlName.png"),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $controlPen.Dispose(); $controlGraphics.Dispose(); $controlImage.Dispose() }
}
Write-Output $targetPath
