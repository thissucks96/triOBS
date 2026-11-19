# Device order for the mic: chosen device, then backup, then the Windows default.
# A made-up id stands in for an unplugged device. Three short tray recordings; a running tray app is asked to exit first.
#   .\backup-device-test.ps1 -Main "{0.0.1...}" -Backup "{0.0.1...}" [-Exe path\to\TriRecTray.exe]
param([Parameter(Mandatory)][string]$Main, [Parameter(Mandatory)][string]$Backup, [string]$Exe = "$PSScriptRoot\..\app\TriRecTray.exe")
$ErrorActionPreference = 'Stop'
$fail = 0
$gone = '{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}'
function Check([string]$what, [bool]$ok) { "{0}  {1}" -f ($(if ($ok) { 'PASS' } else { $script:fail++; 'FAIL' })), $what }
if (Get-Process TriRecTray -ErrorAction SilentlyContinue) {
    & $Exe --exit; $t = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Process TriRecTray, ffmpeg -ErrorAction SilentlyContinue) -and $t.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300 }
}

function Run([string]$name, [string]$dev, [string]$bak) {
    $dir = Join-Path $PSScriptRoot ("backup_" + $name + "_" + (Get-Date -Format HHmmss))
    New-Item -ItemType Directory $dir | Out-Null
    @{ OutDir = $dir; Fps = 10; Qp = 30; SegmentMinutes = 60; SystemAudio = $false; Mic = $true; MicDevice = $dev; MicBackup = $bak } |
        ConvertTo-Json | Set-Content "$dir\settings.json"
    $env:TRIREC_SETTINGS = "$dir\settings.json"
    $p = Start-Process $Exe -PassThru
    Remove-Item Env:TRIREC_SETTINGS
    Start-Sleep 12
    & $Exe --exit; $p.WaitForExit(30000) | Out-Null
    $line = Get-Content "$dir\trirec.log" | Select-String 'audio Mic' | ForEach-Object Line
    "--- $name"; $line | ForEach-Object { "    $_" }
    $f = (Get-ChildItem $dir -Filter *.mkv | Select-Object -First 1).FullName
    $streams = if ($f) { & ffprobe -v error -select_streams a -show_entries stream=index -of csv=p=0 $f } else { @() }
    [pscustomobject]@{ Log = ($line -join "`n"); Tracks = @($streams).Count }
}

$mainName = (& "$PSScriptRoot\..\app\trirec.exe" devices | Select-String -SimpleMatch $Main -Context 1, 0).Context.PreContext[0].Trim(' *')
$bakName = (& "$PSScriptRoot\..\app\trirec.exe" devices | Select-String -SimpleMatch $Backup -Context 1, 0).Context.PreContext[0].Trim(' *')
"main: $mainName / backup: $bakName"

$r = Run 'main' $Main $Backup
Check 'main connected: main used' ($r.Log -match [regex]::Escape("chosen device ($mainName)"))
Check 'main run has a sound track' ($r.Tracks -eq 1)
$r = Run 'backup' $gone $Backup
Check 'main missing: backup used' ($r.Log -match [regex]::Escape("backup device ($bakName)"))
Check 'missing note names the backup' ($r.Log -match 'using the backup until it is')
Check 'backup run has a sound track' ($r.Tracks -eq 1)
$r = Run 'default' $gone $gone
Check 'both missing: Windows default used' ($r.Log -match 'Windows default device')
Check 'default run has a sound track' ($r.Tracks -eq 1)
"failures: $fail"
exit $fail
