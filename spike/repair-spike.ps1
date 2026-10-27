# Spike: repair an STL with Windows.Graphics.Printing3D (the engine behind 3D Builder's Repair).
# Must run in Windows PowerShell 5.1, which can project WinRT types directly.
param(
    [Parameter(Mandatory)][string]$InputStl,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -Path "$PSScriptRoot\..\src\MeshIO.cs" -ReferencedAssemblies System.IO.Compression, System.IO.Compression.FileSystem, System.Xml
$null = [Windows.Graphics.Printing3D.Printing3D3MFPackage, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]

$asTaskOp = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await-Op($op, [type]$resultType) {
    $task = $asTaskOp.MakeGenericMethod($resultType).Invoke($null, @($op))
    $task.Wait() | Out-Null
    $task.Result
}
$asTaskAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await-Action($action) { $asTaskAction.Invoke($null, @($action)).Wait() | Out-Null }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$name = [IO.Path]::GetFileNameWithoutExtension($InputStl)
$in3mf = Join-Path $OutDir "$name.input.3mf"
$out3mf = Join-Path $OutDir "$name.repaired.3mf"
$outStl = Join-Path $OutDir "$name.repaired.stl"
$sw = [Diagnostics.Stopwatch]::StartNew()

$mesh = [MeshIO]::ReadStl($InputStl)
"BEFORE  $([MeshIO]::Stats($mesh))"
[MeshIO]::Write3mf($mesh, $in3mf)
"  wrote $in3mf ($([int]$sw.Elapsed.TotalSeconds)s)"

# Printing3D clones the stream, so it needs a native WinRT stream rather than a wrapped .NET one.
$fs = Await-Op ([Windows.Storage.Streams.FileRandomAccessStream]::OpenAsync((Resolve-Path $in3mf).Path, [Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$pkg = New-Object Windows.Graphics.Printing3D.Printing3D3MFPackage
$model = Await-Op ($pkg.LoadModelFromPackageAsync($fs)) ([Windows.Graphics.Printing3D.Printing3DModel])
$fs.Dispose()
"  loaded via Printing3D: $($model.Meshes.Count) mesh(es) ($([int]$sw.Elapsed.TotalSeconds)s)"

$t0 = $sw.Elapsed.TotalSeconds
Await-Action $model.RepairAsync()
"  RepairAsync done in $([math]::Round($sw.Elapsed.TotalSeconds - $t0, 1))s"

$outPkg = New-Object Windows.Graphics.Printing3D.Printing3D3MFPackage
Await-Action $outPkg.SaveModelToPackageAsync($model)
$ras = Await-Op $outPkg.SaveAsync() ([Windows.Storage.Streams.IRandomAccessStream])
$src = [IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
$dst = [IO.File]::Create($out3mf); $src.CopyTo($dst); $dst.Dispose(); $src.Dispose()

$repaired = [MeshIO]::Read3mf($out3mf)
"AFTER   $([MeshIO]::Stats($repaired))"
[MeshIO]::WriteStl($repaired, $outStl)
"  wrote $outStl (total $([int]$sw.Elapsed.TotalSeconds)s)"
