[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$')][string]$Version
)
$ErrorActionPreference='Stop'
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $releaseRoot
$releaseOutput=Join-Path $releaseRoot '.artifacts/releases'
$releaseWork=Join-Path $releaseOutput ([Guid]::NewGuid().ToString('N'))
$releaseBundle=Join-Path $releaseWork 'bundle'
New-Item -ItemType Directory -Path (Join-Path $releaseBundle 'Plugins'),(Join-Path $releaseBundle 'docs') -Force | Out-Null
# Rebuild without debug symbols or machine-specific source paths. Never package
# an entire runtime/publish directory: it may contain local configuration files.
$releaseProperties=@('-p:DebugType=None','-p:DebugSymbols=false',"-p:PathMap=$releaseRoot=/_/src","-p:Version=$Version")
dotnet build FanControlIntegration.slnx -c Release -t:Rebuild @releaseProperties
if($LASTEXITCODE -ne 0) { throw 'Release build failed' }
foreach($project in @('tests/FanControlBridge.Tests','tests/Waterforce.Tests')) {
    dotnet run --project $project -c Release --no-build
    if($LASTEXITCODE -ne 0) { throw "Release tests failed: $project" }
}
foreach($project in @('src/FanControl.Mqtt','third_party/Waterforce')) {
    $destination=Join-Path $releaseWork ([IO.Path]::GetFileName($project))
    dotnet publish $project -c Release --no-build -o $destination @releaseProperties
    if($LASTEXITCODE -ne 0) { throw "Release publish failed: $project" }
}
Copy-Item (Join-Path $releaseWork 'FanControl.Mqtt/FanControl.Mqtt.dll') (Join-Path $releaseBundle 'Plugins/FanControl.Mqtt.dll')
Copy-Item (Join-Path $releaseWork 'Waterforce/FanControl.GigabyteWaterforce.dll') (Join-Path $releaseBundle 'Plugins/FanControl.GigabyteWaterforce.dll')
foreach($name in @('INSTALL.md','MQTT.md','MODES.md','CPU-PERFORMANCE.md','HARDWARE.md','OPERATIONS.md','DIAGNOSTICS.md','THIRD_PARTY.md','MAINTENANCE.md')) {
    Copy-Item (Join-Path $releaseRoot "docs/$name") (Join-Path $releaseBundle "docs/$name")
}
Copy-Item (Join-Path $releaseRoot 'third_party/Waterforce/README.md') (Join-Path $releaseBundle 'docs/WATERFORCE-UPSTREAM.md')
if(Test-Path (Join-Path $releaseRoot 'LICENSE')) { Copy-Item (Join-Path $releaseRoot 'LICENSE') (Join-Path $releaseBundle 'LICENSE') }
Copy-Item (Join-Path $releaseRoot 'docs/licenses') (Join-Path $releaseBundle 'docs/licenses') -Recurse
$releaseInstructions=@'
Fan Control AORUS + MQTT plugin bundle

For Fan Control V282, .NET 10 edition, on Windows.
Exit Fan Control normally and back up your existing Plugins and Configurations.
Copy the two DLLs from Plugins into the existing Fan Control Plugins directory,
then start Fan Control again. Fan Control supplies HidSharp and its plugin SDK;
this archive does not redistribute Fan Control or its host dependencies.
Your curves, calibration, tray icons, credentials and device opt-in are local.
This archive contains none of them and does not replace any configuration.

MQTT setup and the credential helper: docs/INSTALL.md and docs/MQTT.md.
The credential helper and optional maintenance scripts are supplied as source
in the repository; they are not included as executables in this plugin bundle.
Waterforce radiator control is opt-in after device validation. Pump writes are
excluded. Optional Night CPU performance control is disabled by default.
Read docs/HARDWARE.md, docs/OPERATIONS.md and docs/THIRD_PARTY.md.
'@
[IO.File]::WriteAllText((Join-Path $releaseBundle 'INSTALL.txt'),$releaseInstructions,[Text.UTF8Encoding]::new($false))
$releaseZip=Join-Path $releaseOutput "FanControl.AorusMqtt-$Version-win-x64.zip"
if(Test-Path $releaseZip) { throw 'Versioned release archive already exists; inspect it rather than overwrite it' }
Compress-Archive -Path (Join-Path $releaseBundle '*') -DestinationPath $releaseZip
$releaseHashes=@($releaseZip,(Join-Path $releaseBundle 'Plugins/FanControl.Mqtt.dll'),(Join-Path $releaseBundle 'Plugins/FanControl.GigabyteWaterforce.dll')) | ForEach-Object {
    $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
}
[IO.File]::WriteAllLines((Join-Path $releaseOutput "SHA256SUMS-$Version.txt"),[string[]]$releaseHashes,[Text.UTF8Encoding]::new($false))
Write-Output $releaseZip
Write-Output (Join-Path $releaseBundle 'Plugins')
