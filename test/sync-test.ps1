# Audio/video sync test for TriRec.
# Records with the console recorder, and $Events times during it shows a magenta
# square on the main monitor at the same instant a 3 kHz beep starts. Then finds
# the first magenta frame and the first beep sample in the file and prints the gap.
#   .\sync-test.ps1 -Fps 23                       check the automatic sync correction at 23 fps
#   .\sync-test.ps1 -Extra '--audio-shift','0'    measure the raw offset (to update Options.AutoShift)
#   -Track 1 measures the System track instead of the Mix
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [int]$Fps = 30, [int]$Events = 6, [string[]]$Extra = @(), [int]$Track = 0)
$Seconds = 8 + 4 * $Events
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot ("sync_" + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Drawing.Primitives, System.Windows.Forms.Primitives, System.ComponentModel.Primitives -TypeDefinition @'
using System.Windows.Forms;
public class FlashForm : Form {
    protected override bool ShowWithoutActivation { get { return true; } }   // do not take focus from the game
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000008; return p; } } // NOACTIVATE | TOPMOST
}
'@

# 0.3 s of 3 kHz tone, 48 kHz mono 16-bit, quarter volume
$n = 14400; $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $bw.Write([int](36 + $n * 2)); $bw.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
$bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1); $bw.Write([int]48000); $bw.Write([int]96000); $bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([Text.Encoding]::ASCII.GetBytes('data')); $bw.Write([int]($n * 2))
for ($i = 0; $i -lt $n; $i++) { $bw.Write([int16](8000 * [Math]::Sin(2 * [Math]::PI * 3000 * $i / 48000))) }
$ms.Position = 0; $player = New-Object System.Media.SoundPlayer $ms; $player.Load()

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form = New-Object FlashForm
$form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.BackColor = [System.Drawing.Color]::FromArgb(255, 0, 255)
$form.ShowInTaskbar = $false
$form.SetBounds($b.X + [int]($b.Width / 2) - 300, $b.Y + [int]($b.Height / 2) - 300, 600, 600)

$rec = Start-Process "$Root\app\trirec.exe" -ArgumentList (@('--seconds', $Seconds, '--fps', $Fps, '--out', "`"$out`"") + $Extra) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$out\rec.out" -RedirectStandardError "$out\rec.err"
$wait = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-ChildItem "$out\*.mkv" -ErrorAction SilentlyContinue | Where-Object Length -gt 0) -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
for ($k = 0; $k -lt $Events; $k++) {
    Start-Sleep -Milliseconds $(if ($k -eq 0) { 3000 } else { 3700 + (Get-Random -Maximum 300) })
    $form.Show(); $form.Refresh(); $player.Play()
    Start-Sleep -Milliseconds 300
    $form.Hide()
}
$rec.WaitForExit(); $form.Dispose()

$f = (Get-ChildItem "$out\*.mkv" | Select-Object -First 1).FullName
# Centre 400x400 of the main monitor on the canvas (main monitor = 2560x1440 at 3840,0)
$v = & ffmpeg -hide_banner -loglevel error -i $f -an -vf "crop=400:400:4920:520,signalstats,metadata=print:file=-" -f null - 2>$null
$t = $null; $u = 0; $frames = foreach ($line in $v) {
    if ($line -match 'pts_time:([\d.]+)') { $t = [double]$Matches[1] }
    elseif ($line -match 'signalstats\.UAVG=([\d.]+)') { $u = [double]$Matches[1] }
    elseif ($line -match 'signalstats\.VAVG=([\d.]+)') { [pscustomobject]@{ T = $t; Magenta = ($u -gt 170 -and [double]$Matches[1] -gt 180) } }
}
$a = & ffmpeg -hide_banner -loglevel error -i $f -map 0:a:$Track -af "bandpass=f=3000:width_type=h:w=200,asetnsamples=480,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-" -f null - 2>$null
$chunks = foreach ($line in $a) {
    if ($line -match 'pts_time:([\d.]+)') { $t = [double]$Matches[1] }
    elseif ($line -match 'RMS_level=(-?[\d.]+|-inf)') { [pscustomobject]@{ T = $t; Db = $(if ($Matches[1] -eq '-inf') { -120 } else { [double]$Matches[1] }) } }
}
function Onsets($items, [scriptblock]$on) {   # times where the condition turns on after at least 1 s off
    $last = -10; $prev = $false
    foreach ($i in $items) { $now = & $on $i; if ($now -and -not $prev -and ($i.T - $last) -gt 1) { $i.T }; if ($now) { $last = $i.T }; $prev = $now }
}
$vOn = @(Onsets $frames { param($i) $i.Magenta })
$peak = ($chunks | Measure-Object Db -Maximum).Maximum
$aOn = @(Onsets $chunks { param($i) $i.Db -gt ($peak - 12) })
"file: $f"
"video frames: $($frames.Count), flash onsets (s): $($vOn -join ', ')"
"audio 3 kHz peak: $peak dB, beep onsets (s): $($aOn -join ', ')"
$gaps = for ($k = 0; $k -lt [Math]::Min($vOn.Count, $aOn.Count); $k++) {
    $g = $aOn[$k] - $vOn[$k]
    "event $($k + 1): audio is {0:+0.000;-0.000} s relative to video (positive = sound comes late)" -f $g | Write-Host
    $g
}
if ($vOn.Count -ne $aOn.Count) { "WARNING: $($vOn.Count) flashes but $($aOn.Count) beeps found; pairs may be wrong" }
$m = $gaps | Measure-Object -Average -Minimum -Maximum
"summary: mean {0:+0.000;-0.000} s, min {1:+0.000;-0.000}, max {2:+0.000;-0.000} over {3} events (video step {4:0.000} s)" -f $m.Average, $m.Minimum, $m.Maximum, $m.Count, (1 / $Fps)
ffprobe -v error -show_entries "stream=codec_type:stream_tags=DURATION" -of csv=p=0 $f
