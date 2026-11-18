// TriRecTray.exe: the recorder as a system tray app, with no main window.
//
// Starting it starts recording. "Stop recording" in the tray menu closes the current file
// properly and keeps the app in the tray (gray dot); "Start recording" begins a new file.
// "Exit" stops recording and quits.
// "Settings..." edits %APPDATA%\TriRec\settings.json; saving restarts the recording if one is running.
//
//   TriRecTray [--out DIR]       record (settings come from the settings file; --out overrides the folder for this run)
//   TriRecTray --stop-recording  ask the running tray app to stop recording and stay in the tray
//   TriRecTray --start-recording ask the running tray app to start recording
//   TriRecTray --exit            ask the running tray app to stop and exit (--stop is the old name)
//   TriRecTray --settings        ask the running tray app to open its settings window
//   TriRecTray --mark            mark this moment (same as Ctrl+Alt+M): appends to <file>.marks.txt
//
// Messages go to trirec.log in the recordings folder.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace TriRec;

sealed class Settings
{
    public string OutDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TriRec");
    public int Fps { get; set; } = 10;
    public int Qp { get; set; } = 26;
    public int SegmentMinutes { get; set; } = 10;
    public bool SystemAudio { get; set; } = true;
    public string SystemDevice { get; set; }          // null = Windows default playback device (followed)
    public int SystemChannels { get; set; } = 2;
    public int SystemVolume { get; set; } = 100;      // percent
    public bool Mic { get; set; } = true;
    public string MicDevice { get; set; }             // null = Windows default microphone (followed)
    public int MicChannels { get; set; } = 1;
    public int MicVolume { get; set; } = 100;
    public bool MixTrack { get; set; } = true;        // System + Mic together as one more track
    public int MixChannels { get; set; } = 1;
    public string FirstTrack { get; set; } = "Mix";   // written first and played by default
    public bool RecordOnLaunch { get; set; } = true;  // false: opening TriRec leaves it stopped in the tray
    // Sound moved this many ms later (negative = earlier). Leave out (null) for the measured automatic value.
    public int? AudioShiftMs { get; set; }

    // TRIREC_SETTINGS lets a test use its own file instead of the real one.
    public static string FilePath => Environment.GetEnvironmentVariable("TRIREC_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TriRec", "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Options ToOptions(string outOverride) => new(Fps, Qp, 20, SegmentMinutes * 60, 0,
        Path.GetFullPath(outOverride ?? OutDir), SystemAudio, Mic, Hidden: true, AudioShiftMs,
        new Source(SystemDevice, SystemChannels, SystemVolume), new Source(MicDevice, MicChannels, MicVolume),
        MixTrack, MixChannels, FirstTrack);

    public Settings Clone() => (Settings)MemberwiseClone();
}

// "Start with Windows" = TriRec.lnk in the current user's Startup folder, pointing at this exe.
// Every build is written to the same app folder, so the shortcut keeps opening the newest version.
// Only touched when the box is changed.
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "TriRec";
    static string Lnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "TriRec.lnk");

    public static bool Enabled => File.Exists(Lnk) || LegacyRun();

    static bool LegacyRun() // builds before 2026-11-17 used the Run key instead
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) != null;
    }

    public static void Set(bool on)
    {
        if (on)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic l = shell.CreateShortcut(Lnk);
            l.TargetPath = Environment.ProcessPath;
            l.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            l.Description = "TriRec: record all monitors from sign-in";
            l.Save();
        }
        else if (File.Exists(Lnk)) File.Delete(Lnk);
        if (LegacyRun()) { using var k = Registry.CurrentUser.OpenSubKey(RunKey, true); k.DeleteValue(Name); }
    }
}

