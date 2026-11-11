# Thin entry point: keep all backend flags in one place.
& (Join-Path $PSScriptRoot 'work\segmented-20261109\RunAccuracy.ps1') @args
exit $LASTEXITCODE
