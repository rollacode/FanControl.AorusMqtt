param([string]$MigrationPlanPath)
$ErrorActionPreference = 'Stop'
$installIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($installIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights for the same interactive user are required.' }
$installRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installRuntime = Join-Path $installRoot '.artifacts\runtime\FanControl.exe'
$installPlugins = Join-Path $installRoot '.artifacts\runtime\Plugins'
$installData = Join-Path $env:LOCALAPPDATA 'FanControlMqtt'
$installReceipt = Join-Path $installRoot '.local\native-startup-install.json'
$installBackup = Join-Path $installRoot ('.local\rollback\native-startup-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
trap { @{success=$false; error=$_.Exception.Message; backup=$installBackup; sampledAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath $installReceipt -Encoding UTF8; exit 1 }
foreach ($installBuild in @('.artifacts\mqtt-plugin\FanControl.Mqtt.dll','.artifacts\waterforce-validated\FanControl.GigabyteWaterforce.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot $installBuild))) { throw 'Build both neutral plugins before installation.' }
}
$installPlan = if ($MigrationPlanPath) { Get-Content -LiteralPath $MigrationPlanPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$installSourceData = if ($installPlan) { [IO.Path]::GetFullPath($installPlan.sourceDataDirectory) } else { $installData }
if (-not $installSourceData.StartsWith([IO.Path]::GetFullPath($env:LOCALAPPDATA)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Migration data must stay under the current user application-data directory.' }
$installSourceSettings = Join-Path $installSourceData 'settings.json'
if (-not (Test-Path -LiteralPath $installSourceSettings)) { throw 'Local MQTT settings are missing; installation does not guess broker details.' }
New-Item -ItemType Directory -Path $installBackup -Force | Out-Null
$installTask = Get-ScheduledTask -TaskName 'FanControl' -ErrorAction SilentlyContinue
if ($installTask) { Export-ScheduledTask -TaskName 'FanControl' | Set-Content -LiteralPath (Join-Path $installBackup 'native-task.xml') -Encoding UTF8 }
$installServices = @()
foreach ($installServiceName in @('FanControl.Service','AsusFanControlService','AorusLcdService')) {
    $installService = Get-Service -Name $installServiceName -ErrorAction SilentlyContinue
    if ($installService) {
        if ($installService.Status -eq 'Running') { throw 'A competing service is running; release its ownership before configuring startup.' }
        $installServices += @{name=$installServiceName; startupType=[string]$installService.StartType}
    }
}
$installServices | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installBackup 'service-startup.json') -Encoding UTF8
$installInstances = @(Get-Process FanControl -ErrorAction SilentlyContinue)
if ($installInstances.Count -gt 1 -or ($installInstances.Count -eq 1 -and $installInstances[0].Path -ne $installRuntime)) { throw 'Unexpected native runtime instance.' }
if ($installInstances.Count -eq 1) {
    $installExit = Start-Process -FilePath $installRuntime -ArgumentList '-e' -WorkingDirectory (Split-Path $installRuntime) -WindowStyle Hidden -PassThru
    if (-not $installExit.WaitForExit(10000) -or -not $installInstances[0].WaitForExit(20000)) { throw 'Native normal exit not confirmed; no forced termination performed.' }
}
New-Item -ItemType Directory -Path $installData -Force | Out-Null
# Copy protected credentials as bytes. No decryption, output or username rewrite.
foreach ($installDataName in @('settings.json','state.json','mqtt-credentials.dpapi','native-mqtt-handover.json','waterforce-control.json','waterforce-radiator-2500-pass.json','waterforce-fan-before-control.json')) {
    $installDataSource = Join-Path $installSourceData $installDataName
    $installDataTarget = Join-Path $installData $installDataName
    if (Test-Path -LiteralPath $installDataTarget) { Copy-Item -LiteralPath $installDataTarget -Destination (Join-Path $installBackup $installDataName) }
    if ($installDataSource -ne $installDataTarget -and (Test-Path -LiteralPath $installDataSource)) { Copy-Item -LiteralPath $installDataSource -Destination $installDataTarget -Force }
}
$installSettings = Get-Content -LiteralPath (Join-Path $installData 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$installSettings.runtimePath = $installRuntime
if ($installSettings.handoverEvidencePath) {
    $installEvidence = [IO.Path]::GetFullPath($installSettings.handoverEvidencePath)
    if ($installEvidence.StartsWith($installSourceData+'\',[StringComparison]::OrdinalIgnoreCase)) {
        $installEvidenceName = [IO.Path]::GetFileName($installEvidence)
        if (-not (Test-Path -LiteralPath (Join-Path $installData $installEvidenceName))) { Copy-Item -LiteralPath $installEvidence -Destination (Join-Path $installData $installEvidenceName) }
        $installSettings.handoverEvidencePath = Join-Path $installData $installEvidenceName
    }
}
$installSettings | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $installData 'settings.json') -Encoding UTF8
if ($installPlan -and $installPlan.previousMqttPluginFileName) {
    $installPreviousName = [string]$installPlan.previousMqttPluginFileName
    if ([IO.Path]::GetFileName($installPreviousName) -ne $installPreviousName -or [IO.Path]::GetExtension($installPreviousName) -ne '.dll') { throw 'Invalid previous plugin filename.' }
    $installPreviousPath = [IO.Path]::GetFullPath((Join-Path $installPlugins $installPreviousName))
    $installPreviousBackup = [IO.Path]::GetFullPath((Join-Path $installBackup $installPreviousName))
    if (-not $installPreviousPath.StartsWith($installPlugins+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $installPreviousBackup.StartsWith($installBackup+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Plugin migration path is outside its expected directory.' }
    if ($installPreviousName -ne 'FanControl.Mqtt.dll' -and (Test-Path -LiteralPath $installPreviousPath)) { Move-Item -LiteralPath $installPreviousPath -Destination $installPreviousBackup }
}
foreach ($installPlugin in @(@{name='FanControl.Mqtt.dll'; folder='mqtt-plugin'},@{name='FanControl.GigabyteWaterforce.dll'; folder='waterforce-validated'})) {
    $installPluginTarget = Join-Path $installPlugins $installPlugin.name
    if (Test-Path -LiteralPath $installPluginTarget) { Copy-Item -LiteralPath $installPluginTarget -Destination (Join-Path $installBackup $installPlugin.name) }
    Copy-Item -LiteralPath (Join-Path $installRoot ('.artifacts\'+$installPlugin.folder+'\'+$installPlugin.name)) -Destination $installPluginTarget -Force
}
foreach ($installService in $installServices) { Set-Service -Name $installService.name -StartupType Manual }
$installAction = New-ScheduledTaskAction -Execute $installRuntime -Argument '-w' -WorkingDirectory (Split-Path $installRuntime)
$installPrincipal = New-ScheduledTaskPrincipal -UserId $installIdentity.User.Value -LogonType Interactive -RunLevel Highest
$installTrigger = New-ScheduledTaskTrigger -AtLogOn -User $installIdentity.User.Value
$installTrigger.Delay = 'PT15S'
$installTaskSettings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'FanControl' -Action $installAction -Trigger $installTrigger -Principal $installPrincipal -Settings $installTaskSettings -Description 'Fan Control with local AORUS radiator and MQTT plugins; owner interactive logon.' -Force | Out-Null
for ($installTaskWait=0; $installTaskWait -lt 8 -and (Get-ScheduledTask -TaskName 'FanControl').State -eq 'Running'; $installTaskWait++) { Start-Sleep -Seconds 1 }
if ((Get-ScheduledTask -TaskName 'FanControl').State -eq 'Running') { throw 'Previous scheduled-task instance has not completed; new launch not attempted.' }
Start-ScheduledTask -TaskName 'FanControl'
@{success=$true; nativeTask='FanControl'; highestInteractive=$true; restoredFromNativeCache=$true; sourceDataPreserved=$true; credentialFilePresent=(Test-Path -LiteralPath (Join-Path $installData 'mqtt-credentials.dpapi')); manualServices=@($installServices.name); backup=$installBackup; sampledAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $installReceipt -Encoding UTF8
