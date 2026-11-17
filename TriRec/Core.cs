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

record Options(int Fps, int Qp, int Gop, int Segment, int Seconds, string OutDir, bool Audio, bool Mic, bool Hidden)
{
    public static Options Parse(string[] args, string defaultOut, bool hidden) => new(
        Int(args, "--fps", 10), Int(args, "--qp", 26), Int(args, "--gop", 20), Int(args, "--segment", 600),
        Int(args, "--seconds", 0), Path.GetFullPath(Str(args, "--out", defaultOut)),
        !args.Contains("--no-audio"), !args.Contains("--no-audio") && !args.Contains("--no-mic"), hidden);

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
        Log($"Canvas {cw}x{ch}, {o.Fps} fps, HEVC qp {o.Qp}");
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
        if (si >= 0)
        {
            if (mix) audio.Add($"[{si}:a]asplit=2[sys][sysmix]");
            tracks.Add((mix ? "[sys]" : $"{si}:a", "System", 2));
        }
        if (mi >= 0)
        {
            audio.Add($"[{mi}:a]{Mono}" + (mix ? ",asplit=2[mic][micmix]" : "[mic]"));
            tracks.Add(("[mic]", "Mic", 1));
        }
        if (mix)
        {
            // normalize=0 keeps both at full level instead of halving each
            audio.Add($"[sysmix]{Mono}[sysmono];[sysmono][micmix]amix=inputs=2:duration=longest:normalize=0[mix]");
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

        var session = new Session { OutDir = o.OutDir, Started = DateTime.Now };
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

    public AudioTap(bool loopback, string title, string pipeName)
    {
        this.loopback = loopback; Title = title; PipeName = pipeName;
        Open(out Rate, out Channels, out Fmt, out blockAlign);
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

    void Open(out int rate, out int channels, out string fmt, out int align)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out var dev);
        var iid = typeof(IAudioClient).GUID;
        dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
        var c = (IAudioClient)o;
        c.GetMixFormat(out IntPtr wf);
        try
        {
            int tag = (ushort)Marshal.ReadInt16(wf, 0), bits = Marshal.ReadInt16(wf, 14);
            channels = Marshal.ReadInt16(wf, 2);
            rate = Marshal.ReadInt32(wf, 4);
            align = Marshal.ReadInt16(wf, 12);
            bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(wf, 24) == 3);
            fmt = isFloat ? "f32le" : bits switch { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new Exception($"unsupported {bits}-bit audio") };
            c.Initialize(0, loopback ? 0x00020000 : 0, 10_000_000, 0, wf, IntPtr.Zero); // shared mode, 1 s buffer
        }
        finally { Marshal.FreeCoTaskMem(wf); }
        var iidCap = typeof(IAudioCaptureClient).GUID;
        c.GetService(ref iidCap, out var co);
        c.Start();
        client = c; cap = (IAudioCaptureClient)co;
    }

    // Try to follow a changed/reconnected default device; only usable if its format matches.
    void Reopen()
    {
        try
        {
            Open(out int r, out int ch, out string f, out _);
            if (r != Rate || ch != Channels || f != Fmt)
            {
                try { client.Stop(); } catch { }
                cap = null; client = null;
                Note($"new default device is {r} Hz {ch} ch {f}, not the {Rate} Hz {Channels} ch {Fmt} this recording started with; track stays silent");
            }
            else Note("device reconnected, capturing again");
        }
        catch (Exception e) { cap = null; client = null; Note("still no device (" + e.Message + ")"); }
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
            long produced = 0, nextReopen = 0;
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
                    aligned = true; anchor = vs;
                    skip = Math.Max(0, produced - Expected());
                }
                else if (!aligned && Expected() > 15L * Rate)
                {
                    aligned = true; // give up; say so once instead of leaving it silent
                    Note("ffmpeg did not report its first video frame; sound in this recording may run about a second ahead of the picture");
                }
                try
                {
                    if (cap != null) Drain(Emit, ref buf, () => skip <= 0 && produced - Expected() > Rate / 5);
                    else if (Environment.TickCount64 >= nextReopen) { nextReopen = Environment.TickCount64 + 2000; Reopen(); }
                }
                catch (Exception e) { cap = null; client = null; Note("device lost, writing silence until it returns (" + e.Message + ")"); } // keep the track alive

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
