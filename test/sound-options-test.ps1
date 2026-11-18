# Sound options from the settings file: chosen device, channels, volume, Mix on/off, track order.
# Two short tray recordings with their own settings and folder; a running tray app is asked to exit first.
#   .\sound-options-test.ps1 [-Exe path\to\TriRecTray.exe] [-MicId "{0.0.1...}"]
param([string]$Exe = "$PSScriptRoot\..\app\TriRecTray.exe", [string]$MicId)
$ErrorActionPreference = 'Stop'
$fail = 0
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }
if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & $Exe --exit; $t = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
}

function Run([string]$name, [hashtable]$s) {
    $dir = Join-Path $PSScriptRoot ("sound_" + $name + "_" + (Get-Date -Format HHmmss))
    New-Item -ItemType Directory $dir | Out-Null
    $s.OutDir = $dir; $s.Fps = 10; $s.Qp = 30; $s.SegmentMinutes = 60
    $s | ConvertTo-Json | Set-Content "$dir\settings.json"
    $env:TRIREC_SETTINGS = "$dir\settings.json"
    $p = Start-Process $Exe -PassThru
    Remove-Item Env:TRIREC_SETTINGS
    Start-Sleep 16
    & $Exe --exit; $p.WaitForExit(30000) | Out-Null
    $f = (Get-ChildItem $dir -Filter *.mkv | Select-Object -First 1).FullName
    $streams = & ffprobe -v error -select_streams a -show_entries "stream=channels:stream_tags=title:stream_disposition=default" -of csv=p=0 $f
    $levels = for ($i = 0; $i -lt @($streams).Count; $i++) {
        $m = (& ffmpeg -hide_banner -i $f -map "0:a:$i" -af volumedetect -f null - 2>&1 | Select-String 'max_volume') -replace '.*max_volume: ', ''
        $m
    }
    [pscustomobject]@{ Dir = $dir; Streams = @($streams); Levels = @($levels); Log = Get-Content "$dir\trirec.log" }
}

# 1: every option changed from its default
$s1 = @{ SystemAudio = $true; SystemChannels = 1; SystemVolume = 50; Mic = $true; MicChannels = 2; MicVolume = 150
         MixTrack = $true; MixChannels = 2; FirstTrack = 'System' }
if ($MicId) { $s1.MicDevice = $MicId }
$r = Run 'all' $s1
"--- run 1 streams (channels,default,title): $($r.Streams -join ' | ')"
"--- run 1 peaks: $($r.Levels -join ' | ')"
$r.Log | Select-String 'audio|Canvas' | ForEach-Object { "    $_" }
Check 'track order System, Mix, Mic' (($r.Streams | ForEach-Object { ($_ -split ',')[-1] }) -join ',' -eq 'System,Mix,Mic')
Check 'System mono and default' ($r.Streams[0] -eq '1,1,System')
Check 'Mix stereo' ($r.Streams[1] -eq '2,0,Mix')
Check 'Mic stereo' ($r.Streams[2] -eq '2,0,Mic')
Check 'volumes in the log (50%, 150%)' ((($r.Log -match 'volume 50%').Count -eq 1) -and (($r.Log -match 'volume 150%').Count -eq 1))
if ($MicId) { Check 'chosen mic used' (($r.Log -match 'audio Mic: .*chosen device').Count -eq 1) }

# 2: system sound only, no Mix
$r2 = Run 'sysonly' @{ SystemAudio = $true; Mic = $false; MixTrack = $true; FirstTrack = 'Mix' }
"--- run 2 streams: $($r2.Streams -join ' | ')"
Check 'only a System track, stereo, default' (@($r2.Streams).Count -eq 1 -and $r2.Streams[0] -eq '2,1,System')
"failures: $fail"
exit $fail
