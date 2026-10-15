# Builds the mod's main homepage image: mask logo and name on the left, the Logbook
# screenshot on the right, tagline footer in the same colours as annotate.ps1.
# Output: images/homepage/rabotnik_main.png. Run: powershell -File images\hero.ps1
. (Join-Path $PSScriptRoot 'common.ps1')

$root     = $PSScriptRoot
$outDir   = Join-Path $root 'homepage'
$logo     = [MaskKey]::KeyOutGrey((Join-Path $root 'rabotnik_mask.png'))
$shot     = [System.Drawing.Image]::FromFile((Join-Path $root 'documentation\rabotnik_logbook.png'))
$name     = 'RABOTNIK'
$subtitle = 'A mod for Eco'
$tagline  = 'Hire citizens who work, eat, vote and pay rent'

$w = 1600; $h = 900
$footerH  = 90
$contentH = $h - $footerH
$leftW    = 660    # width of the logo/name column

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = 'AntiAlias'
$g.InterpolationMode = 'HighQualityBicubic'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear($footerColor)

# Logbook screenshot, cropped in around the book and brightened, fills the right of the
# content area, then fades into the dark left column.
$shotX = 600
$destW = $w - $shotX
$srcH  = 640
$srcW  = [int]($srcH * $destW / $contentH)
$src   = New-Object System.Drawing.Rectangle ([int](515 - $srcW / 2)), 60, $srcW, $srcH   # book is near (515, 380)
$gain  = [single]1.3
$cm = New-Object System.Drawing.Imaging.ColorMatrix
$cm.Matrix00 = $gain; $cm.Matrix11 = $gain; $cm.Matrix22 = $gain
$attrs = New-Object System.Drawing.Imaging.ImageAttributes
$attrs.SetColorMatrix($cm)
$g.DrawImage($shot, (New-Object System.Drawing.Rectangle $shotX, 0, $destW, $contentH), $src.X, $src.Y, $src.Width, $src.Height, 'Pixel', $attrs)
$fadeW = 260
$fadeRect = New-Object System.Drawing.RectangleF ($shotX - 2), 0, ($fadeW + 4), $contentH
$fade = New-Object System.Drawing.Drawing2D.LinearGradientBrush $fadeRect, $footerColor, ([System.Drawing.Color]::FromArgb(0, $footerColor)), ([single]0)
$fade.WrapMode = 'TileFlipX'   # stops the gradient wrapping a sliver of the far colour onto the edges
$g.FillRectangle($fade, $shotX, 0, $fadeW, $contentH)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $footerColor), 0, 0, ($shotX + 3), $contentH)   # hides the antialiased seam

# Soft green glow behind the logo.
$logoSize = 300
$logoX = ($leftW - $logoSize) / 2
$logoY = 110
$glowR = 260
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(($leftW / 2 - $glowR), ($logoY + $logoSize / 2 - $glowR), 2 * $glowR, 2 * $glowR)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterColor    = [System.Drawing.Color]::FromArgb(70, $edgeColor)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $edgeColor))
$g.FillPath($glow, $glowPath)
$g.DrawImage($logo, [single]$logoX, [single]$logoY, [single]$logoSize, [single]$logoSize)

# Name, accent rule and subtitle, centred in the left column.
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = 'Center'
$nameFont = New-Object System.Drawing.Font 'Segoe UI Black', ([single]92), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$nameY = $logoY + $logoSize + 30
$g.DrawString($name, $nameFont, (New-Object System.Drawing.SolidBrush $textColor), (New-Object System.Drawing.RectangleF 0, $nameY, $leftW, 140), $fmt)
$ruleY = $nameY + 145
$g.FillRectangle((New-Object System.Drawing.SolidBrush $edgeColor), ($leftW / 2 - 90), $ruleY, 180, 4)
$subFont = New-Object System.Drawing.Font 'Segoe UI Semibold', ([single]30), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString($subtitle, $subFont, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 200, 200))), (New-Object System.Drawing.RectangleF 0, ($ruleY + 18), $leftW, 50), $fmt)

# Footer: accent line and tagline, as on the annotated screenshots.
$g.FillRectangle((New-Object System.Drawing.SolidBrush $edgeColor), 0, $contentH, $w, 3)
$tagFont = New-Object System.Drawing.Font 'Segoe UI Semibold', ([single]34), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$fmt.LineAlignment = 'Center'
$g.DrawString($tagline, $tagFont, (New-Object System.Drawing.SolidBrush $textColor), (New-Object System.Drawing.RectangleF 0, $contentH, $w, $footerH), $fmt)

New-Item -ItemType Directory -Force $outDir | Out-Null
$out = Join-Path $outDir 'rabotnik_main.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $shot.Dispose(); $logo.Dispose()
Write-Output "Wrote $out"
