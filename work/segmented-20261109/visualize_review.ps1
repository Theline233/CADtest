param([Parameter(Mandatory=$true)][string]$InputWorkbook,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
$cadInput=[IO.Path]::GetFullPath($InputWorkbook)
$cadOutput=[IO.Path]::GetFullPath($Output)
if(-not $cadInput.StartsWith('D:\CADtest\outputs\',[StringComparison]::OrdinalIgnoreCase)-or-not $cadOutput.StartsWith('D:\CADtest\outputs\',[StringComparison]::OrdinalIgnoreCase)){throw 'Preview paths must stay below workspace outputs.'}
if(Test-Path -LiteralPath $cadOutput){throw 'Preview output must be new.'}
New-Item -ItemType Directory -Path $cadOutput | Out-Null
$cadBefore=(Get-FileHash -LiteralPath $cadInput -Algorithm SHA256).Hash
$cadExcel=$null;$cadBook=$null
try {
    $cadExcel=New-Object -ComObject Excel.Application
    $cadExcel.Visible=$false;$cadExcel.DisplayAlerts=$false;$cadExcel.AskToUpdateLinks=$false;$cadExcel.AutomationSecurity=3
    $cadBook=$cadExcel.Workbooks.Open($cadInput,0,$true)
    $cadPreviews=@(
        @('overview','复核明细','$A$1:$H$12'),
        @('confirmation','复核明细','$I$7:$P$12'),
        @('files','文件诊断','$A$1:$F$13'),
        @('files-more','文件诊断','$A$14:$F$19'),
        @('evidence','复核明细','$S$7:$U$12')
    )
    foreach($cadSpec in $cadPreviews){
        $cadSheet=$cadBook.Worksheets.Item($cadSpec[1])
        $cadSheet.PageSetup.PrintArea=$cadSpec[2]
        $cadSheet.PageSetup.Orientation=2
        $cadSheet.PageSetup.Zoom=$false
        $cadSheet.PageSetup.FitToPagesWide=1
        $cadSheet.PageSetup.FitToPagesTall=1
        $cadSheet.ExportAsFixedFormat(0,(Join-Path $cadOutput ($cadSpec[0]+'.pdf')))
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cadSheet)
    }
    $cadSheet=$cadBook.Worksheets.Item('复核明细')
    $cadClass=(Get-Item -LiteralPath 'Registry::HKEY_CLASSES_ROOT\Excel.Application\CLSID').GetValue('')
    $cadServer=(Get-Item -LiteralPath ('Registry::HKEY_CLASSES_ROOT\CLSID\'+$cadClass+'\LocalServer32')).GetValue('')
    $cadCheck=[ordered]@{readOnly=$cadBook.ReadOnly;automationVersion=$cadExcel.Version;automationServer=$cadServer;header=$cadSheet.Range('A7').Text;headerColor=$cadSheet.Range('A7').Interior.Color;confirmationColor=$cadSheet.Range('I8').Interior.Color;row8Height=$cadSheet.Rows.Item(8).RowHeight;sourceSha256=$cadBefore;unchanged=$false}
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cadSheet)
} finally {
    if($null -ne $cadBook){$cadBook.Close($false);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cadBook)}
    if($null -ne $cadExcel){$cadExcel.Quit();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cadExcel)}
}
if((Get-FileHash -LiteralPath $cadInput -Algorithm SHA256).Hash -ne $cadBefore){throw 'Read-only preview changed workbook.'}
$cadCheck.unchanged=$true
$cadCheck | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cadOutput 'excel-check.json') -Encoding utf8
$cadCheck | ConvertTo-Json
