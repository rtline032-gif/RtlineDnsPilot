param([string]$Destination = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = '1'
& dotnet build (Join-Path $PSScriptRoot 'NetPilot.csproj') -c Release --ignore-failed-sources --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& dotnet run --project (Join-Path $PSScriptRoot 'Tests\NetPilot.Tests.csproj') -c Release --ignore-failed-sources --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Verification failed. No release package was copied.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($taskFile in @('RtlineDnsPilot.exe','RtlineDnsPilot.dll','RtlineDnsPilot.deps.json','RtlineDnsPilot.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\Release\net10.0-windows\' + $taskFile)) -Destination (Join-Path $Destination $taskFile) -Force
}
Write-Output ('Verified application package: ' + $Destination)
