[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Recognize','Validate','Export')][string]$Mode,
    [string[]]$Drawing,
    [string]$Output,
    [string]$Manifest,
    [string]$Review,
    [string]$Reviewer,
    [double]$MemoryMiB=3072,
    [double]$TimeoutSeconds=300,
    [double]$CpuPercent=25,
    [string]$Python='C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1104-1290-0_26.1104.1290.0_x64__3k8sg7r9htsxt\dependencies\python\python.exe',
    [string]$Node='C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1104-1290-0_26.1104.1290.0_x64__3k8sg7r9htsxt\dependencies\node\bin\node.exe'
)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Python -PathType Leaf)){throw 'Python runtime missing.'}
$culture=[Globalization.CultureInfo]::InvariantCulture
$limits=@('--memory-mib',$MemoryMiB.ToString($culture),'--timeout',$TimeoutSeconds.ToString($culture),'--cpu-percent',$CpuPercent.ToString($culture))
if($Mode -eq 'Recognize'){
    if(-not $Drawing -or -not $Output){throw 'Recognize requires -Drawing and a new -Output directory.'}
    if(-not(Test-Path -LiteralPath $Node -PathType Leaf)){throw 'Node runtime missing.'}
    $cadOut=[IO.Path]::GetFullPath($Output)
    $cadOutputs=[IO.Path]::GetFullPath('D:\CADtest\outputs')+'\'
    if(-not $cadOut.StartsWith($cadOutputs,[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $cadOut)){throw 'Use a new directory below D:\CADtest\outputs.'}
    New-Item -ItemType Directory -Path $cadOut | Out-Null
    & $Python -B -X utf8 (Join-Path $PSScriptRoot 'runner.py') --output (Join-Path $cadOut 'recognition') --node $Node --prepare-review @limits --drawing @Drawing
    if($LASTEXITCODE -ne 0){throw 'Processing did not complete. Inspect recognition/batch-status.json and review-status.json; existing output was retained.'}
    exit 0
}
if(-not $Manifest -or -not $Review){throw 'Validate/Export require -Manifest and -Review.'}
$cadArgs=@($Mode.ToLowerInvariant(),'--manifest',$Manifest,'--review',$Review)+$limits
if($Mode -eq 'Export'){
    if(-not $Output -or -not $Reviewer){throw 'Export requires -Output and -Reviewer.'}
    $cadArgs+=@('--output',$Output,'--reviewer',$Reviewer)
}
& $Python -B -X utf8 (Join-Path $PSScriptRoot 'review_guard.py') @cadArgs
$cadExit=$LASTEXITCODE
if($cadExit -eq 2){Write-Warning '还有待核项目或校验问题；未生成正式确认明细。'}
elseif($cadExit -ne 0){throw 'Review workflow failed; inspect the recorded reason.'}
exit $cadExit
