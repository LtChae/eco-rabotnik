# Builds grep-friendly indexes of the vanilla Eco content found in Mods/__core__.
# Usage: powershell -File build-vanilla-index.ps1 -CoreDir <...\Server\Mods\__core__> -OutDir <output dir>
param(
    [Parameter(Mandatory)] [string] $CoreDir,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$types   = [Collections.Generic.List[string]]::new()
$recipes = [Collections.Generic.List[string]]::new()
$tags    = @{}

foreach ($f in Get-ChildItem $CoreDir -Recurse -Filter *.cs) {
    if ($f.Name -eq 'WorldObjectOccupancyAutoGen.cs') { continue }
    $rel  = $f.FullName.Substring($CoreDir.Length + 1)
    $text = [IO.File]::ReadAllText($f.FullName)

    # class Name : Base   (base may span lines / be generic)
    foreach ($m in [regex]::Matches($text, '(?m)^\s*(?:\[[^\n]*\]\s*)*(?:public |internal )?(?:abstract |sealed |static )?(?:partial )?class\s+(\w+)\s*:\s*([\w\.]+(?:<[^>{]+>)?)')) {
        $types.Add("$($m.Groups[1].Value)`t$($m.Groups[2].Value.Trim())`t$rel")
    }

    # Tags applied to items/blocks/objects and tags used as recipe ingredients
    foreach ($m in [regex]::Matches($text, '\[Tag\("([^"]+)"')) { $tags[$m.Groups[1].Value] = 1 + [int]$tags[$m.Groups[1].Value] }

    # Recipe families: name, required skill/level, tables, ingredients, outputs
    foreach ($m in [regex]::Matches($text, '(?s)((?:\[[^\]]*\]\s*)*)public partial class (\w+) : RecipeFamily(.*?)partial void ModsPostInitialize\(\);')) {
        $attrs = $m.Groups[1].Value; $name = $m.Groups[2].Value; $body = $m.Groups[3].Value
        $skill = ([regex]::Matches($attrs, 'RequiresSkill\(typeof\((\w+)\),\s*(\d+)\)') | ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" }) -join ' & '
        $tables = ([regex]::Matches($body, 'AddRecipe\(tableType:\s*typeof\((\w+)\)') | ForEach-Object { $_.Groups[1].Value }) -join ','
        $ings = ([regex]::Matches($body, 'new IngredientElement\((?:typeof\((\w+)\)|"([^"]+)"),\s*([\d\.f]+)') | ForEach-Object {
            $id = if ($_.Groups[1].Success) { $_.Groups[1].Value } else { "tag:" + $_.Groups[2].Value }; "$($_.Groups[3].Value) $id" }) -join ', '
        $outs = ([regex]::Matches($body, 'new CraftingElement<(\w+)>\(((?:[^()]|\([^()]*\))*)\)') | ForEach-Object {
            $n = ($_.Groups[2].Value -split ',')[-1].Trim(); if (-not $n) { $n = '1' }; "$n $($_.Groups[1].Value)" }) -join ', '
        $recipes.Add("$name`t$tables`t$skill`t$ings => $outs`t$rel")
    }
}

$types.Sort(); $recipes.Sort()
$enc = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines((Join-Path $OutDir 'vanilla-types.tsv'), (@("# ClassName`tBaseType`tFile (relative to Mods/__core__)") + $types), $enc)
[IO.File]::WriteAllLines((Join-Path $OutDir 'vanilla-recipes.tsv'), (@("# RecipeFamily`tCraftingTables`tRequiredSkill Level`tIngredients => Outputs (counts are base amounts)`tFile") + $recipes), $enc)
[IO.File]::WriteAllLines((Join-Path $OutDir 'vanilla-tags.txt'), (@("# Tag name`tnumber of [Tag(...)] uses in __core__") + ($tags.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)`t$($_.Value)" })), $enc)
"types: {0}  recipes: {1}  tags: {2}" -f $types.Count, $recipes.Count, $tags.Count
