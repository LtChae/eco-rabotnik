# Builds the mod homepage images: each screenshot in images/documentation gets a
# 1920x1080 navy-padded canvas, a tagline footer strip and a 45-degree corner ribbon
# carrying the Rabotnik mask.
# Output goes to images/homepage. Run: powershell -File images\annotate.ps1
. (Join-Path $PSScriptRoot 'common.ps1')

$root    = $PSScriptRoot
$srcDir  = Join-Path $root 'documentation'
$outDir  = Join-Path $root 'homepage'
$logo    = [MaskKey]::KeyOutGrey((Join-Path $root 'rabotnik_mask.png'))

# Screenshot file name -> footer tagline. Screenshots not listed get no tagline.
$taglines = [ordered]@{
    'rabotnik_logbook.png'      = 'Hire a Rabotnik: every worker lives in its own Logbook'
    'rabotnik_activity_tab.png' = 'They join work parties and take harvest and hauling contracts'
    'rabotnik_money_tab.png'    = 'They rent a home, earn wages and buy their own food'
    'rabotnik_tab.png'          = 'They eat, learn skills and level up like any citizen'
    'rabotnik_ecopedia.png'          = 'Fully documented in-game: eight Ecopedia pages explain every rule'
    'rabotnik_ecopedia_voting.png'   = 'They vote, follow laws and reward fair players with reputation, all configurable'
    'rabotnik_ecopedia_settings.png' = 'Fully configurable: tune every rule from the server UI'
}

$ribbonText  = 'RABOTNIK'

New-Item -ItemType Directory -Force $outDir | Out-Null

foreach ($file in Get-ChildItem $srcDir -Filter *.png) {
    $shot = [System.Drawing.Image]::FromFile($file.FullName)
    # Every image is the same 16:9 canvas, as mod.io's gallery expects; the screenshot
    # sits centred on navy padding above the footer, shrunk only if it doesn't fit.
    $w = 1920; $h = 1080
    $scale   = 1.0   # fixed so banners are the same pixel size on every screenshot
    $footerH = [int](64 * $scale)
    $areaH   = $h - $footerH
    $margin  = 24
    $fit   = [Math]::Min(1.0, [Math]::Min(($w - 2 * $margin) / $shot.Width, ($areaH - 2 * $margin) / $shot.Height))
    $shotW = [int]($shot.Width * $fit); $shotH = [int]($shot.Height * $fit)

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear($padColor)
    $g.DrawImage($shot, [int](($w - $shotW) / 2), [int](($areaH - $shotH) / 2), $shotW, $shotH)

    # Footer strip with a thin accent line on top.
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $footerColor), 0, $areaH, $w, $footerH)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $edgeColor), 0, $areaH, $w, [Math]::Max(2, [int](3 * $scale)))
    $tagline = $taglines[$file.Name]
    if ($tagline) {
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ([single](26 * $scale)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $rect = New-Object System.Drawing.RectangleF 0, $areaH, $w, $footerH
        $g.DrawString($tagline, $font, (New-Object System.Drawing.SolidBrush $textColor), $rect, $fmt)
    }

    # Corner ribbon. Rotating 45 degrees about the top-right corner makes local +y the
    # diagonal into the image, so the ribbon is a band at distance $dist along it.
    # Its visible length at the outer edge is 2 * ($dist - $ribbonH / 2), so keep $dist
    # large enough for the logo and text to fit.
    $ribbonH = [single](78 * $scale)
    $dist    = [single](190 * $scale)
    $len     = [single](600 * $scale)
    $g.TranslateTransform($w, 0)
    $g.RotateTransform(45)
    $top = $dist - $ribbonH / 2
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $ribbonColor), -$len, $top, 2 * $len, $ribbonH)
    $edge = New-Object System.Drawing.Pen $edgeColor, ([single][Math]::Max(2, 3 * $scale))
    $g.DrawLine($edge, -$len, $top, $len, $top)
    $g.DrawLine($edge, -$len, $top + $ribbonH, $len, $top + $ribbonH)

    $logoSize = $ribbonH * 0.82
    $font = New-Object System.Drawing.Font 'Segoe UI Black', ([single]($ribbonH * 0.36)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $textSize = $g.MeasureString($ribbonText, $font)
    $gap = $ribbonH * 0.12
    $x = -($logoSize + $gap + $textSize.Width) / 2
    $g.DrawImage($logo, $x, $dist - $logoSize / 2, $logoSize, $logoSize)
    $g.DrawString($ribbonText, $font, (New-Object System.Drawing.SolidBrush $textColor), $x + $logoSize + $gap, $dist - $textSize.Height / 2)
    $g.ResetTransform()

    $out = Join-Path $outDir $file.Name
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $shot.Dispose()
    Write-Output "Wrote $out"
}
$logo.Dispose()
