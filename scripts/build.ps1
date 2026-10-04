$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet build FanControlIntegration.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet run --project tests/FanControlBridge.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet run --project tests/Waterforce.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Waterforce tests failed' }
dotnet publish src/FanControl.Mqtt -c Release -o .artifacts/mqtt-plugin
if ($LASTEXITCODE -ne 0) { throw 'MQTT plugin publish failed' }
dotnet publish third_party/Waterforce -c Release -o .artifacts/waterforce-validated
if ($LASTEXITCODE -ne 0) { throw 'Waterforce plugin publish failed' }
