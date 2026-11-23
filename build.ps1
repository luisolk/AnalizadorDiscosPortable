$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

# Stop any running instance of AnalizadorDiscosPortable before overwriting
Get-Process AnalizadorDiscosPortable -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 1. Generate a custom Treemap Icon (app.ico)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(64, 64)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(15, 23, 42))

$bBlue   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(59, 130, 246))
$bPurple = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(139, 92, 246))
$bAmber  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 158, 11))
$bGreen  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(16, 185, 129))
$bRose   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(236, 72, 153))

$g.FillRectangle($bBlue,   4,  4, 34, 34)
$g.FillRectangle($bPurple, 40, 4, 20, 22)
$g.FillRectangle($bAmber,  40, 28, 20, 10)
$g.FillRectangle($bGreen,  4,  40, 22, 20)
$g.FillRectangle($bRose,   28, 40, 32, 20)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 1.5)
$g.DrawRectangle($pen, 3, 3, 57, 57)

$g.Dispose()

$msPng = New-Object System.IO.MemoryStream
$bmp.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $msPng.ToArray()
$msPng.Dispose()
$bmp.Dispose()

$icoPath = Join-Path $projectDir "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)      # Reserved
$bw.Write([UInt16]1)      # Type 1 = ICO
$bw.Write([UInt16]1)      # 1 image
$bw.Write([Byte]64)       # Width
$bw.Write([Byte]64)       # Height
$bw.Write([Byte]0)        # Palette
$bw.Write([Byte]0)        # Reserved
$bw.Write([UInt16]1)      # Color planes
$bw.Write([UInt16]32)     # Bits per pixel
$bw.Write([UInt32]$pngBytes.Length)
$bw.Write([UInt32]22)     # Offset to PNG data
$bw.Write($pngBytes)
$bw.Close()
$fs.Close()

# 2. Compile Single-File Portable Executable with Administrator Manifest
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outExe = Join-Path $projectDir "AnalizadorDiscosPortable.exe"
$manifestPath = Join-Path $projectDir "app.manifest"

Write-Host "Compilando AnalizadorDiscosPortable.exe (con permisos de Administrador integrados)..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/win32icon:$icoPath" `
    "/win32manifest:$manifestPath" `
    "/out:$outExe" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    "$projectDir\src\Models.cs" `
    "$projectDir\src\SafetyAdvisor.cs" `
    "$projectDir\src\NativeScanner.cs" `
    "$projectDir\src\InstallScanner.cs" `
    "$projectDir\src\DuplicateScanner.cs" `
    "$projectDir\src\FileDeleter.cs" `
    "$projectDir\src\TreemapControl.cs" `
    "$projectDir\src\MainForm.cs" `
    "$projectDir\src\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Error de compilacion con codigo $LASTEXITCODE"
}

Write-Host "Compilacion exitosa: $outExe" -ForegroundColor Green
Get-Item $outExe | Select-Object Name, Length, LastWriteTime
