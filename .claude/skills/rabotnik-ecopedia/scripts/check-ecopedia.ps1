# Validates Rabotnik's Ecopedia pages the way the server will read them (decompiled v0.14.1.1 rules, see eco-modding "Ecopedia pages").
# Checks: every file parses as XML; files sit one level under Ecopedia/; every [link] / {link} names an existing page (vanilla or ours);
# no page name collides with a vanilla page; no XML entities. Prints problems and exits 1 if any.
# Usage: powershell -File .claude/skills/rabotnik-ecopedia/scripts/check-ecopedia.ps1
param(
    [string]$ModEcopedia = "$PSScriptRoot\..\..\..\..\Mods\UserCode\Rabotnik\Ecopedia",
    [string]$VanillaEcopedia = "E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__\Ecopedia"
)

$problems = @()
$ModEcopedia = (Resolve-Path $ModEcopedia).Path

# Page name = last ';' part of the file name, for files that have at least one ';'. Only immediate subfolders are loaded.
function Get-PageNames($root) {
    Get-ChildItem -LiteralPath $root -Directory | ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter *.xml } |
        ForEach-Object { $p = $_.BaseName.Split(';'); if ($p.Length -gt 1) { $p[-1] } }
}

$vanilla = @(Get-PageNames $VanillaEcopedia)
$mine = @(Get-PageNames $ModEcopedia)
$all = $vanilla + $mine

foreach ($name in $mine) { if ($vanilla -contains $name) { $problems += "Page name collides with vanilla: $name" } }
$mine | Group-Object | Where-Object Count -gt 1 | ForEach-Object { $problems += "Duplicate page name: $($_.Name)" }

# XML directly in Ecopedia/ or nested deeper than one folder is silently ignored by the server.
Get-ChildItem -LiteralPath $ModEcopedia -Recurse -Filter *.xml | Where-Object { $_.Directory.Parent.FullName -ne $ModEcopedia } |
    ForEach-Object { $problems += "Not loaded (must be in Ecopedia/<folder>/): $($_.FullName)" }

foreach ($f in Get-ChildItem -LiteralPath $ModEcopedia -Recurse -Filter *.xml) {
    $text = Get-Content -LiteralPath $f.FullName -Raw
    try { [xml]$text | Out-Null } catch { $problems += "XML error in $($f.Name): $($_.Exception.Message)" }
    if ($text -match '&(amp|lt|gt|quot|apos|#\d+);') { $problems += "XML entity in $($f.Name) (the client gets raw inner XML; reword instead)" }
    foreach ($m in [regex]::Matches($text, '[\[{](.+?)[\]}]')) {
        $link = $m.Groups[1].Value
        $target = ($link.Split('|'))[-1]
        if ($all -notcontains $target) { $problems += "Unresolved link in $($f.Name): [$link]" }
    }
}

if ($problems.Count) { $problems | ForEach-Object { Write-Output $_ }; exit 1 }
Write-Output "OK: $($mine.Count) Rabotnik pages, all links resolve."
