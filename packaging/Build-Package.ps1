#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$')][string]$Stamp,
    [Parameter(Mandatory)][string]$RuntimeSource,
    [Parameter(Mandatory)][string]$IsccPath,
    [Parameter(Mandatory)][string]$ChineseMessages,
    [string]$OutputRoot = '',
    [string]$NugetCacheSource = '',
    [string]$DotnetRuntimeVersion = '10.0.5'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $source 'outputs\packages' }

function PlainDirectory([string]$path) {
    $full = (Resolve-Path -LiteralPath $path -ErrorAction Stop).ProviderPath
    for($part=[IO.DirectoryInfo]::new($full);$null -ne $part;$part=$part.Parent) {
        if($part.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Links are not accepted: $($part.FullName)"}
    }
    if(-not (Test-Path -LiteralPath $full -PathType Container)){throw "Not a directory: $full"}
    return [IO.Path]::GetFullPath($full)
}
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function CopyTree([string]$from,[string]$to) {
    $from = PlainDirectory $from
    if(Get-ChildItem -LiteralPath $from -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1){throw 'Input tree contains links'}
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    & robocopy $from $to /E /XD bin obj __pycache__ node_modules /XF *.pdb *.lock *.log /NFL /NDL /NJH /NJS /NP | Out-Null
    if($LASTEXITCODE -ge 8){throw "Copy failed: $from"}
    $global:LASTEXITCODE=0
}
function Execute([string]$name,[string[]]$arguments,[string]$log) {
    & $name @arguments *> $log
    if($LASTEXITCODE -ne 0){throw "Command failed ($LASTEXITCODE); see $log"}
}
function FileMap([string]$root) {
    $map=[ordered]@{}
    foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName) {
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        $map[$relative]=Sha $file.FullName
    }
    return $map
}

$source=PlainDirectory $source
$RuntimeSource=PlainDirectory $RuntimeSource
foreach($file in @($IsccPath,$ChineseMessages)) { if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing input: $file"} }
$IsccPath=(Resolve-Path -LiteralPath $IsccPath).ProviderPath
$ChineseMessages=(Resolve-Path -LiteralPath $ChineseMessages).ProviderPath
foreach($relative in @('python\python.exe','python\python312._pth','node\node.exe',"dotnet\host\fxr\$DotnetRuntimeVersion\hostfxr.dll", "dotnet\shared\Microsoft.NETCore.App\$DotnetRuntimeVersion\coreclr.dll")) {
    if(-not(Test-Path -LiteralPath (Join-Path $RuntimeSource $relative))){throw "Runtime input missing: $relative"}
}
$signature=Get-AuthenticodeSignature (Join-Path $RuntimeSource 'node\node.exe')
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'OpenJS Foundation'){throw 'Node signature is not valid/OpenJS'}
$nodeVersion=(& (Join-Path $RuntimeSource 'node\node.exe') --version).Trim()
if($nodeVersion -ne 'v24.10.0'){throw 'This release notice set is pinned to Node 24.10.0; update notices before changing it.'}
$pythonVersion=(& (Join-Path $RuntimeSource 'python\python.exe') -I -S --version).Trim()
if($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch '^Python 3\.12\.'){throw 'Expected a Python 3.12 embedded runtime'}
$runtimeHashes=FileMap $RuntimeSource

