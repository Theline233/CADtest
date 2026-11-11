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
    [string]$Python=$env:CADTEST_PYTHON,
    [string]$Node=$env:CADTEST_NODE
)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'CADtest requires Windows Job Objects; use Windows.' }
$root = if ($env:CADTEST_ROOT) { $env:CADTEST_ROOT } else { Join-Path $PSScriptRoot '..\..' }
if ($env:CADTEST_ROOT -and -not [IO.Path]::IsPathFullyQualified($root)) { throw 'CADTEST_ROOT must be absolute.' }
$root = (Resolve-Path -LiteralPath $root -ErrorAction Stop).ProviderPath
if (-not $Python) {
    $bundled = Join-Path $root 'runtime\python\python.exe'
    if (Test-Path -LiteralPath $bundled -PathType Leaf) { $Python = $bundled }
    else {
        foreach ($name in @('python.exe','python3.exe')) {
            $found = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($found -and $found.Source -notmatch '\\Microsoft\\WindowsApps\\') { $Python = $found.Source; break }
        }
    }
}
if (-not $Python) { throw 'Python not found. Install Python 3.11+ or pass -Python / CADTEST_PYTHON.' }
$Python = (Get-Command $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$previousRoot = $env:CADTEST_ROOT
$previousEncoding = $env:PYTHONUTF8
try {
    $env:CADTEST_ROOT = $root
    $env:PYTHONUTF8 = '1'
    $culture=[Globalization.CultureInfo]::InvariantCulture
    $limits=@('--memory-mib',$MemoryMiB.ToString($culture),'--timeout',$TimeoutSeconds.ToString($culture),'--cpu-percent',$CpuPercent.ToString($culture))
    if($Mode -eq 'Recognize'){
        if(-not $Drawing -or -not $Output){throw 'Recognize requires -Drawing and a new -Output directory.'}
        # Validate the task parent as well as recognition/, without creating it.
        $checkedOutput = & $Python -B -X utf8 (Join-Path $PSScriptRoot 'workspace_paths.py') --task-output $Output
        if ($LASTEXITCODE -ne 0) { throw 'Output path rejected; no task was started.' }
        $Output = [string]$checkedOutput
        $arguments=@('-B','-X','utf8',(Join-Path $PSScriptRoot 'runner.py'),'--output',(Join-Path $Output 'recognition'),'--prepare-review')+$limits
        if ($Node) { $arguments += @('--node',$Node) }
        $arguments += @('--drawing') + $Drawing
        & $Python @arguments
        $result=$LASTEXITCODE
        if($result -ne 0){Write-Warning 'Processing did not complete. Inspect the new task output; prior results are retained.'}
    } else {
        if(-not $Manifest -or -not $Review){throw 'Validate/Export require -Manifest and -Review.'}
        $arguments=@('-B','-X','utf8',(Join-Path $PSScriptRoot 'review_guard.py'),$Mode.ToLowerInvariant(),'--manifest',$Manifest,'--review',$Review)+$limits
        if($Mode -eq 'Export'){
            if(-not $Output -or -not $Reviewer){throw 'Export requires -Output and -Reviewer.'}
            $arguments+=@('--output',$Output,'--reviewer',$Reviewer)
        }
        & $Python @arguments
        $result=$LASTEXITCODE
        if($result -eq 2){Write-Warning 'Review is incomplete or invalid; no confirmed export was produced.'}
    }
} finally {
    $env:CADTEST_ROOT = $previousRoot
    $env:PYTHONUTF8 = $previousEncoding
}
exit $result
