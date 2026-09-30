param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $RepositoryRoot 'src\Shnapp.App\Assets'
[System.IO.Directory]::CreateDirectory($assets) | Out-Null
$source = [System.Drawing.Image]::FromFile((Join-Path $RepositoryRoot 'icon.png'))

function Get-ResizedPng {
    param([int]$Width, [int]$Height)
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $ratio = [Math]::Min($Width / $source.Width, $Height / $source.Height)
    $w = [int][Math]::Round($source.Width * $ratio)
    $h = [int][Math]::Round($source.Height * $ratio)
    $graphics.DrawImage($source, [int](($Width - $w) / 2), [int](($Height - $h) / 2), $w, $h)
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = @($sizes | ForEach-Object { Get-ResizedPng $_ $_ })
    $stream = [System.IO.File]::Create((Join-Path $assets 'AppIcon.ico'))
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = [byte]($sizes[$i] % 256)
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image) }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    $outputs = @{
        'ShnappIcon.png' = @(128, 128)
        'Square150x150Logo.scale-200.png' = @(300, 300)
        'Square44x44Logo.scale-200.png' = @(88, 88)
        'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24, 24)
        'Square44x44Logo.targetsize-48_altform-lightunplated.png' = @(48, 48)
        'StoreLogo.png' = @(50, 50)
        'Wide310x150Logo.scale-200.png' = @(620, 300)
        'SplashScreen.scale-200.png' = @(1240, 600)
        'LockScreenLogo.scale-200.png' = @(48, 48)
    }
    foreach ($entry in $outputs.GetEnumerator()) {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $assets $entry.Key),
            (Get-ResizedPng $entry.Value[0] $entry.Value[1]))
    }
    [System.IO.File]::Copy(
        (Join-Path $RepositoryRoot 'logo.png'),
        (Join-Path $assets 'ShnappLogo.png'), $true)
} finally {
    $source.Dispose()
}
