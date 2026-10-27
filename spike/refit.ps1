# Refit the repair-time estimate: pair each logged repair with its original file, count shells, and fit
# log(time) against log(tris), log(1+bad), log(shells).
#
# Repairs in place replace the originals, so they're looked for in -Originals (folders holding untouched copies)
# and in the Recycle Bin, matched by file name and triangle count.
#   spike\refit.ps1 -Logs mylog.csv -Originals D:\models-backup
param(
    [string[]]$Logs = @("$env:LOCALAPPDATA\3mfBatchRepair\3mfBatchRepair-log.csv"),
    [string[]]$Originals = @()
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition (Get-Content -Raw "$PSScriptRoot\..\src\MeshIO.cs") -ReferencedAssemblies System.IO.Compression, System.IO.Compression.FileSystem, System.Xml
New-Item -ItemType Directory -Force "$PSScriptRoot\..\test-out" | Out-Null

$logs = $Logs | Where-Object { Test-Path $_ }
$rows = foreach ($l in $logs) { Get-Content $l -Encoding UTF8 | Where-Object { $_ -notmatch '^time,' } | ConvertFrom-Csv -Header time,input,output,status,before,after,seconds,read_s,prepare_s,repair_s,write_s,error }
$rows = $rows | Where-Object { $_.status -eq 'repaired' -and $_.repair_s -and $_.before -match '^(\d+) tris' }

# candidate originals by file name
$cands = @{}
function AddCand($name, $path) { $k = $name.ToLowerInvariant(); if (-not $cands[$k]) { $cands[$k] = @() }; $cands[$k] += $path }
foreach ($dir in $Originals) {
    Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { [MeshIO]::IsMesh($_.Name) } | ForEach-Object { AddCand $_.Name $_.FullName }
}
(New-Object -ComObject Shell.Application).NameSpace(10).Items() | ForEach-Object {
    if ([MeshIO]::IsMesh($_.Path)) { AddCand $_.Name $_.Path }
}

$seen = @{}
$samples = foreach ($r in $rows) {
    $tris = [int]($r.before -replace ' tris.*')
    $name = Split-Path $r.input -Leaf
    $key = "$name|$tris"
    if ($seen[$key]) { continue }
    foreach ($p in $cands[$name.ToLowerInvariant()]) {
        $m = $null
        try { $m = [MeshIO]::Read($p) } catch { continue }
        if ($m.TriangleCount -ne $tris) { continue }
        $s = [MeshIO]::Stats($m)
        $seen[$key] = $true
        [pscustomobject]@{ name = $name; tris = $tris; bad = $s.NonManifoldEdges + $s.BadOrientationEdges + $s.BoundaryEdges + $s.Degenerate; shells = $s.Shells; t = [double]$r.repair_s }
        break
    }
}
$samples | Export-Csv "$PSScriptRoot\..\test-out\refit-samples.csv" -NoTypeInformation
"samples with originals found: $(@($samples).Count) of $(@($rows | ForEach-Object { (Split-Path $_.input -Leaf) + '|' + ($_.before -replace ' tris.*') } | Sort-Object -Unique).Count) logged repairs"

$d = $samples | ForEach-Object { [pscustomobject]@{ tri = [math]::Log($_.tris); bad = [math]::Log(1 + $_.bad); shl = [math]::Log($_.shells); y = [math]::Log($_.t) } }
function Fit($cols) {
    $n = $cols.Count + 1; $A = New-Object 'double[,]' $n, ($n + 1)
    foreach ($r in $d) { $v = @(1.0) + @($cols | ForEach-Object { $r.$_ }); for ($i = 0; $i -lt $n; $i++) { for ($j = 0; $j -lt $n; $j++) { $A[$i, $j] += $v[$i] * $v[$j] }; $A[$i, $n] += $v[$i] * $r.y } }
    for ($i = 0; $i -lt $n; $i++) { $p = $A[$i, $i]; for ($j = $i; $j -le $n; $j++) { $A[$i, $j] /= $p }; for ($k = 0; $k -lt $n; $k++) { if ($k -ne $i) { $f = $A[$k, $i]; for ($j = $i; $j -le $n; $j++) { $A[$k, $j] -= $f * $A[$i, $j] } } } }
    $b = 0..($n - 1) | ForEach-Object { $A[$_, $n] }
    $my = ($d | Measure-Object y -Average).Average; $ssr = 0; $sst = 0
    foreach ($r in $d) { $v = @(1.0) + @($cols | ForEach-Object { $r.$_ }); $p = 0; for ($i = 0; $i -lt $n; $i++) { $p += $b[$i] * $v[$i] }; $ssr += ($r.y - $p) * ($r.y - $p); $sst += ($r.y - $my) * ($r.y - $my) }
    $factor = [math]::Exp([math]::Sqrt($ssr / @($d).Count))
    # ordering is what matters most, so also report rank correlation of predicted vs actual (Spearman)
    $pred = foreach ($r in $d) { $v = @(1.0) + @($cols | ForEach-Object { $r.$_ }); $p = 0; for ($i = 0; $i -lt $n; $i++) { $p += $b[$i] * $v[$i] }; $p }
    $rho = Spearman $pred @($d | ForEach-Object { $_.y })
    "{0,-16} coef: {1,-32} R2={2:0.00}  typical error x{3:0.0}  rank corr={4:0.00}" -f ($cols -join '+'), (($b | ForEach-Object { $_.ToString('0.000') }) -join ', '), (1 - $ssr / $sst), $factor, $rho
}
function Ranks($xs) { $idx = 0..($xs.Count - 1) | Sort-Object { $xs[$_] }; $r = New-Object double[] $xs.Count; for ($k = 0; $k -lt $idx.Count; $k++) { $r[$idx[$k]] = $k }; $r }
function Spearman($a, $b) {
    $ra = Ranks $a; $rb = Ranks $b; $n = $a.Count; $s = 0
    for ($i = 0; $i -lt $n; $i++) { $s += ($ra[$i] - $rb[$i]) * ($ra[$i] - $rb[$i]) }
    1 - 6 * $s / ($n * ($n * $n - 1))
}
Fit @('tri'); Fit @('tri', 'bad'); Fit @('tri', 'shl'); Fit @('tri', 'bad', 'shl'); Fit @('shl')

