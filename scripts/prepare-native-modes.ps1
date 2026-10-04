param([Parameter(Mandatory)][string]$ControlMappingPath)
$ErrorActionPreference = 'Stop'
$nativeModesRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeModesDirectory = Join-Path $nativeModesRoot '.artifacts\runtime\Configurations'
$nativeModesSource = Join-Path $nativeModesDirectory 'userConfig.json'
$nativeModesBackup = Join-Path $nativeModesRoot ('.local\rollback\native-modes-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
New-Item -ItemType Directory -Path $nativeModesBackup -Force | Out-Null
Copy-Item -LiteralPath $nativeModesSource -Destination (Join-Path $nativeModesBackup 'userConfig.json')
$nativeModesBase = Get-Content -LiteralPath $nativeModesSource -Raw -Encoding UTF8 | ConvertFrom-Json
function Copy-NativeModeObject($nativeObject) { return $nativeObject | ConvertTo-Json -Depth 70 | ConvertFrom-Json }
function Get-NativeModeCurve([string]$curveName) {
    $found = @($nativeModesBase.FanControl.FanCurves | Where-Object Name -EQ $curveName)
    if ($found.Count -ne 1) { throw "Required curve missing or ambiguous: $curveName" }
    return Copy-NativeModeObject $found[0]
}
function Get-NativeModeCurveValue($curve, [double]$temperature) {
    $points = @($curve.Points | ForEach-Object {
        $parts = $_.Split(',')
        [pscustomobject]@{ T=[double]::Parse($parts[0],[Globalization.CultureInfo]::InvariantCulture); V=[double]::Parse($parts[1],[Globalization.CultureInfo]::InvariantCulture) }
    } | Sort-Object T)
    if ($temperature -le $points[0].T) { return $points[0].V }
    for ($index=1; $index -lt $points.Count; $index++) {
        if ($temperature -le $points[$index].T) {
            $left=$points[$index-1]; $right=$points[$index]
            return $left.V + ($right.V-$left.V)*($temperature-$left.T)/($right.T-$left.T)
        }
    }
    return $points[-1].V
}
function Format-NativeModePoint([double]$temperature,[double]$percent) {
    return $temperature.ToString([Globalization.CultureInfo]::InvariantCulture) + ',' + ([math]::Round($percent,2)).ToString([Globalization.CultureInfo]::InvariantCulture)
}
function New-NativeBalancedCurve($performance,$silent) {
    $result = Copy-NativeModeObject $performance
    $result.Points = @(40,55,65,75,80,85 | ForEach-Object {
        $temperature=[double]$_
        $percent=((Get-NativeModeCurveValue $performance $temperature)+(Get-NativeModeCurveValue $silent $temperature))/2
        Format-NativeModePoint $temperature $percent
    })
    return $result
}
$cpuPerformance=Get-NativeModeCurve 'CPU-Performance'
$gpuPerformance=Get-NativeModeCurve 'GPU-Performance'
$cpuSilent=Get-NativeModeCurve 'CPU-Silent'
$gpuSilent=Get-NativeModeCurve 'GPU-Silent'
$nativePairs = @{
    Performance=@($cpuPerformance,$gpuPerformance)
    Balanced=@((New-NativeBalancedCurve $cpuPerformance $cpuSilent),(New-NativeBalancedCurve $gpuPerformance $gpuSilent))
    Night=@((Copy-NativeModeObject $cpuSilent),(Copy-NativeModeObject $gpuSilent))
}
$nativeControlMapping = Get-Content -LiteralPath $ControlMappingPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cpuIds=@($nativeControlMapping.cpuControlIds)
$gpuId=$nativeControlMapping.gpuControlId
if ($cpuIds.Count -eq 0 -or @($cpuIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -or [string]::IsNullOrWhiteSpace($gpuId)) { throw 'Explicit local CPU/GPU control mapping required.' }
$nativePrepared=@()
foreach ($name in @('Performance','Balanced','Night')) {
    $config=Copy-NativeModeObject $nativeModesBase
    $cpu=Copy-NativeModeObject $nativePairs[$name][0]; $cpu.Name='CPU'
    $gpu=Copy-NativeModeObject $nativePairs[$name][1]; $gpu.Name='GPU'
    $config.FanControl.FanCurves=@($cpu,$gpu)
    foreach ($control in $config.FanControl.Controls) {
        if ($control.Identifier -in $cpuIds) { $control.SelectedFanCurve=[pscustomobject]@{Name='CPU'} }
        elseif ($control.Identifier -eq $gpuId) { $control.SelectedFanCurve=[pscustomobject]@{Name='GPU'} }
        elseif ($control.Enable) { throw "Unmapped enabled channel: $($control.Identifier)" }
    }
    if (($config.MainWindow | ConvertTo-Json -Depth 30 -Compress) -ne ($nativeModesBase.MainWindow | ConvertTo-Json -Depth 30 -Compress)) { throw 'Tray/window settings changed' }
    $path=Join-Path $nativeModesDirectory ($name+'.json')
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $nativeModesBackup ($name+'.json')) }
    $config | ConvertTo-Json -Depth 70 | Set-Content -LiteralPath $path -Encoding utf8
    $nativePrepared+=@{ name=$name; path=$path; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
@{ sampledAt=[DateTimeOffset]::UtcNow.ToString('O'); backup=$nativeModesBackup; profiles=$nativePrepared; traySettingsIdentical=$true; coolingCommandSent=$false; radiatorControlIncluded=$false } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $nativeModesRoot '.local\native-mode-preparation.json') -Encoding utf8
