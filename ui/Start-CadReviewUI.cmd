@echo off
cd /d "%~dp0.."
if not exist "ui\bin\Release\net10.0-windows\CadReviewUI.exe" (
  echo Desktop not built. Run Build.ps1 from the repository root first.
  pause
  exit /b 1
)
start "" "ui\bin\Release\net10.0-windows\CadReviewUI.exe"
