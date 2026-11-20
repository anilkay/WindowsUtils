# Lists UI texts that have no translation yet.
# Collects the first argument of every L.T("...") / L.F("...") call in the WinForms project
# (adjacent "a" + "b" literals are joined) and compares it with the names in
# Localization/Strings.<culture>.resx. Values shown through L.V(...) come from Core at run time and
# cannot be found this way: add those as "value:<English>" entries by hand. The same goes for texts
# passed through a variable (e.g. the row names in DiskHealthControl.Properties).
#
#   pwsh tools/Check-Translations.ps1            # Turkish
#   pwsh tools/Check-Translations.ps1 -Culture de
param([string]$Culture = 'tr')

$root = Join-Path $PSScriptRoot '..\WindowsUtils'
$resx = Join-Path $root "Localization\Strings.$Culture.resx"
[xml]$xml = Get-Content $resx -Raw -Encoding UTF8
$known = @{}
foreach ($d in $xml.root.data) { $known[$d.name] = $true }

$literal = '"((?:[^"\\]|\\.)*)"'
$call = [regex]('L\.[TF]\(\s*(' + $literal + '(?:\s*\+\s*' + $literal + ')*)')
$used = @{}
Get-ChildItem $root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'L.cs' } |
    ForEach-Object {
        $text = Get-Content $_.FullName -Raw -Encoding UTF8
        foreach ($m in $call.Matches($text)) {
            $key = -join ([regex]::Matches($m.Groups[1].Value, $literal) | ForEach-Object { [regex]::Unescape($_.Groups[1].Value) })
            if (-not $used.ContainsKey($key)) { $used[$key] = $_.Name }
        }
    }

$missing = $used.Keys | Where-Object { -not $known.ContainsKey($_) } | Sort-Object
if ($missing) {
    Write-Host "$($missing.Count) text(s) without a '$Culture' translation:"
    $missing | ForEach-Object { Write-Host "  [$($used[$_])] $_" }
    exit 1
}
Write-Host "All $(@($used.Keys).Length) UI texts have a '$Culture' translation."
