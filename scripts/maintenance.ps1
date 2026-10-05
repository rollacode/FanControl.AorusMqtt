[CmdletBinding()]
param(
    [ValidateSet('Status','Backup','Restart','Install','Restore','RepairNightCpu')][string]$Action='Status',
    [string]$RuntimePath='',
    [string]$TaskName='FanControl',
    [string]$BackupPath='',
    [switch]$Force,
    [switch]$WithWaterforce,
    [string]$ResultPath=''
)
$ErrorActionPreference='Stop'
$maintRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$maintLocal=Join-Path $maintRoot '.local/maintenance'
$maintData=Join-Path $env:LOCALAPPDATA 'FanControlMqtt'
$maintProbe=Join-Path $maintRoot '.artifacts/probe/FanControlBridge.Probe.dll'

function Read-MaintJson([string]$Path) {
    if(-not (Test-Path -LiteralPath $Path)) { return $null }
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8); try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() } }
    finally { $stream.Dispose() }
}
function Write-MaintJson([string]$Path,$Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
}
function Get-MaintHosts { @(Get-CimInstance Win32_Process -Filter "Name='FanControl.exe'") }
function Is-MaintFresh($Receipt,[int]$Seconds=15) {
    if($null -eq $Receipt -or -not $Receipt.sampledAt) { return $false }
    # PowerShell 7 may deserialize ISO timestamps as DateTime; Windows
    # PowerShell keeps strings. Preserve the offset instead of culture parsing.
    $age=([DateTimeOffset]::UtcNow-[DateTimeOffset]$Receipt.sampledAt).TotalSeconds
    return $age -ge -5 -and $age -le $Seconds
}
$maintSettings=Read-MaintJson (Join-Path $maintData 'settings.json')
if(-not $RuntimePath) { $RuntimePath=$maintSettings.runtimePath }
if(-not $RuntimePath) { $RuntimePath=Join-Path $maintRoot '.artifacts/runtime/FanControl.exe' }
$RuntimePath=[IO.Path]::GetFullPath($RuntimePath)
if([IO.Path]::GetFileName($RuntimePath) -ne 'FanControl.exe') { throw 'Expected the installed FanControl.exe path' }
$maintRuntime=Split-Path $RuntimePath
$maintConfigs=Join-Path $maintRuntime 'Configurations'
if($maintSettings.profiles) {
    $maintFolders=@($maintSettings.profiles | ForEach-Object { Split-Path ([IO.Path]::GetFullPath($_.configPath)) } | Select-Object -Unique)
    if($maintFolders.Count -ne 1) { throw 'Maintenance requires one native configuration folder' }
    $maintConfigs=$maintFolders[0]
}