// Global hotkey Ctrl+Alt+M (works while a game has focus). Ok is false if another program already owns it.
sealed class Hotkey : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    readonly Action pressed;
    public readonly bool Ok;

    public Hotkey(Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams());
        Ok = RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.M);
        if (!Ok) Recorder.Log("Ctrl+Alt+M is taken by another program; use the tray menu to mark moments");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == (IntPtr)1) pressed(); // WM_HOTKEY
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Ok) UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

static class Tray
{
    const string StopEventName = "TriRecTray.Stop", SettingsEventName = "TriRecTray.Settings", MutexName = "TriRecTray.SingleInstance",
                 PauseEventName = "TriRecTray.StopRecording", ResumeEventName = "TriRecTray.StartRecording", MarkEventName = "TriRecTray.Mark";

    static Settings settings;
    static Options opts;
    static string outOverride;
    static Session session;
    static bool paused;    // the owner stopped recording; the app stays in the tray
    static bool died;      // the recorder ended by itself and a restart is pending
    static int failures;   // restarts in a row that did not last
    static long retryAt;
    static NotifyIcon icon;
    static Icon red, gray;
    static SettingsForm form;
    static readonly object logLock = new();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--exit") || args.Contains("--stop")) { Signal(StopEventName); return; }
        if (args.Contains("--stop-recording")) { Signal(PauseEventName); return; }
        if (args.Contains("--start-recording")) { Signal(ResumeEventName); return; }
        if (args.Contains("--mark")) { Signal(MarkEventName); return; }
        if (args.Contains("--settings") && Signal(SettingsEventName)) return;
        // Same as ticking or clearing "Start with Windows" in Settings, without opening the window.
        if (args.Contains("--startup-on")) { AutoStart.Set(true); return; }
        if (args.Contains("--startup-off")) { AutoStart.Set(false); return; }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        red = Dot(Color.FromArgb(220, 40, 40));
        gray = Dot(Color.Gray);
        if (args.Contains("--settings-shot")) { Shot(args); return; } // test hook: draw the window to a PNG

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running (e.g. the taskbar pin was clicked): bring up its settings window instead.
            if (!Signal(SettingsEventName)) MessageBox.Show("TriRec is already running. Look for its dot in the system tray.", "TriRec");
            return;
        }

        settings = Settings.Load();
        outOverride = Options.Str(args, "--out", null);
        Recorder.Log = msg =>
        {
            lock (logLock)
                try { File.AppendAllText(Path.Combine(opts.OutDir, "trirec.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg.Trim()}{Environment.NewLine}"); } catch { }
        };

        using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
        using var settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        using var pauseEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PauseEventName);
        using var resumeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ResumeEventName);
        using var markEvent = new EventWaitHandle(false, EventResetMode.AutoReset, MarkEventName);
        using var hotkey = new Hotkey(Mark);
        icon = new NotifyIcon { Icon = gray, Text = "TriRec", Visible = true };

        var menu = new ContextMenuStrip();
        var status = menu.Items.Add("Recording");
        status.Enabled = false;
        var toggle = menu.Items.Add("Stop recording", null, (_, _) => { if (paused) Resume(); else Pause(); });
        menu.Items.Add(hotkey.Ok ? "Mark this moment (Ctrl+Alt+M)" : "Mark this moment", null, (_, _) => Mark());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open recordings folder", null, (_, _) => Process.Start(new ProcessStartInfo(opts.OutDir) { UseShellExecute = true })); // works for folder names with spaces or commas
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        menu.Opening += (_, _) => { status.Text = State(); toggle.Text = paused ? "Start recording" : "Stop recording"; };
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowSettings();

        if (settings.RecordOnLaunch) StartRecording();
        else { paused = true; opts = settings.ToOptions(outOverride); Recorder.Log("TriRec opened; recording not started (setting)"); }
        if (args.Contains("--settings")) ShowSettings();

        // UI-thread timer: keeps the tooltip current, notices ffmpeg dying, and listens for --stop / --settings.
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            if (stopEvent.WaitOne(0)) { Application.Exit(); return; }
            if (settingsEvent.WaitOne(0)) ShowSettings();
            if (pauseEvent.WaitOne(0) && !paused) Pause();
            if (resumeEvent.WaitOne(0) && paused) Resume();
            if (markEvent.WaitOne(0)) Mark();
            if (session != null && !died && session.Ff.HasExited)
            {
                // The recorder ended by itself: start it again, waiting longer each time it keeps failing
                // (10 s, 20 s, 40 s ... up to 5 min) so a lasting fault does not spin.
                died = true;
                session.Wall.Stop();
                icon.Icon = gray;
                if (session.Wall.Elapsed.TotalMinutes >= 2) failures = 0; // it had been running fine
                int wait = ScheduleRetry();
                Recorder.Log($"ffmpeg exited by itself with code {session.Ff.ExitCode}; starting again in {wait} s");
                icon.ShowBalloonTip(10000, "TriRec stopped recording",
                    $"The recorder ended by itself (code {session.Ff.ExitCode}). Starting again in {wait} seconds.", ToolTipIcon.Warning);
            }
            else if (died && form == null && Environment.TickCount64 >= retryAt)
            {
                session?.Stop(0); // already exited; this releases the sound devices
                session = null;
                if (StartRecording(quiet: true)) Recorder.Log("recording restarted");
                else { died = true; Recorder.Log($"trying again in {ScheduleRetry()} s"); }
            }
            icon.Text = "TriRec: " + State();
        };
        timer.Start();

        SystemEvents.SessionEnding += (_, _) => Application.Exit(); // sign-out or shutdown: close the file first
        Application.Run();

        // Reached from the menu item, --stop, or sign-out.
        timer.Stop();
        icon.Visible = false;
        StopRecording();
        icon.Dispose();
    }

    // Ctrl+Alt+M or the menu: note the time in the file being recorded, in "<file>.marks.txt" next to it,
    // so a moment can be found later without scrubbing. Times are from the file name (its start), about ±1 s.
    static void Mark()
    {
        if (session == null || paused || died) { icon.ShowBalloonTip(2000, "TriRec", "Not recording; nothing marked.", ToolTipIcon.None); return; }
        var now = DateTime.Now;
        var file = new DirectoryInfo(opts.OutDir).GetFiles("*.mkv")
            .Where(f => f.LastWriteTime >= session.Started.AddSeconds(-5))
            .OrderByDescending(f => f.Name).FirstOrDefault();
        if (file == null || !DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd_HH-mm-ss",
                null, System.Globalization.DateTimeStyles.None, out var start))
        {
            Recorder.Log("mark: no file being recorded was found");
            return;
        }
        var at = now - start;
        if (at < TimeSpan.Zero) at = TimeSpan.Zero;
        string marks = Path.ChangeExtension(file.FullName, ".marks.txt");
        try { File.AppendAllText(marks, $"{(int)at.TotalHours:00}:{at:mm\\:ss}  (clock {now:HH:mm:ss}){Environment.NewLine}"); }
        catch (Exception e) { Recorder.Log("mark: could not write " + marks + ": " + e.Message); return; }
        Recorder.Log($"mark at {at:hh\\:mm\\:ss} in {file.Name}");
        icon.ShowBalloonTip(2000, "TriRec", $"Marked {(int)at.TotalHours:00}:{at:mm\\:ss} in {file.Name}", ToolTipIcon.None);
    }

    // Stop recording but stay in the tray, like OBS. Also cancels a pending automatic restart.
    static void Pause()
    {
        Cursor.Current = Cursors.WaitCursor;
        paused = true;
        if (died) { session?.Stop(0); session = null; died = false; } // already exited; release the sound devices
        else StopRecording();
        icon.Icon = gray;
        Recorder.Log("recording stopped from the tray; app still running");
        Cursor.Current = Cursors.Default;
    }

    static void Resume()
    {
        paused = false;
        if (StartRecording()) Recorder.Log("recording started from the tray");
        else paused = true; // could not start (message already shown): stay stopped, no automatic retries
    }

    static string State()
    {
        if (paused) return "Stopped (right-click, Start recording)";
        if (died) return "Recording stopped; starting again shortly";
        if (session == null) return "Not recording (check Settings)";
        var t = session.Wall.Elapsed;
        return $"Recording for {(int)t.TotalHours}h {t.Minutes:00}m";
    }

    static int ScheduleRetry()
    {
        int wait = Math.Min(300, 10 << Math.Min(failures++, 5));
        retryAt = Environment.TickCount64 + wait * 1000L;
        return wait;
    }

    // quiet = an automatic retry: no message box, the log line is enough.
    static bool StartRecording(bool quiet = false)
    {
        opts = settings.ToOptions(outOverride);
        died = false;
        if (!quiet) failures = 0;
        try
        {
            Directory.CreateDirectory(opts.OutDir);
            session = Recorder.Start(opts);
            icon.Icon = red;
            return true;
        }
        catch (Exception e)
        {
            session = null;
            icon.Icon = gray;
            Recorder.Log("could not start: " + e.Message);
            if (!quiet) MessageBox.Show("TriRec could not start recording:\n\n" + e.Message, "TriRec", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    static void StopRecording()
    {
        if (session == null) return;
        bool clean = session.Stop(15000);
        Recorder.Log(clean ? $"Stopped after {session.Wall.Elapsed.TotalMinutes:F1} min, ffmpeg exit code {session.Ff.ExitCode}"
                           : "ffmpeg did not stop within 15 s and was ended; the last file may be missing its final seconds");
        session = null;
    }

    static void ShowSettings()
    {
        if (form != null) { form.Activate(); return; }
        form = new SettingsForm(settings, AutoStart.Enabled, AppIcon(), State);
        form.FormClosed += (_, _) =>
        {
            var f = form;
            form = null;
            if (!f.Saved) return;
            settings = Persist(f);
            if (paused) { opts = settings.ToOptions(outOverride); return; } // stopped: keep it stopped, use the new settings next time
            Cursor.Current = Cursors.WaitCursor;
            StopRecording();
            StartRecording();
            Cursor.Current = Cursors.Default;
        };
        form.Show();
        form.Activate();
    }

    static Settings Persist(SettingsForm f)
    {
        var s = f.SavedSettings;
        s.Save();
        if (f.SavedAutoStart != AutoStart.Enabled) AutoStart.Set(f.SavedAutoStart);
        return s;
    }

    static bool Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var ev)) return false;
        ev.Set();
        return true;
    }

    // TriRecTray --settings-shot FILE.png [--shot-save]: picture of each settings tab (FILE-1.png, FILE-2.png ...),
    // taken from the screen; with --shot-save also set 12 fps and press Save, to prove the save path writes the file.
    static void Shot(string[] args)
    {
        var f = new SettingsForm(Settings.Load(), AutoStart.Enabled, AppIcon(), () => "Recording for 0h 12m (preview)") { TopMost = true };
        f.Show();
        string file = Options.Str(args, "--settings-shot", "settings.png");
        for (int i = 0; i < f.Tabs.TabCount; i++)
        {
            f.Tabs.SelectedIndex = i;
            var until = Environment.TickCount64 + 600; // let the level meters and painting settle
            while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(15); }
            var b = f.Bounds;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
            bmp.Save(Path.ChangeExtension(file, null) + $"-{i + 1}.png", ImageFormat.Png);
        }
        if (args.Contains("--shot-save"))
        {
            f.Fps.Value = 12;
            f.SaveButton.PerformClick();
            Application.DoEvents();
            if (f.Saved) Persist(f);
        }
        else f.Close();
    }

    // The exe's own icon (trirec.ico), so the settings window matches the taskbar pin.
    static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? red; } catch { return red; }
    }

    static Icon Dot(Color c)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(c);
            g.FillEllipse(brush, 4, 4, 24, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
