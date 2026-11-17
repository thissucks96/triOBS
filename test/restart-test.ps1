# Save-and-restart test for the tray app.
# Starts TriRecTray with its own settings file and folder, opens the settings window,
# presses Save (which restarts the recording), then stops the app and shows whether
# the files before and after the restart both have picture and two sound tracks.
# With -Kill it also ends ffmpeg by force mid-recording to see how the app reacts.
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Kill)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot ("restart_" + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
$env:TRIREC_SETTINGS = Join-Path $out 'settings.json'
@{ OutDir = $out; Fps = 10; Qp = 26; SegmentMinutes = 10; SystemAudio = $true; Mic = $true } | ConvertTo-Json | Set-Content $env:TRIREC_SETTINGS
$exe = "$Root\app\TriRecTray.exe"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$app = Start-Process $exe -PassThru
Start-Sleep 10
& $exe --settings
$win = $null; $sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $win -and $sw.Elapsed.TotalSeconds -lt 10) {
    Start-Sleep -Milliseconds 300
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children',
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'TriRec settings')))
}
if (-not $win) { "settings window did not appear" }
else {
    $save = $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Save')))
    $save.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    "pressed Save"
}
Start-Sleep 14
if ($Kill) {
    $ff = Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' AND ParentProcessId=$($app.Id)"
    if ($ff) { Stop-Process -Id $ff.ProcessId -Force; "ended ffmpeg $($ff.ProcessId) by force" } else { "no ffmpeg child found" }
    Start-Sleep 35   # 10 s wait + about 8 s start-up, with margin
    "ffmpeg children now (1 = it restarted): "+ @(Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe' AND ParentProcessId=$($app.Id)").Count
}
$sw.Restart(); & $exe --stop
if (-not $app.WaitForExit(20000)) { "tray app did not exit in 20 s"; $app.Kill() } else { "stopped in {0:F1} s" -f $sw.Elapsed.TotalSeconds }
"--- log"; Get-Content "$out\trirec.log"
"--- files"
Get-ChildItem "$out\*.mkv" | Sort-Object Name | ForEach-Object {
    $s = ffprobe -v error -show_entries "stream=codec_type:stream_tags=title,DURATION" -of csv=p=0 $_.FullName 2>$null
    "{0}  {1:F1} MB  {2}" -f $_.Name, ($_.Length / 1MB), ($s -join ' | ')
}