# Mutations run under normal UAC, using the same interactive Windows account.
if($Action -ne 'Status' -and $Action -ne 'Backup') {
    $maintAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if(-not $maintAdmin) {
        $maintResult=Join-Path $maintLocal ('result-'+[Guid]::NewGuid().ToString('N')+'.json')
        [IO.Directory]::CreateDirectory($maintLocal) | Out-Null
        $maintArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Action',$Action,'-RuntimePath',('"'+$RuntimePath+'"'),'-TaskName',('"'+$TaskName+'"'),'-ResultPath',('"'+$maintResult+'"'))
        if($BackupPath) { $maintArgs+=@('-BackupPath',('"'+[IO.Path]::GetFullPath($BackupPath)+'"')) }
        if($Force) { $maintArgs+='-Force' }
        if($WithWaterforce) { $maintArgs+='-WithWaterforce' }
        $maintElevated=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $maintArgs -Wait -PassThru
        $maintReceipt=Read-MaintJson $maintResult
        if($null -eq $maintReceipt) { throw 'Elevated action returned no receipt; UAC may have been cancelled' }
        $maintReceipt | ConvertTo-Json -Depth 8
        if(-not $maintReceipt.success -or $maintElevated.ExitCode -ne 0) { exit 1 }
        return
    }
}
function New-MaintBackup {
    $backup=Join-Path $maintLocal ('backups/'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    foreach($folder in @('Configurations','Plugins','LocalSettings')) { [IO.Directory]::CreateDirectory((Join-Path $backup $folder)) | Out-Null }
    foreach($file in @(Get-ChildItem -LiteralPath $maintConfigs -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.json' -or $_.Name -eq 'CACHE' })) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $backup ('Configurations/'+$file.Name))
    }
    foreach($name in @('FanControl.Mqtt.dll','FanControl.GigabyteWaterforce.dll')) {
        $source=Join-Path $maintRuntime ('Plugins/'+$name)
        if(Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $backup ('Plugins/'+$name)) }
    }
    foreach($name in @('settings.json','mqtt-credentials.dpapi','state.json','cpu-performance-before-night.json','waterforce-control.json','native-mqtt-handover.json')) {
        $source=Join-Path $maintData $name
        if(Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $backup ('LocalSettings/'+$name)) }
    }
    Write-MaintJson (Join-Path $backup 'manifest.json') @{version=1;createdAt=[DateTimeOffset]::UtcNow;runtimePath=$RuntimePath;configFolder=$maintConfigs;userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}
    return $backup
}
function Stop-MaintHost {
    $hosts=Get-MaintHosts
    foreach($hostProcess in $hosts) {
        if(-not $hostProcess.ExecutablePath -or -not [string]::Equals($hostProcess.ExecutablePath,$RuntimePath,[StringComparison]::OrdinalIgnoreCase)) { throw 'A different or unverifiable Fan Control instance is running; no process stopped' }
    }
    if($hosts.Count -eq 0) { return }
    if($hosts.Count -eq 1 -and (Test-Path -LiteralPath $maintProbe)) {
        # Direct native Exit RPC; never run FanControl.exe -e against broken IPC,
        # never move the mouse or invoke tray/window controls.
        $previousErrorPreference=$ErrorActionPreference
        try { $ErrorActionPreference='Continue'; & dotnet $maintProbe --native-exit $maintRuntime 2>$null | Out-Null }
        finally { $ErrorActionPreference=$previousErrorPreference }
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while(@(Get-MaintHosts).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    $remaining=Get-MaintHosts
    if($remaining.Count -gt 0 -and -not $Force) { throw 'Native Exit did not finish. Re-run with -Force to terminate only the verified runtime; backup is already saved' }
    foreach($hostProcess in $remaining) {
        if(-not [string]::Equals($hostProcess.ExecutablePath,$RuntimePath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime changed during restart; refused termination' }
        Stop-Process -Id $hostProcess.ProcessId -ErrorAction Stop
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while(@(Get-MaintHosts).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if(@(Get-MaintHosts).Count -gt 0) { throw 'Runtime still running; no DLL or config replaced' }
}
function Start-MaintHost {
    if(@(Get-MaintHosts).Count -gt 0) { return }
    $task=Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if($task) {
        if(@($task.Actions | Where-Object { [string]::Equals($_.Execute,$RuntimePath,[StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) { throw 'Startup task points to a different executable' }
        $deadline=[DateTime]::UtcNow.AddSeconds(5)
        while($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250; $task=Get-ScheduledTask -TaskName $TaskName }
        Start-ScheduledTask -TaskName $TaskName
    } else { Start-Process -FilePath $RuntimePath -ArgumentList '-w' -WorkingDirectory $maintRuntime | Out-Null }
}
function Wait-MaintReady {
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        $receipt=Read-MaintJson (Join-Path $maintData 'diagnostic-snapshot.json')
        if((Is-MaintFresh $receipt) -and $receipt.snapshot.controlReady -and @(Get-MaintHosts).Count -eq 1) {
            $cpu=Read-MaintJson (Join-Path $maintData 'cpu-performance-status.json')
            return @{controlReady=$true;observedProfile=$receipt.snapshot.bridge.observedProfile;mqtt=$receipt.snapshot.mqttConnectionState;cpuFresh=(Is-MaintFresh $cpu);boost=$cpu.boost}
        }
        Start-Sleep -Milliseconds 500
    } while([DateTime]::UtcNow -lt $deadline)
    return @{controlReady=$false;message='Runtime restart completed; fresh control-ready receipt not observed. Run Status for diagnosis'}
}

$maintRestart=$false; $maintBackup=$null; $maintExitCode=0
try {
    switch($Action) {
        'Status' {
            $receipt=Read-MaintJson (Join-Path $maintData 'diagnostic-snapshot.json')
            $cpu=Read-MaintJson (Join-Path $maintData 'cpu-performance-status.json')
            $fresh=Is-MaintFresh $receipt; $cpuFresh=Is-MaintFresh $cpu
            $maintOutput=@{success=$true;action=$Action;processIds=@(Get-MaintHosts | ForEach-Object ProcessId);sampledAt=$receipt.sampledAt;diagnosticsFresh=$fresh;controlReady=($fresh -and $receipt.snapshot.controlReady);cpuFresh=$cpuFresh}
            if($fresh) { $maintOutput.observedProfile=$receipt.snapshot.bridge.observedProfile; $maintOutput.mqtt=$receipt.snapshot.mqttConnectionState }
            if($fresh) {
                $maintOutput.cpuCelsius=$receipt.snapshot.telemetry.cpuPackage.celsius
                $maintOutput.gpuCelsius=$receipt.snapshot.telemetry.gpu.celsius
                $maintOutput.radiatorRpm=$receipt.snapshot.telemetry.waterforce.fanRpm
                $maintOutput.pumpRpm=$receipt.snapshot.telemetry.waterforce.pumpRpm
            }
            if($cpuFresh) { $maintOutput.cpuProfile=$cpu.profile; $maintOutput.boost=$cpu.boost; $maintOutput.maximumCpu=$cpu.maximum; $maintOutput.cpuState=$cpu.state }
        }
        'Backup' { $maintOutput=@{success=$true;action=$Action;backup=(New-MaintBackup)} }
        default {
            if(-not (Test-Path -LiteralPath $RuntimePath)) { throw 'Installed Fan Control runtime unavailable' }
            if($Action -eq 'Install') {
                $sourceDll=Join-Path $maintRoot '.artifacts/mqtt-plugin/FanControl.Mqtt.dll'
                if(-not (Test-Path -LiteralPath $sourceDll)) { throw 'Build the MQTT plugin before installation' }
                if($WithWaterforce -and -not (Test-Path -LiteralPath (Join-Path $maintRoot '.artifacts/waterforce-validated/FanControl.GigabyteWaterforce.dll'))) { throw 'Build the device-specific plugin first' }
            }
            if($Action -eq 'Restore') {
                if(-not $BackupPath) { throw 'Restore needs -BackupPath' }
                $BackupPath=[IO.Path]::GetFullPath($BackupPath)
                $allowed=[IO.Path]::GetFullPath((Join-Path $maintLocal 'backups'))+[IO.Path]::DirectorySeparatorChar
                if(-not $BackupPath.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Restore only accepts backups inside this project maintenance backup folder' }
                $manifest=Read-MaintJson (Join-Path $BackupPath 'manifest.json')
                if($null -eq $manifest -or $manifest.version -ne 1 -or $manifest.userSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or $manifest.runtimePath -ne $RuntimePath -or $manifest.configFolder -ne $maintConfigs) { throw 'Backup belongs to another user/runtime/configuration folder' }
            }
            if($Action -eq 'RepairNightCpu' -and -not $maintSettings.cpuPerformanceControlEnabled) { throw 'Night CPU feature is not enabled in this installation; no policy enabled automatically' }
            $maintBackup=New-MaintBackup
            Stop-MaintHost
            $maintRestart=$true
            # Capture the normal-exit autosave too, without overwriting the pre-exit backup.
            $afterExitBackup=New-MaintBackup
            if($Action -eq 'Install') {
                [IO.Directory]::CreateDirectory((Join-Path $maintRuntime 'Plugins')) | Out-Null
                Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $maintRuntime 'Plugins/FanControl.Mqtt.dll') -Force
                if((Get-FileHash $sourceDll).Hash -ne (Get-FileHash (Join-Path $maintRuntime 'Plugins/FanControl.Mqtt.dll')).Hash) { throw 'Installed MQTT DLL verification failed' }
                if($WithWaterforce) { Copy-Item -LiteralPath (Join-Path $maintRoot '.artifacts/waterforce-validated/FanControl.GigabyteWaterforce.dll') -Destination (Join-Path $maintRuntime 'Plugins/FanControl.GigabyteWaterforce.dll') -Force }
            }
            if($Action -eq 'Restore') {
                foreach($file in @(Get-ChildItem -LiteralPath (Join-Path $BackupPath 'Configurations') -File | Where-Object { $_.Extension -eq '.json' -or $_.Name -eq 'CACHE' })) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $maintConfigs $file.Name) -Force }
                foreach($name in @('FanControl.Mqtt.dll','FanControl.GigabyteWaterforce.dll')) { $file=Join-Path $BackupPath ('Plugins/'+$name); if(Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $maintRuntime ('Plugins/'+$name)) -Force } }
                foreach($name in @('settings.json','mqtt-credentials.dpapi','state.json','cpu-performance-before-night.json','waterforce-control.json','native-mqtt-handover.json')) { $file=Join-Path $BackupPath ('LocalSettings/'+$name); if(Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $maintData $name) -Force } }
            }
            if($Action -eq 'RepairNightCpu') {
                $nightProfile=@($maintSettings.profiles | Where-Object name -eq 'Night')
                if($nightProfile.Count -ne 1) { throw 'Expected one configured Night profile' }
                $night=Read-MaintJson $nightProfile[0].configPath
                $card=@($night.FanControl.Controls | Where-Object Identifier -Like '*/CpuPerformance/Limit')
                if($card.Count -gt 1) { throw 'Ambiguous duplicate CPU limit cards; backup saved' }
                if($card.Count -eq 0) {
                    $template=$null
                    foreach($profile in $maintSettings.profiles) { $config=Read-MaintJson $profile.configPath; $template=$config.FanControl.Controls | Where-Object Identifier -Like '*/CpuPerformance/Limit' | Select-Object -First 1; if($template) { break } }
                    if($null -eq $template) { throw 'No intact CPU card template; native settings left unchanged' }
                    $card=@(($template | ConvertTo-Json -Depth 30) | ConvertFrom-Json)
                    $night.FanControl.Controls=@($night.FanControl.Controls)+$card
                }
                $card[0].Enable=$true; $card[0].IsHidden=$false; $card[0].ManualControl=$true; $card[0].ManualControlValue=100
                $card[0].SelectedFanCurve=$null; $card[0].PairedFanSensor=$null; $card[0].Calibration=@()
                Write-MaintJson $nightProfile[0].configPath $night
            }
            Start-MaintHost
            $maintRestart=$false
            $maintOutput=@{success=$true;action=$Action;backup=$maintBackup;afterExitBackup=$afterExitBackup;verification=(Wait-MaintReady)}
        }
    }
} catch { $maintExitCode=1; $maintOutput=@{success=$false;action=$Action;backup=$maintBackup;error=$_.Exception.Message} }
finally {
    if($maintRestart) { try { Start-MaintHost } catch { $maintOutput.restartError=$_.Exception.Message } }
}
if($ResultPath) { Write-MaintJson ([IO.Path]::GetFullPath($ResultPath)) $maintOutput }
$maintOutput | ConvertTo-Json -Depth 8
exit $maintExitCode
