#Requires -Version 5.1
<#
Release smoke test — run on ANY machine, including one without VRChat or
any model installed.

Boots the published Chatterbox.exe against a throwaway data folder and a
fake VRChat log in which a watched player is already present, then checks
that the process survives, the page connected, the settings were read, the
fake log was read (a world and one player), and the boot-time auto-start
check ran (the exact path that crashed 1.2.4).
Nothing touches the real %APPDATA%\Chatterbox or the real game log.

  .\tools\smoke.ps1                       # after .\build.ps1 (uses publish\Chatterbox.exe)
  .\tools\smoke.ps1 -Exe C:\Tools\Chatterbox\Chatterbox.exe
#>
[CmdletBinding()]
param(
  [string]$Exe = '',
  [int]$WaitSeconds = 15
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() defaults
# when a script runs with -File, so the default path is resolved here.
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\publish\Chatterbox.exe' }
$Exe = (Resolve-Path $Exe).Path
if (Get-Process Chatterbox -ErrorAction SilentlyContinue) {
  throw 'Chatterbox is already running - close it first (single-instance mutex).'
}

$root = Join-Path $env:TEMP ('chatterbox-smoke-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data'
$logs = Join-Path $root 'vrchat'
New-Item -ItemType Directory -Force $data, $logs | Out-Null

# A returning user with one watched player and auto-start on.
@'
{
  "AutoStartEnabled": true,
  "AutoStartFriends": [ { "Id": "usr_smoke-0000-0000-0000-000000000001", "Name": "SmokeTestPlayer" } ]
}
'@ | Set-Content (Join-Path $data 'stt_settings.json') -Encoding UTF8

# A VRChat log in which that player is already in the instance.
$stamp = Get-Date -Format 'yyyy.MM.dd HH:mm:ss'
@"
$stamp Log        -  [Behaviour] Joining wrld_smoke-0000-0000-0000-000000000001:1~private(usr_me)~region(us)
$stamp Log        -  [Behaviour] Entering Room: Smoke Test World
$stamp Log        -  [Behaviour] OnPlayerJoined SmokeTestPlayer (usr_smoke-0000-0000-0000-000000000001)
"@ | Set-Content (Join-Path $logs 'output_log_2026-01-01_00-00-00.txt') -Encoding UTF8

$t0 = Get-Date
$p = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe) -ArgumentList @(
  '--data-dir', "`"$data`"", '--vrchat-log-dir', "`"$logs`"", '--assume-vrchat-running')
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
$alive = -not $p.HasExited

$bootLog = Join-Path $data 'last_boot.log'
$log = if (Test-Path $bootLog) { Get-Content $bootLog -Raw } else { '' }
$errLog = Join-Path $data 'error.log'
$errors = if (Test-Path $errLog) { Get-Content $errLog -Raw } else { '' }
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $t0; ProviderName = 'Application Error' } -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -match 'Chatterbox' }).Count
if ($alive) { Stop-Process -Id $p.Id -Force }

$checks = [ordered]@{
  'process alive after wait'       = $alive
  'boot log written'               = ($log.Length -gt 0)
  'settings file read'             = ($log -match 'settings from:\s+settings file')
  'page connected'                 = ($log -match 'page connected')
  'boot auto-start check ran'      = ($log -match 'boot auto-start check')
  'VRChat log read'                = ($log -match 'vrchat log:\s+output_log_2026-01-01_00-00-00\.txt .*in a world, 1 player')
  'no Windows crash events'        = ($crashes -eq 0)
  'no unhandled exceptions logged' = ($errors -notmatch 'Unhandled|SessionWork|UiDispatcher|OnUiMessage')
}
$failed = 0
foreach ($k in $checks.Keys) {
  $ok = [bool]$checks[$k]
  if (-not $ok) { $failed++ }
  Write-Host ('  [{0}] {1}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $k)
}
Write-Host ''
Write-Host '--- last_boot.log ---'
Write-Host $log
if ($errors) { Write-Host '--- error.log ---'; Write-Host $errors }
try { Remove-Item -Recurse -Force $root } catch { }

if ($failed -gt 0) { Write-Host "SMOKE TEST FAILED ($failed check(s))" -ForegroundColor Red; exit 1 }
Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
exit 0
