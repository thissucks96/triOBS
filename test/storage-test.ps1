# Storage limit (Retention.cs) on fake recordings: sparse files that report 1 GB each but use no disk space.
# Part 1 uses `trirec cleanup`; part 2 checks the tray app applies KeepGb when it opens (with --stopped: no recording).
# A running tray app is asked to exit first.
#   .\storage-test.ps1 [-Dir path] [-App path\to\app]
param([string]$Dir = "$env:TEMP\trirec-storage-test", [string]$App = "$PSScriptRoot\..\app")
$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }
$trirec = "$App\trirec.exe"
$now = Get-Date

function Fake([string]$name, [double]$ageDays) {
    $p = Join-Path $Dir $name
    New-Item -ItemType File $p -Force | Out-Null
    fsutil sparse setflag $p | Out-Null
    $fs = [IO.File]::OpenWrite($p); $fs.SetLength(1GB); $fs.Close()
    (Get-Item $p).LastWriteTime = $now.AddDays(-$ageDays)
}
function Reset {
    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
    New-Item -ItemType Directory $Dir | Out-Null
    # ten old recordings, #1 oldest (28 days) ... #10 newest (1 day); #3 has marks
    for ($i = 1; $i -le 10; $i++) { Fake ("2026-10-{0:00}_12-00-00.mkv" -f $i) (31 - 3 * $i) }
    Set-Content (Join-Path $Dir '2026-10-03_12-00-00.marks.txt') '00:01:00  (clock 12:01:00)'
    Fake '2026-10-11_12-00-00.mkv' (1 / 1440)            # "being recorded": newest, written a minute ago
    Fake 'obs_clip.mkv' 40                                # not TriRec's
    Fake '2026-10-01_12-00-00.mon1.mkv' 40                # a crop
    New-Item -ItemType Directory "$Dir\sub" | Out-Null; Fake 'sub\2026-09-01_12-00-00.mkv' 60
}
function Left { (Get-ChildItem $Dir -Filter '2026-10-??_12-00-00.mkv' | ForEach-Object { [int]$_.Name.Substring(8, 2) }) -join ',' }
function Foreign { (Test-Path "$Dir\obs_clip.mkv") -and (Test-Path "$Dir\2026-10-01_12-00-00.mon1.mkv") -and (Test-Path "$Dir\sub\2026-09-01_12-00-00.mkv") }

# ---- part 1: trirec cleanup
Reset
$dry = & $trirec cleanup $Dir --gb 5
$dry | ForEach-Object { "    $_" }
Check 'dry run lists 6 (oldest unmarked first), deletes nothing' ((($dry -match 'would delete').Count -eq 6) -and (Left) -eq '1,2,3,4,5,6,7,8,9,10,11')
$out = & $trirec cleanup $Dir --gb 5 --delete
Check 'over 5 GB: 1,2,4,5,6,7 deleted; marked 3 and newest kept' ((Left) -eq '3,8,9,10,11')
Check 'one log line per deleted file' (($out -match '^storage: deleted').Count -eq 6)
Check 'other files untouched' (Foreign)
& $trirec cleanup $Dir --days 5 --delete | Out-Null
Check 'older than 5 days: 8 deleted, marked 3 kept' ((Left) -eq '3,9,10,11')
& $trirec cleanup $Dir --days 5 --include-marked --delete | Out-Null
Check 'including marked: 3 and its marks file deleted' ((Left) -eq '9,10,11' -and -not (Test-Path "$Dir\2026-10-03_12-00-00.marks.txt"))
& $trirec cleanup $Dir --gb 1 --delete | Out-Null
Check 'limit below the newest file: everything but the one being recorded goes' ((Left) -eq '11')
Check 'other files still untouched' (Foreign)

# ---- part 2: the tray app, KeepGb from settings, applied on opening
if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & "$App\TriRecTray.exe" --exit; $t = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
}
Reset
@{ OutDir = $Dir; KeepGb = 5; KeepDays = 0; KeepMarked = $true } | ConvertTo-Json | Set-Content "$Dir\settings.json"
$env:TRIREC_SETTINGS = "$Dir\settings.json"
$p = Start-Process "$App\TriRecTray.exe" -ArgumentList '--stopped' -PassThru
Remove-Item Env:TRIREC_SETTINGS
Start-Sleep 5
& "$App\TriRecTray.exe" --exit; $p.WaitForExit(20000) | Out-Null
$log = Get-Content "$Dir\trirec.log"
$log | ForEach-Object { "    $_" }
Check 'tray: opened stopped (no recording)' (($log -match 'recording not started \(--stopped\)').Count -eq 1 -and -not ($log -match 'Canvas'))
Check 'tray: same 6 deleted on opening' ((Left) -eq '3,8,9,10,11' -and ($log -match 'storage: deleted').Count -eq 6)
Remove-Item $Dir -Recurse -Force
"failures: $fail"
exit $fail
