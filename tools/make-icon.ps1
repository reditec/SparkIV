# Builds src\SparkIV.ico from the logo assets\logo\sparkiv-icon.svg: renders it with Microsoft Edge (headless) at
# 256 px on a transparent background, scales it down to the sizes Windows uses (16 to 256 px) and packs them as PNG
# images into the .ico. Also writes assets\logo\sparkiv-icon-256.png.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$svg = Join-Path $root 'assets\logo\sparkiv-icon.svg'
$png256 = Join-Path $root 'assets\logo\sparkiv-icon-256.png'
$ico = Join-Path $root 'src\SparkIV.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sparkiv-icon-' + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
  $page = Join-Path $tmp 'icon.html'
  $uri = ([Uri]$svg).AbsoluteUri
  Set-Content $page "<!doctype html><html><body style=`"margin:0;background:transparent`"><img src=`"$uri`" width=`"256`" height=`"256`" style=`"display:block`"></body></html>" -Encoding UTF8
  & $edge --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --window-size=256,256 `
    "--screenshot=$png256" ([Uri]$page).AbsoluteUri 2>$null
  for ($i = 0; $i -lt 50 -and -not (Test-Path $png256); $i++) { Start-Sleep -Milliseconds 200 }
  Start-Sleep -Milliseconds 500

  Add-Type -AssemblyName System.Drawing
  $source = [System.Drawing.Image]::FromFile($png256)
  $images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $graphics.Dispose()
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    ,$stream.ToArray()
  }
  $source.Dispose()

  # ICONDIR, one ICONDIRENTRY per size, then the PNG data.
  $out = New-Object IO.MemoryStream
  $writer = New-Object IO.BinaryWriter $out
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
  }
  foreach ($image in $images) { $writer.Write($image) }
  $writer.Flush()
  [IO.File]::WriteAllBytes($ico, $out.ToArray())
  "Wrote $ico ($($sizes -join ', ') px) and $png256"
}
finally {
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
