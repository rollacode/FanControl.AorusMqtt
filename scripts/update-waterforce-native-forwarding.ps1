param([ValidateSet('Waterforce','Mqtt')][string]$Plugin = 'Waterforce')
$ErrorActionPreference = 'Stop'
$updateRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$updateLocal = Join-Path $env:LOCALAPPDATA 'FanControlMqtt'
$updateRuntime = Join-Path $updateRoot '.artifacts\runtime\FanControl.exe'
$updateReceipt = Join-Path $updateRoot ('.local\'+$Plugin.ToLowerInvariant()+'-native-forwarding-update.json')
trap { @{success=$false; error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $updateReceipt -Encoding UTF8; exit 1 }
$updateInstances = @(Get-Process FanControl -ErrorAction Stop)
if ($updateInstances.Count -ne 1 -or $updateInstances[0].Path -ne $updateRuntime) { throw 'Unexpected native runtime.' }
$updateSnapshot = Get-Content -LiteralPath (Join-Path $updateLocal 'diagnostic-snapshot.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$updateMode = $updateSnapshot.snapshot.bridge.observedProfile
if ($updateMode -notin @('Performance','Balanced','Night')) { throw 'Actual active configuration unknown; no guessed mode.' }
$updateBackup = Join-Path $updateRoot ('.local\rollback\waterforce-forwarding-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
New-Item -ItemType Directory -Path $updateBackup -Force | Out-Null
$updateConfigHashes = @{}
foreach ($updateName in @('Performance','Balanced','Night')) {
    $updateConfigHashes[$updateName] = (Get-FileHash -LiteralPath (Join-Path $updateRoot ('.artifacts\runtime\Configurations\'+$updateName+'.json'))).Hash
}
$updateExit = Start-Process -FilePath $updateRuntime -ArgumentList '-e' -WorkingDirectory (Split-Path $updateRuntime) -WindowStyle Hidden -PassThru
[void]$updateExit.WaitForExit(10000)
$updateRecovered = $false
if (-not $updateInstances[0].WaitForExit(15000)) {
    $updateHardware = Get-Content -LiteralPath (Join-Path $updateLocal 'waterforce-plugin-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($updateHardware.hostProcessId -ne $updateInstances[0].Id -or $updateHardware.hardwareControlEnabled) { throw 'Native hardware Reset not confirmed; armed runtime left intact.' }
    Stop-Process -Id $updateInstances[0].Id -Force
    if (-not $updateInstances[0].WaitForExit(10000)) { throw 'Released native runtime did not exit.' }
    $updateRecovered = $true
}
$updatePluginName = if ($Plugin -eq 'Waterforce') { 'FanControl.GigabyteWaterforce.dll' } else { 'FanControl.Mqtt.dll' }
$updateBuildDirectory = if ($Plugin -eq 'Waterforce') { '.artifacts\waterforce-validated' } else { '.artifacts\mqtt-plugin' }
$updatePlugin = Join-Path $updateRoot ('.artifacts\runtime\Plugins\'+$updatePluginName)
Copy-Item -LiteralPath $updatePlugin -Destination $updateBackup
Copy-Item -LiteralPath (Join-Path (Join-Path $updateRoot $updateBuildDirectory) $updatePluginName) -Destination $updatePlugin -Force
Start-Process -FilePath $updateRuntime -ArgumentList ('-c "'+(Join-Path $updateRoot ('.artifacts\runtime\Configurations\'+$updateMode+'.json'))+'" -w') -WorkingDirectory (Split-Path $updateRuntime) -WindowStyle Normal | Out-Null
@{success=$true; restoredMode=$updateMode; recoveredReleasedShutdown=$updateRecovered; backup=$updateBackup; installedSha256=(Get-FileHash -LiteralPath $updatePlugin).Hash; originalConfigHashes=$updateConfigHashes; sampledAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $updateReceipt -Encoding UTF8
