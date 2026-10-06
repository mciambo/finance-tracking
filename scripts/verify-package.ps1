param([Parameter(Mandatory)][string]$Fixture)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$executable=Join-Path $projectRoot 'dist/FinanceTracking-win-x64/cli/finance.exe'
$directory=Join-Path $env:TEMP ('finance-package-check-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$dbPath=Join-Path $directory 'finance.db'
function Invoke-Finance([string[]]$Arguments,[int]$ExpectedExit=0) {
    $start=[System.Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute=$false
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.CreateNoWindow=$true
    foreach ($argument in @('--db',$dbPath,'--format','json')+$Arguments) { $start.ArgumentList.Add($argument) }
    $process=[System.Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEnd()
    $stderr=$process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne $ExpectedExit) { throw "Unexpected exit $($process.ExitCode): $stderr" }
    if ($ExpectedExit -eq 0) { if ($stdout.Trim()) { return ($stdout | ConvertFrom-Json) } }
    else { return ($stderr | ConvertFrom-Json) }
}
$preview=Invoke-Finance @('import',$Fixture)
if (Test-Path -LiteralPath $dbPath) { throw 'Read-only preview created database' }
$import=Invoke-Finance @('import',$Fixture,'--apply','--token',$preview.token)
if ($import.movements -ne $preview.movements) { throw 'Import preview mismatch' }
$repeated=Invoke-Finance @('import',$Fixture)
if ($repeated.movements -ne 0) { throw 'Repeated import added movements' }
$manual=Invoke-Finance @('expenses','add','--date','04/10/2026','--description','Spesa manuale CLI','--amount','12,50')
if ($manual.amount_cents -ne 1250 -or !$manual.id.StartsWith('m:')) { throw 'Manual addition failed' }
Invoke-Finance @('expenses','edit',$manual.id,'--description','Correzione CLI','--amount','10') | Out-Null
$filtered=@(Invoke-Finance @('expenses','list','--description','correzione','--min','10','--max','10'))
if ($filtered.Count -ne 1) { throw 'Filtering failed' }
$query=@(Invoke-Finance @('query','SELECT amount_cents FROM v_expenses WHERE id=$id','--param',('id='+$manual.id)))
if ($query[0].amount_cents -ne 1000) { throw 'Parameterized query failed' }
$csv=Join-Path $directory 'expenses.csv'
$start=[System.Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
foreach ($argument in @('--db',$dbPath,'expenses','export','--format','csv','--out',$csv)) { $start.ArgumentList.Add($argument) }
$process=[System.Diagnostics.Process]::Start($start);$process.WaitForExit()
if ($process.ExitCode -ne 0 -or !(Get-Content -LiteralPath $csv -TotalCount 1).Contains('amount_cents')) { throw 'CSV export failed' }
$rules=Invoke-Finance @('rules','preview')
if ($rules.changes.Count -ne 0) { throw 'No-op rules preview changed data' }
Invoke-Finance @('rules','apply','--token',$rules.token) | Out-Null
$alerts=@(Invoke-Finance @('alerts','list'))
if ($alerts.Count -gt 0) { Invoke-Finance @('alerts','handle',$alerts[0].id) | Out-Null }
$backup=Join-Path $directory 'backup.db'
Invoke-Finance @('backup','--out',$backup) | Out-Null
Invoke-Finance @('expenses','delete',$manual.id) | Out-Null
Invoke-Finance @('restore',$backup,'--apply') | Out-Null
$restored=Invoke-Finance @('expenses','get',$manual.id)
if ($restored.amount_cents -ne 1000) { throw 'Restore failed' }
$errorResult=Invoke-Finance @('query','DELETE FROM expenses') 1
if ($errorResult.error.code -ne 'QUERY_READ_ONLY') { throw 'Query write restriction failed' }
Write-Output "Packaged CLI verified: $directory"
