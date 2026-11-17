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

## 3. Never tested
- Clicking the tray menu and how the icon looks (stop was tested with `--stop`).
- Stop on Windows sign-out, second-launch message.
- Ctrl+C in the console version.
- Sync after the 2026-11-16 04:30 audio changes (not re-measured; the timing rule is unchanged).
