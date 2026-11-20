# Save clip on a live tray recording (own settings and folder, 1-minute files, 30 s clips).
#  1. --clip 45 s in: one clip from the current file; also how far the written file lags behind "now"
#  2. --clip 12 s into the second file: joined from the end of the first file and the start of the second
#  3. a real Ctrl+Alt+S keypress
# A running tray app is asked to exit first.
#   .\clip-test.ps1 [-Exe path\to\TriRecTray.exe]
param([string]$Exe = "$PSScriptRoot\..\app\TriRecTray.exe")
$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }
if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & $Exe --exit; $t = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
}
Add-Type -Namespace K -Name B -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
function Press-CtrlAltS { foreach ($k in 0x11, 0x12, 0x53) { [K.B]::keybd_event($k, 0, 0, [UIntPtr]::Zero) }; Start-Sleep -Milliseconds 80
                          foreach ($k in 0x53, 0x12, 0x11) { [K.B]::keybd_event($k, 0, 2, [UIntPtr]::Zero) } }

$dir = Join-Path $PSScriptRoot ("clip_" + (Get-Date -Format HHmmss))
New-Item -ItemType Directory $dir | Out-Null
@{ OutDir = $dir; Fps = 10; Qp = 30; SegmentMinutes = 1; ClipSeconds = 30; KeepGb = 0 } | ConvertTo-Json | Set-Content "$dir\settings.json"
$env:TRIREC_SETTINGS = "$dir\settings.json"
$app = Start-Process $Exe -PassThru
Remove-Item Env:TRIREC_SETTINGS
$t = [Diagnostics.Stopwatch]::StartNew(); while (-not (Get-ChildItem $dir -Filter *.mkv) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
$first = Get-ChildItem $dir -Filter *.mkv | Select-Object -First 1
$start = [datetime]::ParseExact($first.BaseName, 'yyyy-MM-dd_HH-mm-ss', $null)

function Clips { @(Get-ChildItem "$dir\Clips" -Filter *.mkv -ErrorAction SilentlyContinue | Sort-Object Name) }
function WaitClip([int]$n) { $t = [Diagnostics.Stopwatch]::StartNew(); while ((Clips).Count -lt $n -and $t.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 100 }; Start-Sleep 1; $t.Elapsed.TotalSeconds }
function Info($f) {
    $d = [double](& ffprobe -v error -show_entries format=duration -of csv=p=0 $f.FullName)
    $s = & ffprobe -v error -show_entries "stream=codec_type,start_time:stream_tags=title:stream_disposition=default" -of csv=p=0 $f.FullName
    [pscustomobject]@{ Dur = $d; Streams = @($s) }
}

# 1
while ((Get-Date) -lt $start.AddSeconds(45)) { Start-Sleep -Milliseconds 100 }
$into = ((Get-Date) - $start).TotalSeconds
$last = (& ffprobe -v error -select_streams v -show_entries packet=pts_time -of csv=p=0 $first.FullName | Select-Object -Last 1)
& $Exe --clip
$w1 = WaitClip 1
$lag = $into - [double]$last
"clip 1: saved {0:F1} s after the request; the file being written was {1:F1} s behind" -f $w1, $lag
$i1 = Info (Clips)[0]; "    duration {0:F1} s; streams: {1}" -f $i1.Dur, ($i1.Streams -join ' | ')
Check 'clip 1 saved within 7 s' ($w1 -lt 7)
Check 'file on disk trails real time by under 4 s (the clip waits 4 s)' ($lag -lt 4)
Check 'clip 1 is 30 s plus at most one keyframe interval (20 s), minus the write lag' ($i1.Dur -ge 30 - $lag - 1 -and $i1.Dur -le 51)
Check 'clip 1 has video and 3 sound tracks with titles' (($i1.Streams -match '^video').Count -eq 1 -and ($i1.Streams -match 'audio.*(Mix|System|Mic)').Count -eq 3)

# 2: second file starts at about 60 s; ask 12 s into it
$t = [Diagnostics.Stopwatch]::StartNew(); while (@(Get-ChildItem $dir -Filter *.mkv).Count -lt 2 -and $t.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
$second = Get-ChildItem $dir -Filter *.mkv | Sort-Object Name | Select-Object -Last 1
$s2 = [datetime]::ParseExact($second.BaseName, 'yyyy-MM-dd_HH-mm-ss', $null)
while ((Get-Date) -lt $s2.AddSeconds(12)) { Start-Sleep -Milliseconds 100 }
& $Exe --clip
$w2 = WaitClip 2
$i2 = Info (Clips)[1]; "clip 2: {0:F1} s after the request; duration {1:F1} s; streams: {2}" -f $w2, $i2.Dur, ($i2.Streams -join ' | ')
Check 'clip 2 joined the two files (log)' ((Get-Content "$dir\trirec.log" | Select-String 'clip: saved .* from .* and ').Count -eq 1)
Check 'clip 2 is longer than the 12 s of the second file' ($i2.Dur -ge 20)
Check 'clip 2 has video and 3 sound tracks' (($i2.Streams -match '^video').Count -eq 1 -and ($i2.Streams -match '^audio').Count -eq 3)

# 3
Start-Sleep 3
Press-CtrlAltS
$w3 = WaitClip 3
Check 'real Ctrl+Alt+S saved a clip' ((Clips).Count -eq 3)

& $Exe --exit; $app.WaitForExit(30000) | Out-Null
Get-Content "$dir\trirec.log" | Select-String 'clip|taken' | ForEach-Object { "    $_" }
Check 'recording files untouched (2 or 3 of them, all readable)' (@(Get-ChildItem $dir -Filter *.mkv | Where-Object { (& ffprobe -v error -show_entries format=duration -of csv=p=0 $_.FullName) }).Count -ge 2)
Remove-Item $dir -Recurse -Force
"failures: $fail"
exit $fail
