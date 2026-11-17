// TriRec core, shared by the console program (trirec.exe) and the tray app (TriRecTray.exe).
//
// Video: ffmpeg ddagrab (Desktop Duplication) per monitor -> NV12 on the GPU ->
//        packed onto one canvas -> ONE hevc_nvenc encoder.
// Audio: WASAPI loopback of the default playback device plus the default
//        microphone, fed to ffmpeg over named pipes as two separate tracks.
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TriRec;

record Mon(int Idx, int W, int H, int X, int Y);

// AudioShiftMs: all sound is moved this much later (negative = earlier) against the picture. null = AutoShift(fps).
record Options(int Fps, int Qp, int Gop, int Segment, int Seconds, string OutDir, bool Audio, bool Mic, bool Hidden,
               int? AudioShiftMs = null)
{
    public int Shift => AudioShiftMs ?? AutoShift(Fps);

    // Measured 2026-11-17 with test\sync-test.ps1 (sound vs picture with no shift): 10 fps -0.03 s, 15 fps +0.10 s,
    // 23 fps +0.20 s, 30 fps +0.20 s (positive = sound late). The shift undoes that, straight lines in between.
    // Other frame rates are interpolated, not measured.
    public static int AutoShift(int fps)
    {
        (int f, int ms)[] p = { (10, 30), (15, -100), (23, -200), (30, -200) };
        if (fps <= p[0].f) return p[0].ms;
        for (int i = 1; i < p.Length; i++)
            if (fps <= p[i].f) return p[i - 1].ms + (p[i].ms - p[i - 1].ms) * (fps - p[i - 1].f) / (p[i].f - p[i - 1].f);
        return p[^1].ms;
    }

    public static Options Parse(string[] args, string defaultOut, bool hidden) => new(
        Int(args, "--fps", 10), Int(args, "--qp", 26), Int(args, "--gop", 20), Int(args, "--segment", 600),
        Int(args, "--seconds", 0), Path.GetFullPath(Str(args, "--out", defaultOut)),
        !args.Contains("--no-audio"), !args.Contains("--no-audio") && !args.Contains("--no-mic"), hidden,
        args.Contains("--audio-shift") ? Int(args, "--audio-shift", 0) : null);

    public static string Str(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }

    public static int Int(string[] args, string name, int def) => int.Parse(Str(args, name, def.ToString()));
}

// A running recording: the ffmpeg process plus the audio feeder threads.
sealed class Session
{
    public Process Ff;
    public DateTime Started;
    public readonly Stopwatch Wall = Stopwatch.StartNew();
    public string OutDir;
    internal CancellationTokenSource Cts = new();
    // Stopwatch timestamp of the moment ffmpeg captured its first video frames (0 = not known yet).
    internal long VideoStart;
    // Sync correction in Stopwatch ticks: the audio timeline starts this much before VideoStart (negative = after).
    internal long ShiftTicks;

    // Ask ffmpeg to finish the current file properly ("q" on its stdin).
    public void RequestStop()
    {
        try { Ff.StandardInput.Write("q"); Ff.StandardInput.Flush(); } catch { }
    }

    // Clean stop; returns false if ffmpeg had to be killed after the timeout.
    public bool Stop(int timeoutMs)
    {
        bool clean = true;
        if (!Ff.HasExited)
        {
            RequestStop();
            if (!Ff.WaitForExit(timeoutMs)) { clean = false; try { Ff.Kill(); } catch { } }
        }
        Cts.Cancel();
        return clean;
    }
}

static class Recorder
{
    const int MaxDim = 8192; // HEVC NVENC limit per side
    public static Action<string> Log = Console.WriteLine;