Push-Location $source
try {
    $commit=(& git rev-parse HEAD).Trim(); if($LASTEXITCODE -ne 0){throw 'Source must be a Git checkout'}
    if(@(& git status --porcelain).Count -gt 0){throw 'Commit or isolate source changes before packaging'}
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    $OutputRoot=PlainDirectory $OutputRoot
    $build=Join-Path $OutputRoot "build-$Stamp"
    if(Test-Path -LiteralPath $build){throw 'Build folder already exists; choose a new stamp'}
    New-Item -ItemType Directory -Path $build | Out-Null
    $snapshot=Join-Path $build 'source'
    $archive=Join-Path $build 'source.zip'
    Execute 'git' @('archive','--format=zip',"--output=$archive",$commit) (Join-Path $build 'archive.log')
    Expand-Archive -LiteralPath $archive -DestinationPath $snapshot
    $sourceHashes=FileMap $snapshot
    $stage=Join-Path $build 'CadReview'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $env:DOTNET_CLI_HOME=Join-Path $build 'dotnet-home'
    $env:NUGET_PACKAGES=Join-Path $build 'nuget-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    if($NugetCacheSource){CopyTree $NugetCacheSource $env:NUGET_PACKAGES}
    $engine=Join-Path $snapshot 'work\segmented-20261109'
    $projects=@(@('native-source\CADtest.csproj','native',$true),@('projector\Projector.csproj','projector\bin\Release\net10.0',$false),@('review-source\CadReviewWorkflow.csproj','review-native',$true))
    Push-Location $snapshot
    try {
        foreach($project in $projects) {
            $path=Join-Path $engine $project[0]
            $label=($project[0] -split '\\')[0]
            $restoreArguments=@('restore',$path,'--configfile',(Join-Path $snapshot 'NuGet.Config'))
            if($project[2]){$restoreArguments+='--locked-mode'}
            Execute 'dotnet' $restoreArguments (Join-Path $build "$label-restore.log")
            Execute 'dotnet' @('build',$path,'-c','Release','--no-restore','-p:DebugType=none','-p:DebugSymbols=false','-o',(Join-Path $engine $project[1])) (Join-Path $build "$label-build.log")
        }
        $ui=Join-Path $snapshot 'ui\CadReviewUI.csproj'
        $publishLock=Join-Path $build 'ui-publish.lock.json'
        $properties=@('-p:RuntimeIdentifier=win-x64','-p:SelfContained=true',"-p:RuntimeFrameworkVersion=$DotnetRuntimeVersion",'-p:TargetLatestRuntimePatch=false',"-p:NuGetLockFilePath=$publishLock")
        Execute 'dotnet' (@('restore',$ui,'--configfile',(Join-Path $snapshot 'NuGet.Config'))+$properties) (Join-Path $build 'ui-restore.log')
        $published=Join-Path $build 'ui-publish'
        Execute 'dotnet' (@('publish',$ui,'-c','Release','--no-restore','-p:PublishSingleFile=true','-p:EnableCompressionInSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=none','-p:DebugSymbols=false','-o',$published)+$properties) (Join-Path $build 'ui-publish.log')
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $published 'CadReviewUI.exe') -Destination $stage
    CopyTree (Join-Path $published 'templates') (Join-Path $stage 'templates')
    $targetEngine=Join-Path $stage 'work\segmented-20261109'
    New-Item -ItemType Directory -Path $targetEngine | Out-Null
    foreach($name in @('runner.py','parallel_runner.py','review_guard.py','prepare_review.py','workspace_paths.py','python-gate.py','node-gate.mjs','extract-requirement-frames.mjs','independent_projection_audit.py','RunAccuracy.ps1','README.md')) {
        Copy-Item -LiteralPath (Join-Path $engine $name) -Destination $targetEngine
    }
    foreach($name in @('batch-fix-20261106','polish-20261106','native-source','review-source','native','review-native')) {CopyTree (Join-Path $engine $name) (Join-Path $targetEngine $name)}
    New-Item -ItemType Directory -Force -Path (Join-Path $targetEngine 'projector') | Out-Null
    Copy-Item -LiteralPath (Join-Path $engine 'projector\Program.cs'),(Join-Path $engine 'projector\Projector.csproj') -Destination (Join-Path $targetEngine 'projector')
    CopyTree (Join-Path $engine 'projector\bin\Release\net10.0') (Join-Path $targetEngine 'projector\bin\Release\net10.0')
    CopyTree $RuntimeSource (Join-Path $stage 'runtime')
    $pth=Join-Path $stage 'runtime\python\python312._pth'
    $lines=@([IO.File]::ReadAllLines($pth) | Where-Object {$_ -notmatch 'work[\\/]segmented-20261109'})
    [IO.File]::WriteAllLines($pth,($lines+@('..\..\work\segmented-20261109')),[Text.UTF8Encoding]::new($false))
    CopyTree (Join-Path $snapshot 'packaging\licenses') (Join-Path $stage 'licenses')
    $oldNotices=Join-Path (Split-Path $RuntimeSource -Parent) 'licenses'
    foreach($name in @('dotnet-LICENSE.txt','dotnet-ThirdPartyNotices.txt','python-LICENSE.txt')) {
        $notice=Join-Path $oldNotices $name
        if(-not(Test-Path -LiteralPath $notice)){throw "Runtime copyright notice missing: $name"}
        Copy-Item -LiteralPath $notice -Destination (Join-Path $stage 'licenses')
    }
    Copy-Item -LiteralPath (Join-Path $snapshot 'LICENSE'),(Join-Path $snapshot 'THIRD_PARTY_NOTICES.md'),(Join-Path $snapshot 'global.json') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $snapshot 'packaging\使用说明.txt') -Destination $stage
    New-Item -ItemType Directory -Path (Join-Path $stage 'outputs') | Out-Null
    foreach($name in $sourceHashes.Keys) {if((Sha (Join-Path $snapshot $name)) -ne $sourceHashes[$name]){throw "Source changed during build: $name"}}
    if((FileMap $RuntimeSource | ConvertTo-Json -Compress) -ne ($runtimeHashes | ConvertTo-Json -Compress)){throw 'Runtime source changed during build'}
    $contract=[regex]::Match([IO.File]::ReadAllText((Join-Path $targetEngine 'runner.py')),"EXTRACTOR_CONTRACT_SHA256\s*=\s*'([A-F0-9]{64})'").Groups[1].Value
    if((Sha (Join-Path $targetEngine 'extract-requirement-frames.mjs')) -ne $contract){throw 'Extractor contract mismatch'}
    $info=[ordered]@{stamp=$Stamp;built_at_utc=[DateTime]::UtcNow.ToString('o');source_commit=$commit;source_files=$sourceHashes;runtime_source_files=$runtimeHashes;python_version=$pythonVersion;node_version=$nodeVersion;dotnet_version=$DotnetRuntimeVersion;ui_sha256=(Sha (Join-Path $stage 'CadReviewUI.exe'));publish_lock_sha256=(Sha $publishLock);extractor_contract=$contract}
    $info | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'PACKAGE-INFO.json') -Encoding utf8
    $payload=FileMap $stage
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $build 'payload-manifest.json') -Encoding utf8
    $zip=Join-Path $build "CadReview-$Stamp.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$true)
    $iss=[IO.File]::ReadAllText((Join-Path $snapshot 'packaging\CadReview.iss.in'))
    $iss=$iss.Replace('@STAMP@',$Stamp).Replace('@OUTPUT@',$build).Replace('@PAYLOAD@',$stage).Replace('@LANGUAGE@',$ChineseMessages)
    $issPath=Join-Path $build 'CadReview.iss'
    [IO.File]::WriteAllText($issPath,$iss,[Text.UTF8Encoding]::new($true))
    Execute $IsccPath @('/Q',$issPath) (Join-Path $build 'installer-build.log')
    $setup=Join-Path $build "CadReview-Setup-$Stamp.exe"
    $summary=[ordered]@{status='built-not-yet-accepted';stamp=$Stamp;source_commit=$commit;stage=$stage;zip=$zip;zip_sha256=(Sha $zip);setup=$setup;setup_sha256=(Sha $setup);files=$payload.Count}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $build 'build-result.json') -Encoding utf8
    $summary | ConvertTo-Json
} finally { Pop-Location }
