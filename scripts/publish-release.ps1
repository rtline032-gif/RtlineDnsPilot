param(
    [ValidateSet('Package','Publish')][string]$Mode = 'Package',
    [Parameter(Mandatory)][string]$ModernDirectory,
    [Parameter(Mandatory)][string]$LegacyDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$SourceManifest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRepository = 'rtline032-gif/RtlineDnsPilot'
$taskSource = [IO.Path]::GetFullPath($SourceRoot)
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskConfig = Get-Content -LiteralPath (Join-Path $taskSource 'release.json') -Raw | ConvertFrom-Json
$taskVersionText = [string]$taskConfig.version
if ($taskVersionText -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$taskVersion = [version]$taskVersionText
$taskVersion4 = [version]::new($taskVersion.Major,$taskVersion.Minor,$taskVersion.Build,0)
$taskTag = 'v' + $taskVersionText
if ([string]$taskConfig.notes -ne ('release-notes-' + $taskVersionText + '.md')) { throw 'Unexpected release notes path.' }
$taskNotes = Join-Path $taskSource $taskConfig.notes
if (-not (Test-Path -LiteralPath $taskNotes -PathType Leaf)) { throw 'Release notes are missing.' }
if (([IO.File]::ReadAllText((Join-Path $taskSource 'AppVersion.cs'))) -notmatch ('Current\s*=\s*"' + [regex]::Escape($taskVersionText) + '"')) { throw 'Release config and source versions differ.' }
if (Test-Path -LiteralPath $taskOutput) { throw 'Output directory already exists; preserving earlier packages.' }
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskStage = Join-Path $taskOutput 'stage'
$taskRuntimeFiles = @('RtlineDnsPilot.exe','RtlineDnsPilot.dll','RtlineDnsPilot.deps.json','RtlineDnsPilot.runtimeconfig.json')
$taskPublicFiles = @('profiles.json','CHANNELS.txt','README.txt','LICENSE','PRIVACY.md','CODE_SIGNING.md')
$taskProfiles = @(Get-Content -LiteralPath (Join-Path $taskSource 'profiles.json') -Raw | ConvertFrom-Json)
if ($taskProfiles.Count -ne 26) { throw 'The public import must contain all 26 known profiles.' }
$taskProfileFields = @('Id','Name','Enabled','Mode','Servers','DnsPort','DohUrl','Note')
foreach ($taskProfile in $taskProfiles) {
    if (@($taskProfile.PSObject.Properties.Name | Where-Object { $_ -notin $taskProfileFields }).Count) { throw 'Unexpected field in the public DNS catalog.' }
    $taskParsedId=[guid]::Empty
    if (-not [guid]::TryParse($taskProfile.Id,[ref]$taskParsedId)) { throw 'Invalid public profile identity.' }
    foreach ($taskServer in $taskProfile.Servers) {
        $taskParsedAddress=[Net.IPAddress]::None
        if (-not [Net.IPAddress]::TryParse($taskServer,[ref]$taskParsedAddress)) { throw 'Invalid public DNS address.' }
    }
}
function Write-Hashes([string]$Directory) {
    $taskLines = Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/')
    }
    [IO.File]::WriteAllLines((Join-Path $Directory 'SHA256SUMS.txt'),[string[]]$taskLines,[Text.UTF8Encoding]::new($false))
}
$taskAssets = @()
foreach ($taskSpec in @(@('win-x64',$ModernDirectory,10),@('win7-x64',$LegacyDirectory,6))) {
    $taskInput = [IO.Path]::GetFullPath([string]$taskSpec[1])
    $taskPackage = Join-Path $taskStage ($taskSpec[0] + '\RtlineDnsPilot')
    New-Item -ItemType Directory -Path $taskPackage -Force | Out-Null
    foreach ($taskFile in $taskRuntimeFiles) { Copy-Item -LiteralPath (Join-Path $taskInput $taskFile) -Destination $taskPackage }
    $taskAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $taskPackage 'RtlineDnsPilot.dll'))
    if ($taskAssembly.Name -ne 'RtlineDnsPilot' -or $taskAssembly.Version -ne $taskVersion4) { throw 'Unexpected managed runtime package version.' }
    $taskRuntime = Get-Content -LiteralPath (Join-Path $taskPackage 'RtlineDnsPilot.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($taskRuntime.runtimeOptions.tfm -ne ('net' + $taskSpec[2] + '.0') -or @($taskRuntime.runtimeOptions.frameworks).Count -ne 2) { throw 'Unexpected runtime flavor.' }
    foreach ($taskFramework in $taskRuntime.runtimeOptions.frameworks) {
        if ($taskFramework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') -or ([version]$taskFramework.version).Major -ne $taskSpec[2]) { throw 'Unexpected framework dependency.' }
    }
    $taskDeps = Get-Content -LiteralPath (Join-Path $taskPackage 'RtlineDnsPilot.deps.json') -Raw | ConvertFrom-Json
    if ($taskDeps.runtimeTarget.name -ne ('.NETCoreApp,Version=v' + $taskSpec[2] + '.0') -or ('RtlineDnsPilot/' + $taskVersionText) -notin $taskDeps.libraries.PSObject.Properties.Name) { throw 'Dependency map does not match the runtime flavor/version.' }
    foreach ($taskFile in $taskPublicFiles) { Copy-Item -LiteralPath (Join-Path $taskSource $taskFile) -Destination $taskPackage }
    Write-Hashes $taskPackage
    if (@(Get-ChildItem -LiteralPath $taskPackage -File).Count -ne 11) { throw 'Runtime archive must contain exactly 11 files.' }
    $taskZip = Join-Path $taskOutput ('RtlineDnsPilot-' + $taskVersionText + '-' + $taskSpec[0] + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory((Split-Path -Parent $taskPackage),$taskZip)
    $taskAssets += $taskZip
}
$taskSourcePackage = Join-Path $taskStage 'source\RtlineDnsPilot'
New-Item -ItemType Directory -Path $taskSourcePackage -Force | Out-Null
if ($SourceManifest) { $taskTracked = @(Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json) }
else {
    $taskTracked = @(& git -C $taskSource ls-files)
    if ($LASTEXITCODE -ne 0 -or $taskTracked.Count -eq 0) { throw 'A tracked public source tree is required.' }
}
foreach ($taskRelative in $taskTracked) {
    if ($taskRelative -eq 'SHA256SUMS.txt') { continue }
    if ($taskRelative -match '(^|/)\.\.?(/|$)|\\|^/') { throw 'Source paths must be relative Git paths without traversal.' }
    if ($taskRelative -match '(^|/)(\.git|bin|obj|\.build|test-data)/|(^|/)(ui-config|dns-session|statistics|events|install|chart-history)([.-]|$)|\.(pdb|log|png|zip|dpapi)$' -or $taskRelative -notmatch '(^\.github/workflows/[^/]+\.ya?ml$|^[\w./-]+\.(cs|csproj|manifest|ps1|md|txt|json|gs|cjs)$|^LICENSE$|^\.gitignore$)') { throw ('Unexpected public source file: ' + $taskRelative) }
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskSource $taskRelative))
    if (-not $taskPath.StartsWith($taskSource + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $taskPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid source path.' }
    $taskDestination = Join-Path $taskSourcePackage $taskRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath $taskPath -Destination $taskDestination
}
Write-Hashes $taskSourcePackage
$taskSourceZip = Join-Path $taskOutput ('RtlineDnsPilot-' + $taskVersionText + '-source.zip')
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path -Parent $taskSourcePackage),$taskSourceZip)
$taskAssets += $taskSourceZip
$taskCatalogAsset = Join-Path $taskOutput ('RtlineDnsPilot-' + $taskVersionText + '-all-known-dns.json')
Copy-Item -LiteralPath (Join-Path $taskSource 'profiles.json') -Destination $taskCatalogAsset
$taskAssets += $taskCatalogAsset
$taskRecords = @($taskAssets | ForEach-Object { $taskItem=Get-Item -LiteralPath $_; [pscustomobject]@{name=$taskItem.Name;size=$taskItem.Length;digest=('sha256:'+(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())} })
$taskRecords | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'package-receipt.json') -Encoding utf8
[IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'),[string[]]($taskRecords | ForEach-Object { $_.digest.Substring(7) + '  ' + $_.name }),[Text.UTF8Encoding]::new($false))
$taskAssets += Join-Path $taskOutput 'SHA256SUMS.txt'
if ($Mode -eq 'Package') { Write-Output ('Prepared ' + $taskRecords.Count + ' public release assets in ' + $taskOutput); return }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne $taskRepository -or $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -notin @('push','workflow_dispatch') -or $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or -not $env:GH_TOKEN) { throw 'Publishing is restricted to the approved main-branch GitHub Actions run.' }
$taskApiHeaders = @{Authorization=('Bearer ' + $env:GH_TOKEN);Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28';'User-Agent'='RtlineDnsPilot-release'}
function Invoke-GitHub([string]$Method,[string]$Path,$Body=$null) {
    if (-not $Path.StartsWith(('repos/' + $taskRepository + '/'),[StringComparison]::Ordinal)) { throw 'Unexpected GitHub API destination.' }
    $taskArgs = @{Method=$Method;Uri=('https://api.github.com/' + $Path);Headers=$taskApiHeaders;TimeoutSec=60}
    if ($null -ne $Body) { $taskArgs.Body=($Body|ConvertTo-Json -Depth 10 -Compress);$taskArgs.ContentType='application/json; charset=utf-8' }
    Invoke-RestMethod @taskArgs
}
$taskExistingRelease=$null
try { $taskExistingRelease=Invoke-GitHub 'GET' ('repos/' + $taskRepository + '/releases/tags/' + $taskTag) }
catch [Microsoft.PowerShell.Commands.HttpResponseException] { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if ($null -ne $taskExistingRelease) { throw 'This release already exists; no release or asset is overwritten.' }
$taskExistingTag=$null
try { $taskExistingTag=Invoke-GitHub 'GET' ('repos/' + $taskRepository + '/git/ref/tags/' + $taskTag) }
catch [Microsoft.PowerShell.Commands.HttpResponseException] { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
if ($null -ne $taskExistingTag) { throw 'This tag already exists; no tag is reused or overwritten.' }
$taskMetadataPath = 'repos/' + $taskRepository + '/contents/updates.json'
$taskMetadata = Invoke-GitHub 'GET' ($taskMetadataPath + '?ref=main')
$taskPreviousJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($taskMetadata.content))
$taskPrevious = @($taskPreviousJson | ConvertFrom-Json)
foreach ($taskEntry in $taskPrevious) {
    if ($taskEntry.tag_name -match '^v?(\d+\.\d+\.\d+)$' -and [version]$Matches[1] -ge $taskVersion) { throw 'Update metadata already contains this or a newer release; refusing a downgrade or overwrite.' }
}
$taskGhArguments=@('release','create',$taskTag)+$taskAssets+@('--repo',$taskRepository,'--draft','--target',$env:GITHUB_SHA,'--title',[string]$taskConfig.title,'--notes-file',$taskNotes)
if ([bool]$taskConfig.prerelease) { $taskGhArguments+='--prerelease' }
& gh @taskGhArguments
if ($LASTEXITCODE -ne 0) { throw 'Draft release upload failed. Existing update metadata was retained.' }
$taskRelease = Invoke-GitHub 'GET' ('repos/' + $taskRepository + '/releases/tags/' + $taskTag)
if (-not $taskRelease.draft -or $taskRelease.target_commitish -ne $env:GITHUB_SHA) { throw 'Unexpected draft release state or target.' }
$taskSumsItem=Get-Item -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')
$taskExpected = @($taskRecords) + @([pscustomobject]@{name=$taskSumsItem.Name;size=$taskSumsItem.Length;digest=('sha256:'+(Get-FileHash -LiteralPath $taskSumsItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant())})
if (@($taskRelease.assets).Count -ne $taskExpected.Count) { throw 'Uploaded draft asset count differs.' }
foreach ($taskExpectedAsset in $taskExpected) {
    $taskUploaded = @($taskRelease.assets | Where-Object name -eq $taskExpectedAsset.name)
    if ($taskUploaded.Count -ne 1 -or $taskUploaded[0].size -ne $taskExpectedAsset.size -or $taskUploaded[0].digest -ne $taskExpectedAsset.digest -or $taskUploaded[0].state -ne 'uploaded') { throw ('Uploaded asset verification failed: ' + $taskExpectedAsset.name) }
}
& gh release edit $taskTag --repo $taskRepository --draft=false --latest=false
if ($LASTEXITCODE -ne 0) { throw 'Publishing the verified draft failed; update metadata was retained.' }
$taskRelease = Invoke-GitHub 'GET' ('repos/' + $taskRepository + '/releases/tags/' + $taskTag)
if ($taskRelease.draft -or -not $taskRelease.published_at) { throw 'Release publication was not confirmed.' }
$taskModernName='RtlineDnsPilot-'+$taskVersionText+'-win-x64.zip'
$taskLegacyName='RtlineDnsPilot-'+$taskVersionText+'-win7-x64.zip'
$taskRuntimeAssets = @($taskRelease.assets | Where-Object { $_.name -in @($taskModernName,$taskLegacyName) } | ForEach-Object {
    [ordered]@{name=$_.name;browser_download_url=$_.browser_download_url;size=$_.size;digest=$_.digest;state=$_.state}
})
if ($taskRuntimeAssets.Count -ne 2) { throw 'Both published Windows runtime assets are required.' }
$taskNew = [ordered]@{tag_name=$taskRelease.tag_name;html_url=$taskRelease.html_url;draft=$false;prerelease=[bool]$taskRelease.prerelease;published_at=$taskRelease.published_at;assets=$taskRuntimeAssets}
$taskManifestJson = ConvertTo-Json -InputObject (@($taskNew) + @($taskPrevious | Select-Object -First 19)) -Depth 8
$taskCurrentMetadata = Invoke-GitHub 'GET' ($taskMetadataPath + '?ref=main')
if ($taskCurrentMetadata.sha -ne $taskMetadata.sha) { throw 'Release is published, but updates.json changed concurrently; no metadata was overwritten.' }
$taskPromoted = Invoke-GitHub 'PUT' $taskMetadataPath @{message=('Publish verified RtlineDnsPilot ' + $taskVersionText + ' update metadata');branch='main';sha=$taskMetadata.sha;content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($taskManifestJson))}
$taskReadback = Invoke-GitHub 'GET' ($taskMetadataPath + '?ref=main')
$taskReadbackText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($taskReadback.content))
if ($taskReadbackText -ne $taskManifestJson) { throw 'Update metadata readback did not match the verified manifest.' }
@{version=$taskVersionText;release=$taskRelease.html_url;source_commit=$env:GITHUB_SHA;metadata_commit=$taskPromoted.commit.sha;assets=$taskExpected;metadata_promoted_after_verified_publication=$true} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $taskOutput 'publication-receipt.json') -Encoding utf8
Write-Output ('Published verified release and update metadata: ' + $taskRelease.html_url)