    public static Session Start(Options o)
    {
        Directory.CreateDirectory(o.OutDir);
        var (mons, cw, ch) = Layout(Probe());
        Log($"Canvas {cw}x{ch}, {o.Fps} fps, HEVC qp {o.Qp}, sound shifted {o.Shift:+0;-0} ms{(o.AudioShiftMs == null ? " (auto)" : "")}");
        for (int i = 0; i < mons.Count; i++)
            Log($"  monitor {i + 1}: {mons[i].W}x{mons[i].H} at ({mons[i].X},{mons[i].Y})");

        File.WriteAllText(Path.Combine(o.OutDir, "layout.json"), JsonSerializer.Serialize(new
        {
            canvas = new { w = cw, h = ch },
            fps = o.Fps,
            monitors = mons.Select((m, i) => new { number = i + 1, w = m.W, h = m.H, x = m.X, y = m.Y }),
        }, new JsonSerializerOptions { WriteIndented = true }));

        var taps = new List<AudioTap>();
        if (o.Audio) AddTap(taps, true, "System");
        if (o.Mic) AddTap(taps, false, "Mic");

        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = o.Hidden,
        };
        var a = psi.ArgumentList;
        // level+verbose: we read ffmpeg's messages ourselves (see below) and need the line that marks the first
        // captured frame. ffmpeg looks for the "q" stop key only once per stats period, so that stays short.
        foreach (var s in new[] { "-hide_banner", "-loglevel", "level+verbose", o.Hidden ? "-nostats" : "-stats", "-stats_period", o.Hidden ? "0.5" : "2", "-y" }) a.Add(s);
        foreach (var t in taps)
            foreach (var s in new[] { "-f", t.Fmt, "-ar", t.Rate.ToString(), "-ac", t.Channels.ToString(),
                         "-probesize", "32", "-analyzeduration", "0", "-thread_queue_size", "4096",
                         "-i", @"\\.\pipe\" + t.PipeName }) a.Add(s);

        // The screen grabs are sources inside the filter graph (as separate ffmpeg inputs they delivered
        // frames in bursts). fps= snaps all monitors to the same timestamps so the canvas is composed once per frame.
        string grab = string.Join(";", mons.Select((m, i) =>
            $"ddagrab=output_idx={m.Idx}:framerate={o.Fps},scale_d3d11=format=nv12,hwdownload,format=nv12,fps={o.Fps}[m{i}]"));
        string stack = mons.Count == 1
            ? "[m0]null[v]"
            : string.Concat(mons.Select((_, i) => $"[m{i}]")) +
              $"xstack=inputs={mons.Count}:layout={string.Join("|", mons.Select(m => $"{m.X}_{m.Y}"))}:fill=black[v]";
        // Sound tracks: Mix = System + Mic in mono (track 1, default), System (stereo, as played), Mic (mono).
        // Each input is the wall-clock-aligned pipe from its tap, so the two line up in the mix.
        var audio = new List<string>();
        var tracks = new List<(string Pad, string Title, int Channels)>();
        int si = taps.FindIndex(t => t.Title == "System"), mi = taps.FindIndex(t => t.Title == "Mic");
        bool mix = si >= 0 && mi >= 0;
        const string Mono = "aresample=48000,aformat=sample_rates=48000:channel_layouts=mono";
        string In(int i) => $"[{i}:a]";
        if (si >= 0)
        {
            audio.Add(In(si) + (mix ? "asplit=2[sys][sysmix]" : "anull[sys]"));
            tracks.Add(("[sys]", "System", 2));
        }
        if (mi >= 0)
        {
            audio.Add(In(mi) + Mono + (mix ? ",asplit=2[mic][micmix]" : "[mic]"));
            tracks.Add(("[mic]", "Mic", 1));
        }
        if (mix)
        {
            // normalize=0 keeps both at full level instead of halving each; the limiter then keeps loud moments
            // (game sound plus speech) under -1 dB instead of clipping. level=0: no automatic gain, latency=1: no delay.
            audio.Add($"[sysmix]{Mono}[sysmono];[sysmono][micmix]amix=inputs=2:duration=longest:normalize=0," +
                      "alimiter=limit=0.891:attack=5:release=50:level=0:latency=1[mix]");
            tracks.Insert(0, ("[mix]", "Mix", 1)); // first track, and the one players pick by default
        }
        a.Add("-filter_complex"); a.Add(string.Join(";", new[] { grab, stack }.Concat(audio)));

        a.Add("-map"); a.Add("[v]");
        for (int i = 0; i < tracks.Count; i++)
        {
            a.Add("-map"); a.Add(tracks[i].Pad);
            a.Add($"-metadata:s:a:{i}"); a.Add("title=" + tracks[i].Title);
            a.Add($"-ac:a:{i}"); a.Add(tracks[i].Channels.ToString());
            a.Add($"-disposition:a:{i}"); a.Add(i == 0 ? "default" : "0");
        }
        foreach (var s in new[] { "-c:v", "hevc_nvenc", "-preset", "p3", "-rc", "constqp", "-qp", o.Qp.ToString(),
                     "-g", (o.Fps * o.Gop).ToString() }) a.Add(s);
        if (tracks.Count > 0) foreach (var s in new[] { "-c:a", "libopus", "-b:a", "96k" }) a.Add(s);
        if (o.Seconds > 0) { a.Add("-t"); a.Add(o.Seconds.ToString()); }
        foreach (var s in new[] { "-f", "segment", "-segment_time", o.Segment.ToString(), "-segment_format", "matroska",
                     "-reset_timestamps", "1", "-strftime", "1",
                     Path.Combine(o.OutDir, "%Y-%m-%d_%H-%M-%S.mkv") }) a.Add(s);

        var session = new Session { OutDir = o.OutDir, Started = DateTime.Now, ShiftTicks = o.Shift * Stopwatch.Frequency / 1000 };
        try { session.Ff = Process.Start(psi); }
        catch { foreach (var t in taps) t.Close(); throw; } // ffmpeg missing: free the devices and pipes
        foreach (var t in taps) new Thread(() => t.Run(session.Cts.Token, session)) { IsBackground = true }.Start();

        // Read ffmpeg's messages. Each monitor's chain logs "Set first pts to 0" when its first frame
        // arrives: that is time zero of the video, and the audio taps start their clock from it.
        // Only warnings/errors (and, in the console, the progress line) are passed on.
        int firstFrames = 0; long stampSum = 0; bool progressShown = false;
        session.Ff.ErrorDataReceived += (_, e) =>
        {
            string line = e.Data;
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("Set first pts to"))
            {
                stampSum += Stopwatch.GetTimestamp();
                if (++firstFrames == mons.Count) Volatile.Write(ref session.VideoStart, stampSum / mons.Count);
                return;
            }
            var m = Regex.Match(line, @"\[(warning|error|fatal|panic)\] ");
            if (m.Success)
            {
                string msg = line.Remove(m.Index, m.Length).Trim();
                if (o.Hidden) Log("ffmpeg: " + msg);
                else { if (progressShown) { Console.WriteLine(); progressShown = false; } Console.WriteLine(msg); }
            }
            else if (!o.Hidden)
            {
                int i = line.IndexOf("[info] frame=");
                if (i >= 0) { Console.Write("\r" + line[(i + 7)..].TrimEnd()); progressShown = true; }
            }
        };
        session.Ff.BeginErrorReadLine();
        Log($"Recording to {o.OutDir}");
        return session;
    }

