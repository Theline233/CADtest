[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$LegacyBaseline='5c9d70c76fd127d66af93f2748a5e063c4442919'
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$case=[IO.Path]::GetFullPath($Output,$root)
if (-not $case.StartsWith((Join-Path $root 'outputs')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $case)) {
    throw 'Use a new test directory under the current repository outputs.'
}
function CheckExit([string]$stage) {
    if ($LASTEXITCODE -ne 0) { throw "$stage failed (exit $LASTEXITCODE)" }
}
$priorRoot=$env:CADTEST_ROOT
$priorEncoding=$env:PYTHONUTF8
Push-Location -LiteralPath $root
try {
    $env:CADTEST_ROOT=$root
    $env:PYTHONUTF8='1'
    & .\Build.ps1 -IncludeTestTools
    $baseline=Join-Path $case 'baseline'
    New-Item -ItemType Directory -Path $baseline -Force | Out-Null
    $archive=Join-Path $case 'baseline.zip'
    git archive --format=zip --output $archive $LegacyBaseline global.json NuGet.Config work/segmented-20261109/review-source
    CheckExit 'archive legacy reviewer'
    Expand-Archive -LiteralPath $archive -DestinationPath $baseline
    $legacyExe=Join-Path $baseline 'review-native'
    dotnet build (Join-Path $baseline 'work/segmented-20261109/review-source/CadReviewWorkflow.csproj') -c Release -o $legacyExe
    CheckExit 'build legacy reviewer'
    $fixtureExe=Join-Path $root 'work/segmented-20261109/fixture-tests/bin/Release/net10.0/FixtureTests.exe'
    $drawing=Join-Path $case 'inputs/layout.dwg'
    & $fixtureExe review-order-dwg $drawing
    CheckExit 'generate DWG'
    $task=Join-Path $case 'task'
    & .\Run.ps1 -Mode Recognize -Drawing $drawing -Output $task
    CheckExit 'recognize DWG'
    $legacy=Join-Path $case 'legacy'
    python -B .\tests\make_review_order_legacy.py $legacy $legacyExe
    CheckExit 'prepare legacy fixture'
    $verify=Join-Path $case 'verify'
    $review=Join-Path $task 'review'
    $oldReview=Join-Path $legacy 'review'
    $app=Join-Path $root 'ui/bin/Release/net10.0-windows/CadReviewUI.exe'
    $ui=Start-Process -FilePath $app -ArgumentList @('--review-export-order-test',"`"$verify`"","`"$review`"","`"$oldReview`"") -WorkingDirectory $root -PassThru -Wait -WindowStyle Hidden
    Get-Content -Encoding utf8 -LiteralPath (Join-Path $verify 'checks.txt')
    if ($ui.ExitCode -ne 0) { throw "Desktop test failed (exit $($ui.ExitCode))" }
    python -B .\tests\verify_review_export_order.py $verify
    CheckExit 'independent XLSX reopen'
}
finally {
    Pop-Location
    $env:CADTEST_ROOT=$priorRoot
    $env:PYTHONUTF8=$priorEncoding
}
