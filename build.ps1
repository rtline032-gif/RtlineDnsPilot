param(
    [string]$Destination = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$Windows7,
    [string]$LegacySdkDirectory
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = '1'
$taskFlags = @()
$taskFramework = 'net10.0-windows'
if ($Windows7) {
    $taskFramework = 'net6.0-windows'
    $taskFlags += '-p:LegacyWindows=true'
    if ($LegacySdkDirectory) {
        $taskLegacyRoot = [IO.Path]::GetFullPath($LegacySdkDirectory)
        if (-not (Test-Path -LiteralPath (Join-Path $taskLegacyRoot 'packs\Microsoft.WindowsDesktop.App.Ref\6.0.36'))) { throw 'The official .NET SDK 6.0.428 targeting packs are required.' }
        $taskFlags += ('-p:NetCoreTargetingPackRoot=' + (Join-Path $taskLegacyRoot 'packs'))
    }
}
& dotnet build (Join-Path $PSScriptRoot 'NetPilot.csproj') -c Release --ignore-failed-sources --disable-build-servers -p:DebugType=None -p:DebugSymbols=false @taskFlags
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
& dotnet build (Join-Path $PSScriptRoot 'Tests\NetPilot.Tests.csproj') -c Release --ignore-failed-sources --disable-build-servers @taskFlags
if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
$taskRunner = 'dotnet'
if ($Windows7 -and $LegacySdkDirectory) {
    $taskRunner = Join-Path $taskLegacyRoot 'dotnet.exe'
    $env:DOTNET_ROOT = $taskLegacyRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}
& $taskRunner (Join-Path $PSScriptRoot ('Tests\bin\Release\' + $taskFramework + '\NetPilot.Tests.dll'))
if ($LASTEXITCODE -ne 0) { throw 'Verification failed. No release package was copied.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
foreach ($taskFile in @('RtlineDnsPilot.exe','RtlineDnsPilot.dll','RtlineDnsPilot.deps.json','RtlineDnsPilot.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('bin\Release\' + $taskFramework + '\' + $taskFile)) -Destination (Join-Path $Destination $taskFile) -Force
}
Write-Output ('Verified application package: ' + $Destination)