    static int pipeSerial;

    static void AddTap(List<AudioTap> taps, bool loopback, string title)
    {
        try
        {
            // A new pipe name for every recording: the tray app starts several in one process.
            var t = new AudioTap(loopback, title, $"trirec_{title.ToLowerInvariant()}_{Environment.ProcessId}_{Interlocked.Increment(ref pipeSerial)}");
            taps.Add(t);
            Log($"  audio {title}: {t.Rate} Hz, {t.Channels} ch, {t.Fmt}");
        }
        catch (Exception e)
        {
            Log($"  audio {title}: unavailable, recording without it ({e.Message})");
        }
    }

    // Ask ffmpeg which Desktop Duplication outputs exist and how big they are.
    static List<(int idx, int w, int h)> Probe()
    {
        var list = new List<(int, int, int)>();
        for (int i = 0; i < 8; i++)
        {
            string err = RunCapture("ffmpeg", $"-hide_banner -f lavfi -i ddagrab=output_idx={i}:framerate=1 -frames:v 1 -f null -");
            var m = Regex.Match(err, @"wrapped_avframe, d3d11, (\d+)x(\d+)");
            if (!m.Success) break;
            list.Add((i, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        }
        if (list.Count == 0) throw new Exception("ffmpeg could not capture any monitor (is ffmpeg on PATH?)");
        return list;
    }

    // Pack monitors onto one canvas: a single row if it fits, otherwise the
    // largest monitor on the left and the rest stacked in a column beside it.
    static (List<Mon>, int w, int h) Layout(List<(int idx, int w, int h)> src)
    {
        var mons = new List<Mon>();
        int cw, ch;
        if (src.Sum(m => m.w) <= MaxDim)
        {
            int x = 0;
            foreach (var m in src) { mons.Add(new Mon(m.idx, m.w, m.h, x, 0)); x += m.w; }
            cw = x; ch = src.Max(m => m.h);
        }
        else
        {
            var s = src.OrderByDescending(m => (long)m.w * m.h).ToList();
            mons.Add(new Mon(s[0].idx, s[0].w, s[0].h, 0, 0));
            int y = 0;
            foreach (var m in s.Skip(1)) { mons.Add(new Mon(m.idx, m.w, m.h, s[0].w, y)); y += m.h; }
            cw = s[0].w + s.Skip(1).Max(m => m.w); ch = Math.Max(s[0].h, y);
        }
        cw += cw & 1; ch += ch & 1;
        if (cw > MaxDim || ch > MaxDim)
            throw new Exception($"monitors need a {cw}x{ch} canvas, over the {MaxDim}x{MaxDim} encoder limit");
        return (mons, cw, ch);
    }

    static string RunCapture(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return err;
    }
}

// One WASAPI capture (loopback of the default playback device, or the default
// microphone) streamed as raw PCM into a named pipe that ffmpeg reads.
// The stream is kept on the wall clock: silence is inserted when the device
// delivers nothing (loopback is silent when nothing plays), so audio cannot
// drift away from the video over a long recording.
sealed class AudioTap
{
    public readonly string Title, PipeName;
    public int Rate, Channels;
    public string Fmt;
    readonly bool loopback;
    int blockAlign;
    IAudioClient client;
    IAudioCaptureClient cap;
    readonly NamedPipeServerStream pipe;
    byte[] format;    // the WAVEFORMATEX this recording uses; every device after the first is asked for it too
    string deviceId;  // the device being captured, to notice when the Windows default changes

    const int Loopback = 0x00020000, AutoConvert = unchecked((int)0x80000000) | 0x08000000; // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
    // Test hooks: TRIREC_TEST_RATE asks for another rate (proves Windows' conversion), TRIREC_TEST_SWITCH_SEC
    // acts as if the default device changed that many seconds in.
    static readonly int TestRate = int.TryParse(Environment.GetEnvironmentVariable("TRIREC_TEST_RATE"), out int r) ? r : 48000;
    static readonly int TestSwitchSec = int.TryParse(Environment.GetEnvironmentVariable("TRIREC_TEST_SWITCH_SEC"), out int s) ? s : 0;

    public AudioTap(bool loopback, string title, string pipeName)
    {
        this.loopback = loopback; Title = title; PipeName = pipeName;
        Open(first: true);
        // ~100 ms of pipe buffer: small, so little stale audio sits in it while ffmpeg starts up
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, Rate * blockAlign / 10); // asynchronous so waiting for ffmpeg can be cancelled
    }

    // Release the device and the pipe. Without this the pipe name stays taken until the process ends.
    public void Close()
    {
        try { client?.Stop(); } catch { }
        cap = null; client = null;
        try { pipe.Dispose(); } catch { }
    }

    // Open the current default device. The first time, ask Windows for a fixed format (48 kHz float; stereo
    // for system sound, mono for the mic) and let it convert, so a later default device with another rate or
    // channel count still fits this recording. If a device refuses that, fall back to its own format.
    void Open(bool first)
    {
        var dev = DefaultDevice(out string id);
        int flags = loopback ? Loopback : 0;
        IAudioClient c;
        if (first)
        {
            format = Wave(TestRate, loopback ? 2 : 1);
            c = Activate(dev);
            if (!TryInit(c, flags | AutoConvert, format))
            {
                c = Activate(dev); // a client whose Initialize failed cannot be used again
                c.GetMixFormat(out IntPtr wf);
                try { format = Copy(wf); } finally { Marshal.FreeCoTaskMem(wf); }
                Init(c, flags, format);
                Recorder.Log($"  audio {Title}: device refused the fixed format; using its own (a later device switch only works if the format matches)");
            }
            int tag = BitConverter.ToUInt16(format, 0), bits = BitConverter.ToInt16(format, 14);
            Channels = BitConverter.ToInt16(format, 2);
            Rate = BitConverter.ToInt32(format, 4);
            blockAlign = BitConverter.ToInt16(format, 12);
            bool isFloat = tag == 3 || (tag == 0xFFFE && BitConverter.ToInt32(format, 24) == 3);
            Fmt = isFloat ? "f32le" : bits switch { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new Exception($"unsupported {bits}-bit audio") };
        }
        else
        {
            c = Activate(dev);
            Init(c, flags | AutoConvert, format);
        }
        var iidCap = typeof(IAudioCaptureClient).GUID;
        c.GetService(ref iidCap, out var co);
        c.Start();
        client = c; cap = (IAudioCaptureClient)co; deviceId = id;
    }

    IMMDevice DefaultDevice(out string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); // a fresh one on the calling thread
        en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out var dev);
        dev.GetId(out IntPtr p);
        try { id = Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        return dev;
    }

