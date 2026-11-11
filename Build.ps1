[CmdletBinding()]
param([switch]$IncludeTestTools)
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The recognition guard requires Windows.' }
$engine = Join-Path $PSScriptRoot 'work\segmented-20261109'
$projects = @(
    @('native-source\CADtest.csproj', 'native', $true),
    @('projector\Projector.csproj', 'projector\bin\Release\net10.0', $false),
    @('review-source\CadReviewWorkflow.csproj', 'review-native', $true)
)
if ($IncludeTestTools) { $projects += ,@('fixture-tests\FixtureTests.csproj','fixture-tests\bin\Release\net10.0',$false) }
Push-Location $PSScriptRoot
try {
    foreach ($entry in $projects) {
        $project = Join-Path $engine $entry[0]
        $restore = @('restore',$project,'--configfile',(Join-Path $PSScriptRoot 'NuGet.Config'))
        if ($entry[2]) { $restore += '--locked-mode' }
        & dotnet @restore
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $($entry[0])" }
        & dotnet build $project -c Release --no-restore -o (Join-Path $engine $entry[1])
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $($entry[0])" }
    }
} finally { Pop-Location }
Write-Host 'Backend built. Use Run.ps1 to recognize, validate or export.'
