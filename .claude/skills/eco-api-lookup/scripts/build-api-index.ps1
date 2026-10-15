# Builds a flat, grep-friendly Eco API index from the ModKit's docfx metadata cache + XML doc comments.
# Usage: powershell -File build-api-index.ps1 -RefDir <ReferenceAssemblies dir> -OutDir <output dir>
param(
    [Parameter(Mandatory)] [string] $RefDir,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 1. Summaries from the XML doc files, keyed by commentId (T:/M:/P:/F:/E:).
$summaries = @{}
foreach ($xf in Get-ChildItem $RefDir -Filter *.xml) {
    try { $x = [xml](Get-Content $xf.FullName -Raw) } catch { continue }
    if (-not $x.doc) { continue }
    foreach ($m in $x.doc.members.member) {
        if (-not $m.summary) { continue }
        $s = $m.summary.InnerXml -replace '<see(also)? cref="[A-Z]:([^"]+)"\s*/>', '$2' -replace '<see(also)? (langword|href)="([^"]+)"\s*/>', '$3' -replace '<[^>]+>', '' -replace '\s+', ' '
        $s = [System.Net.WebUtility]::HtmlDecode($s).Trim()
        if ($s) { $summaries[$m.name] = $s }
    }
}

# 2. Declarations from the docfx YAML (obj/xdoc/cache/obj/*). Each file is one assembly.
foreach ($yf in Get-ChildItem (Join-Path $RefDir 'obj\xdoc\cache\obj') -File) {
    $reader = [IO.StreamReader]::new($yf.FullName)
    $first = $reader.ReadLine()
    if ($first -notmatch '^id: (.+)$') { $reader.Close(); continue }
    $assembly = $Matches[1]
    $out = [Collections.Generic.List[string]]::new()
    $current = $null; $pending = $null
    while ($null -ne ($line = $reader.ReadLine())) {
        if ($line -match '^\s*commentId: (.+)$') { $current = $Matches[1]; continue }
        if ($line -match '^\s*content:\s*$') { $pending = 'content'; continue }
        if ($pending -eq 'content' -and $line -match '^(\s*)CSharp: ?(.*)$') {
            $indent = $Matches[1].Length; $val = $Matches[2]
            $parts = [Collections.Generic.List[string]]::new()
            if ($val -notmatch '^[>|]-?$') { $parts.Add($val) }
            # continuation lines are indented deeper than the CSharp: key
            while ($reader.Peek() -ge 0) {
                $next = $reader.ReadLine()
                $ni = ($next -replace '^(\s*).*$', '$1').Length
                if ($next.Trim().Length -gt 0 -and $ni -le $indent) { $line = $next; break }
                $parts.Add($next.Trim())
            }
            $sig = (($parts | Where-Object { $_ -ne '' }) -join ' ').Trim()
            if ($sig.StartsWith("'") -and $sig.EndsWith("'")) { $sig = $sig.Substring(1, $sig.Length - 2) -replace "''", "'" }
            elseif ($sig.StartsWith('"') -and $sig.EndsWith('"')) { $sig = $sig.Substring(1, $sig.Length - 2) -replace '\\"', '"' }
            if ($current -and $current -notmatch '^(N|Overload):') {
                $sum = $summaries[$current]
                if ($sum -and $sum.Length -gt 300) { $sum = $sum.Substring(0, 297) + '...' }
                $out.Add("$current`t$sig" + $(if ($sum) { "`t// $sum" } else { '' }))
            }
            $pending = $null
            continue
        }
        if ($pending -and $line -notmatch '^\s*$') { $pending = $null }
    }
    $reader.Close()
    $out.Sort()
    [IO.File]::WriteAllLines((Join-Path $OutDir "$assembly.api.tsv"), $out, [Text.UTF8Encoding]::new($false))
    "{0}: {1} members" -f $assembly, $out.Count
}