    static IAudioClient Activate(IMMDevice dev)
    {
        var iid = typeof(IAudioClient).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
        return (IAudioClient)o;
    }

    static void Init(IAudioClient c, int flags, byte[] wf)
    {
        IntPtr p = Marshal.AllocHGlobal(wf.Length);
        try { Marshal.Copy(wf, 0, p, wf.Length); c.Initialize(0, flags, 10_000_000, 0, p, IntPtr.Zero); } // shared mode, 1 s buffer
        finally { Marshal.FreeHGlobal(p); }
    }

    static bool TryInit(IAudioClient c, int flags, byte[] wf)
    {
        try { Init(c, flags, wf); return true; } catch (COMException) { return false; }
    }

    static byte[] Copy(IntPtr wf)
    {
        int tag = (ushort)Marshal.ReadInt16(wf, 0);
        var b = new byte[tag == 1 ? 18 : 18 + Marshal.ReadInt16(wf, 16)]; // plain PCM has no cbSize field
        Marshal.Copy(wf, b, 0, tag == 1 ? 16 : b.Length);
        return b;
    }

    // WAVEFORMATEXTENSIBLE, 32-bit float.
    static byte[] Wave(int rate, int channels)
    {
        var b = new byte[40];
        void W16(int at, int v) => BitConverter.GetBytes((short)v).CopyTo(b, at);
        void W32(int at, int v) => BitConverter.GetBytes(v).CopyTo(b, at);
        W16(0, 0xFFFE); W16(2, channels); W32(4, rate); W32(8, rate * channels * 4); W16(12, channels * 4); W16(14, 32);
        W16(16, 22); W16(18, 32); W32(20, channels == 1 ? 4 : 3); // mono = centre, stereo = left|right
        new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray().CopyTo(b, 24); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        return b;
    }

