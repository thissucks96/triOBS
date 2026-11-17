# Stop recording without exiting, then start again, then exit.
# Uses its own settings file and folder. The tray app allows one copy, so a running one is asked to exit first.
#   .\pause-test.ps1 -Exe path\to\TriRecTray.exe
param([string]$Exe = "$PSScriptRoot\..\app\TriRecTray.exe")
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot ("pause_" + (Get-Date -Format HHmmss))
New-Item -ItemType Directory $dir | Out-Null
$settings = Join-Path $dir 'settings.json'
@{ OutDir = $dir; Fps = 10; Qp = 30; SegmentMinutes = 60; SystemAudio = $true; Mic = $true } | ConvertTo-Json | Set-Content $settings

function Ff { @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Where-Object { $_.CommandLine -like "*$dir*" }) }
function Wait-Until([scriptblock]$cond, [int]$sec) { $t = [Diagnostics.Stopwatch]::StartNew(); while (-not (& $cond) -and $t.Elapsed.TotalSeconds -lt $sec) { Start-Sleep -Milliseconds 300 }; [int]$t.Elapsed.TotalSeconds }
$fail = 0
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }

if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & $Exe --exit
    $s = Wait-Until { -not (Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) } 30
    "running copy exited after $s s"
}

$env:TRIREC_SETTINGS = $settings
$tray = Start-Process $Exe -ArgumentList '--out', "`"$dir`"" -PassThru
Remove-Item Env:TRIREC_SETTINGS
Wait-Until { (Ff).Count -gt 0 } 30 | Out-Null
Start-Sleep 12
Check 'recording at start' ((Ff).Count -eq 1)

& $Exe --stop-recording
$s = Wait-Until { (Ff).Count -eq 0 } 30
Check "ffmpeg ended after Stop recording ($s s)" ((Ff).Count -eq 0)
Start-Sleep 3
Check 'tray app still running while stopped' (-not $tray.HasExited)
$files1 = @(Get-ChildItem $dir -Filter *.mkv)
Check "one file after first recording ($($files1.Count))" ($files1.Count -eq 1)

& $Exe --start-recording
Wait-Until { (Ff).Count -gt 0 } 30 | Out-Null
Start-Sleep 12
Check 'recording again after Start recording' ((Ff).Count -eq 1)

& $Exe --exit
$s = Wait-Until { $tray.HasExited } 30
Check "app exited after Exit ($s s)" $tray.HasExited
Check 'ffmpeg gone after Exit' ((Ff).Count -eq 0)

$files = @(Get-ChildItem $dir -Filter *.mkv | Sort-Object Name)
Check "two files in total ($($files.Count))" ($files.Count -eq 2)
foreach ($f in $files) {
    $streams = (& ffprobe -v error -show_entries stream=codec_type -of csv=p=0 $f.FullName) -join ','
    $dur = [double](& ffprobe -v error -show_entries format=duration -of csv=p=0 $f.FullName)
    Check ("{0}: {1:F1} s, streams {2}" -f $f.Name, $dur, $streams) ($streams -eq 'video,audio,audio,audio' -and $dur -gt 5)
}
"--- log"; Get-Content (Join-Path $dir 'trirec.log')
"failures: $fail"
exit $fail
