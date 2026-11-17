# Audio/video sync test for TriRec.
# Records ~30 s with the console recorder, and twice during it shows a magenta
# square on the main monitor at the same instant a 3 kHz beep starts. Then finds
# the first magenta frame and the first beep sample in the file and prints the gap.
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [int]$Seconds = 30)
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

$rec = Start-Process "$Root\app\trirec.exe" -ArgumentList '--seconds', $Seconds, '--out', "`"$out`"" -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$out\rec.out" -RedirectStandardError "$out\rec.err"
$wait = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-ChildItem "$out\*.mkv" -ErrorAction SilentlyContinue | Where-Object Length -gt 0) -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 200 }
foreach ($pause in 5, 9) {
    Start-Sleep $pause
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
$a = & ffmpeg -hide_banner -loglevel error -i $f -map 0:a:0 -af "bandpass=f=3000:width_type=h:w=200,asetnsamples=480,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-" -f null - 2>$null
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
for ($k = 0; $k -lt [Math]::Min($vOn.Count, $aOn.Count); $k++) {
    "event $($k + 1): audio is {0:+0.00;-0.00} s relative to video (positive = sound comes late)" -f ($aOn[$k] - $vOn[$k])
}
ffprobe -v error -show_entries "stream=codec_type:stream_tags=DURATION" -of csv=p=0 $f