    // Switch to the current default device (after it changed or the old one was lost).
    void Reopen(string why)
    {
        try { client?.Stop(); } catch { }
        cap = null; client = null;
        try { Open(first: false); Note(why + ", capturing from the default device"); }
        catch (Exception e) { Note("no usable device yet, writing silence (" + e.Message + ")"); }
    }

    bool DefaultChanged()
    {
        try { DefaultDevice(out string id); return id != deviceId; }
        catch { return true; } // no default device at all: Reopen will say so
    }

    string lastNote;
    void Note(string msg)
    {
        if (msg == lastNote) return; // do not repeat the same line every 2 s
        lastNote = msg;
        Recorder.Log($"[{DateTime.Now:HH:mm:ss}] audio {Title}: {msg}");
    }

    public void Run(CancellationToken ct, Session session)
    {
        // Capture and pipe-writing are separate threads with a queue between them. ffmpeg reads audio
        // about as far behind real time as its video pipeline lags (over a second), so writes to the
        // pipe block routinely; the queue holds that standing backlog so no audio is lost.
        var queue = new System.Collections.Concurrent.BlockingCollection<byte[]>(2000); // ~20 s of 10 ms packets
        bool pipeClosed = false;
        try
        {
            pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
            byte[] buf = new byte[1 << 16];
            Drain(null, ref buf); // throw away what the device collected while ffmpeg was starting
            long connected = Stopwatch.GetTimestamp();
            new Thread(() =>
            {
                try { foreach (var b in queue.GetConsumingEnumerable(ct)) pipe.Write(b, 0, b.Length); }
                catch { } // ffmpeg closed the pipe, or we were cancelled
                finally { pipeClosed = true; }
            }) { IsBackground = true }.Start();

            // ffmpeg needs audio to arrive straight away (it will not finish opening the pipe otherwise),
            // so the clock starts when the pipe opens. About half a second later ffmpeg captures its first
            // video frame; at that point the clock is moved to that instant and the same amount of incoming
            // audio is skipped, so from then on sample N of this track and the video share one timeline.
            long anchor = connected, skip = 0;
            bool aligned = false;
            // The audio timeline is the wall clock since the anchor. Every frame produced is
            // counted against it: silence is added when the device delivers nothing (loopback is silent
            // when nothing plays), and packets are skipped if the device runs fast.
            long produced = 0, nextReopen = 0, nextCheck = 0;
            long testSwitchAt = TestSwitchSec > 0 ? Environment.TickCount64 + TestSwitchSec * 1000L : long.MaxValue;
            long Expected() => (long)((Stopwatch.GetTimestamp() - anchor) * (double)Rate / Stopwatch.Frequency);
            int chunk = Rate / 50; // 20 ms of frames
            byte[] zeros = new byte[chunk * blockAlign];

            void Emit(byte[] b, int bytes, int frames)
            {
                if (skip > 0) { skip -= frames; return; }
                var copy = new byte[bytes];
                Buffer.BlockCopy(b, 0, copy, 0, bytes);
                if (queue.TryAdd(copy)) produced += frames; // if ffmpeg stalled for 20 s the packet is dropped and silence fills in later
            }

            while (!ct.IsCancellationRequested && !pipeClosed)
            {
                long vs;
                if (!aligned && (vs = Volatile.Read(ref session.VideoStart)) != 0)
                {
                    // Shifted for sync: an earlier anchor puts sound later (silence is added), a later one
                    // puts it earlier (the extra audio is skipped).
                    aligned = true; anchor = vs - session.ShiftTicks;
                    skip = Math.Max(0, produced - Expected());
                }
                else if (!aligned && Expected() > 15L * Rate)
                {
                    aligned = true; // give up; say so once instead of leaving it silent
                    Note("ffmpeg did not report its first video frame; sound in this recording may run about a second ahead of the picture");
                }
                try
                {
                    if (cap != null)
                    {
                        Drain(Emit, ref buf, () => skip <= 0 && produced - Expected() > Rate / 5);
                        // Every 2 s: has Windows' default device changed (e.g. switched to headphones)? Then follow it.
                        if (Environment.TickCount64 >= nextCheck)
                        {
                            nextCheck = Environment.TickCount64 + 2000;
                            bool test = Environment.TickCount64 >= testSwitchAt;
                            if (test) testSwitchAt = long.MaxValue;
                            if (test || DefaultChanged()) Reopen(test ? "test: pretended the default device changed" : "default device changed");
                        }
                    }
                    else if (Environment.TickCount64 >= nextReopen) { nextReopen = Environment.TickCount64 + 2000; Reopen("device back"); }
                }
                catch (Exception e) { try { client?.Stop(); } catch { } cap = null; client = null; Note("device lost, writing silence until it returns (" + e.Message + ")"); } // keep the track alive

                long behind = Expected() - produced;
                if (behind > Rate / 10)
                    for (; behind > Rate / 20 && queue.Count < queue.BoundedCapacity; behind -= chunk) Emit(zeros, zeros.Length, chunk);
                Thread.Sleep(10);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        finally { Close(); }
    }

    // Read every pending packet; write it (or discard when write is null / skip() says we are ahead of the clock).
    void Drain(Action<byte[], int, int> write, ref byte[] buf, Func<bool> skip = null)
    {
        cap.GetNextPacketSize(out uint n);
        while (n > 0)
        {
            cap.GetBuffer(out IntPtr p, out uint frames, out uint flags, out _, out _);
            int bytes = (int)frames * blockAlign;
            if (buf.Length < bytes) buf = new byte[bytes];
            if ((flags & 2) != 0) Array.Clear(buf, 0, bytes); // AUDCLNT_BUFFERFLAGS_SILENT
            else Marshal.Copy(p, buf, 0, bytes);
            cap.ReleaseBuffer(frames);
            if (write != null && !(skip?.Invoke() ?? false)) write(buf, bytes, (int)frames);
            cap.GetNextPacketSize(out n);
        }
    }
}

// ------------------------------------------------------- WASAPI COM interop

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IntPtr store);
    void GetId(out IntPtr id);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    void GetBufferSize(out uint frames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint frames);
    void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr handle);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint frames);
    void GetNextPacketSize(out uint frames);
}
