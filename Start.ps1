$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'ui\bin\Release\net10.0-windows\CadReviewUI.exe'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw 'Desktop not built. Run Build.ps1 first.' }
Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot
