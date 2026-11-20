# How fast the tray app comes back when ffmpeg dies. Own settings and folder; a running tray app is asked to exit first.
#  1. start-up time (monitors measured all at once)
#  2. ffmpeg killed after 40 s of recording: restart within ~1 s, monitors not measured again, no warning
#  3. killed again 6 s later (did not run fine): backs off 10 s and measures the monitors again
#   .\crash-test.ps1 [-Exe path\to\TriRecTray.exe]
param([string]$Exe = "$PSScriptRoot\..\app\TriRecTray.exe")
$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }
if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & $Exe --exit; $t = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
}
$dir = Join-Path $PSScriptRoot ("crash_" + (Get-Date -Format HHmmss))
New-Item -ItemType Directory $dir | Out-Null
@{ OutDir = $dir; Fps = 10; Qp = 30; SegmentMinutes = 60; KeepGb = 0 } | ConvertTo-Json | Set-Content "$dir\settings.json"
$log = "$dir\trirec.log"
function Ff { Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' AND ParentProcessId=$($app.Id)" | Where-Object { $_.CommandLine -match 'segment' } }
function WaitFile([int]$n, [int]$sec) { $t = [Diagnostics.Stopwatch]::StartNew(); while (@(Get-ChildItem $dir -Filter *.mkv).Count -lt $n -and $t.Elapsed.TotalSeconds -lt $sec) { Start-Sleep -Milliseconds 100 }; $t.Elapsed.TotalSeconds }

$env:TRIREC_SETTINGS = "$dir\settings.json"
$t0 = Get-Date
$app = Start-Process $Exe -PassThru
Remove-Item Env:TRIREC_SETTINGS
$startup = WaitFile 1 30
"start-up: first file after {0:F1} s" -f $startup
Check 'start-up under 6 s (was about 10)' ($startup -lt 6)

Start-Sleep 40
$k1 = Get-Date; Stop-Process -Id (Ff).ProcessId -Force
$gap = WaitFile 2 20
"after a kill at 40 s: new file after {0:F1} s" -f $gap
Check 'restarted within 4 s (was about 18)' ($gap -lt 4)

Start-Sleep 6
Stop-Process -Id (Ff).ProcessId -Force
$gap2 = WaitFile 3 40
"after a second kill 6 s later: new file after {0:F1} s" -f $gap2
Check 'quick second failure backs off (10 s or more)' ($gap2 -ge 9)
Start-Sleep 4
& $Exe --exit; $app.WaitForExit(30000) | Out-Null

$l = Get-Content $log
$l | Select-String 'monitors:|starting again|restarted|stopped|Stopped|exited' | ForEach-Object { "    $_" }
Check 'first start measured the monitors' (($l | Select-String 'monitors: measured').Count -ge 1)
Check 'fast restart reused them' (($l | Select-String 'monitors: same as before').Count -eq 1)
Check 'fast restart waited 1 s' (($l | Select-String 'starting again in 1 s').Count -eq 1)
Check 'quick failure waited 10 s' (($l | Select-String 'starting again in 10 s').Count -eq 1)
Check 'measured again after the quick failure' (($l | Select-String 'monitors: measured').Count -eq 2)
foreach ($f in Get-ChildItem $dir -Filter *.mkv | Sort-Object Name) {
    $s = & ffprobe -v error -show_entries stream=codec_type -of csv=p=0 $f.FullName
    "    {0}  {1:F1} MB  {2}" -f $f.Name, ($f.Length / 1MB), (@($s) -join ',')
}
$last = Get-ChildItem $dir -Filter *.mkv | Sort-Object Name | Select-Object -Last 1
Check 'last file (cleanly stopped) has picture and sound' ((& ffprobe -v error -show_entries stream=codec_type -of csv=p=0 $last.FullName) -contains 'audio')
Remove-Item $dir -Recurse -Force
"failures: $fail"
exit $fail
