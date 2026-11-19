# TriRec: still to do

Written 2026-11-15.

## 1. Audio/video sync: DONE 2026-11-16, small residual left
- Test: `test\sync-test.ps1` (magenta square + 3 kHz beep, twice per 30 s recording).
- Found: sound was a constant 1.2 s EARLY (audio clock restarted when ffmpeg began reading).
- Fixed in `Core.cs` (`AudioTap.Run`): capture and pipe-writing separated by a queue; audio
  clock moved to the moment ffmpeg logs each monitor's first frame.
- Now: sound is 0.25-0.30 s early (one run, two events). Not chased further at the owner's
  request. Likely cause: the "first frame" log line arrives after the frame was captured.
- Recordings made before 02:25 on 2026-11-16 have the 1.2 s error.
- Dead ends: ddagrab as separate ffmpeg inputs (uneven frames); holding audio back until
  the video starts (ffmpeg will not open the pipe without data; 6.5 s late).
- The three monitors start about 0.05-0.16 s apart from each other; not corrected.

## 2. Double-check the code: DONE 2026-11-16
Fixed (test: `test\restart-test.ps1`, add `-Kill` to end ffmpeg by force):
- Saving settings restarted the recording WITHOUT sound ("All pipe instances are busy"):
  the audio pipes were never closed. Now closed, and named per recording. Confirmed before/after.
- Audio threads and devices leaked when ffmpeg could not start. Now released.
- Sound collected while ffmpeg starts is thrown away instead of being written late.
- A log line is written if ffmpeg never reports its first frame (sync would silently be 1 s off).
- "Open recordings folder" now works for folder names with spaces or commas (not clicked).
Looked at, left alone:
- "Error parsing Opus packet header": printed when a file is opened, once per track.
  All 30000 packets of a 10-minute file are there. Harmless.
Open, not fixed (each is new behaviour, the owner decides):
- (DONE 04:45) If ffmpeg ends by itself the tray app starts it again: after 10 s, then 20, 40 ...
  up to 5 min while it keeps failing. Tested by ending ffmpeg by force (`restart-test.ps1 -Kill`):
  back after 18 s with sound. About 18 s of recording is lost per restart.
  Whether a lock screen, UAC prompt or resolution change ends ffmpeg is still NOT known,
  and repeated failures (the longer waits) were not exercised.
- Switching the default playback device mid-recording: the System track goes silent until
  the next recording (only a lost device is followed, not a changed default).
- No automatic deletion of old recordings. Measured 0.7-1.2 GB per hour.
- `layout.json` is overwritten by each recording; older files in the same folder crop wrongly
  if the monitor layout changed in between.
- Start-up takes about 8 s (ffmpeg is run once per monitor to measure it).
- The last second or so of sound before a stop is not written.

## Sound tracks (2026-11-17)
- Track 1 "Mix" (default): System + Mic, mono. Track 2 "System": stereo. Track 3 "Mic": mono.
  Recordings before 01:08 on 2026-11-17 have System first and a stereo Mic.
- The mix adds both at full level (no halving); loud game sound plus loud speech can clip.
  Seen peaking at -0.3 dB in the first live minute.
- `test\sync-test.ps1` measures track 1, which is now the Mix (the beep is in it).

## Stop without exiting (2026-11-17)
- Tray menu: "Stop recording" / "Start recording" (app stays in the tray, gray dot), "Exit".
  Switches for scripts: `--stop-recording`, `--start-recording`, `--exit` (`--stop` still exits).
- Test: `test\pause-test.ps1` (stop, start, exit; two complete files). Passed 10/10.
- While stopped, saving settings does not start recording; no automatic restarts.
- Launching the app (and Start with Windows) still starts recording straight away.

## One exe, own icon, taskbar pin (2026-11-17)
- `dotnet publish TriRecTray -c Release -o app` gives a single `app\TriRecTray.exe` (needs .NET 8 Desktop Runtime).
- Icon: `TriRecTray\trirec.ico`, drawn by `tools\make-icon.ps1`.
- Opening it while it runs (e.g. clicking the taskbar pin) opens the settings window of the running copy.
  Tested: one copy stays, settings window appears, no message box.
- Start menu shortcut `TriRec` added (%APPDATA%\Microsoft\Windows\Start Menu\Programs\TriRec.lnk).

## Sync, limiter, device switch, marks (2026-11-17)
- Sync: the offset depends on frame rate. Measured with no correction (positive = sound late):
  10 fps -0.03 s, 15 fps +0.10 s, 23 fps +0.20 s, 30 fps +0.20 s (6-10 events each, while the PC was idle).
  `Options.AutoShift` undoes it (23 fps: sound 200 ms earlier); in between is interpolated.
  Override: `AudioShiftMs` in settings.json, `--audio-shift` on the console.
  NOT confirmed after the change: the check runs were spoiled by Chrome on the main monitor (flashes missing
  or late). Re-run on an idle screen: `test\sync-test.ps1 -Fps 23 -Events 10`.
- Mix limiter (alimiter, -1 dB). Offline test: loud input 0.0 dB (clipped) -> -1.0 dB; normal level unchanged.
- Sound format: Windows is asked for 48 kHz float (System stereo, Mic mono) and converts. Every 2 s the default
  device is checked; a change is followed. Tested with forced 44.1 kHz and a pretended switch (beeps intact
  before and after). A real switch of the default device was NOT tested.
- Ctrl+Alt+M / menu "Mark this moment" / `--mark`: appends "hh:mm:ss (clock)" to `<file>.marks.txt`.
  Tested with the switch and a real keypress.

## Settings window v2 (2026-11-17)
- Tabs: Recording, Sound, Tracks, Advanced; status line at the bottom. Code in `TriRecTray\SettingsForm.cs`.
- Sound, per source: on/off, device (Windows default = followed, or a chosen device; falls back to the default
  while the chosen one is unplugged and returns to it), channels, volume 0-200 %, live level meter.
- Tracks: Mix on/off, Mix mono/stereo, which track is first (default). `Options.TrackPlan` decides the order.
- Advanced: sync automatic or by hand; Ctrl+Alt+M note; settings file and ffmpeg location.
- New: "Start recording as soon as TriRec opens" (off = opens stopped).
- `trirec devices` lists sound devices with ids.
- Test: `test\sound-options-test.ps1 -MicId ...` (order, channels, default flag, volume, chosen device). 7/7.
- Found: the Windows default mic is "Microphone (Virtual Desktop Audio)", which is near silent (peak -43 dB);
  the Yeti peaked at -11.7 dB. The owner's settings still use the default; pick the Yeti in Sound if it is the real mic.
- NOT tested: clicking through the window by hand; Save from the new window (the save code path is
  the same as before but the form is new); unplugging a chosen device mid-recording.

## Backup device (2026-11-19)
- Each source has "If not connected": order is chosen device, backup, Windows default; checked every 2 s, so it
  moves back up as soon as a better one is connected. Settings: `SystemBackup`, `MicBackup`. Log says which one is used.
- Owner's mic: Focusrite "Analogue 1 + 2" first, Yeti as backup.
- Test: `test\backup-device-test.ps1 -Main ... -Backup ...` (a made-up id stands in for an unplugged device). 7/7.
- NOT tested: really unplugging the Focusrite mid-recording and plugging it back in.

## 3. Never tested
- Clicking the tray menu and how the icon looks (stop/start/exit were tested with the switches).
- Saving settings while recording is stopped.
- Stop on Windows sign-out, second-launch message.
- Ctrl+C in the console version.
- Sync after the 2026-11-16 04:30 audio changes (not re-measured; the timing rule is unchanged).
