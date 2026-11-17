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
//
// Messages go to trirec.log in the recordings folder.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    public bool Mic { get; set; } = true;

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
        Path.GetFullPath(outOverride ?? OutDir), SystemAudio, Mic, Hidden: true);
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

sealed class SettingsForm : Form
{
    static readonly (string Label, int Qp)[] Qualities =
    {
        ("Sharpest (largest files)", 20), ("High", 23), ("Standard", 26), ("Smaller files", 30), ("Smallest (soft text)", 34),
    };

    readonly TextBox folder = new() { Width = 330 };
    public readonly NumericUpDown Fps = new() { Minimum = 1, Maximum = 60, Width = 70 };
    readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 120, Width = 70 };
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly CheckBox sys = new() { Text = "Record system sound", AutoSize = true };
    readonly CheckBox mic = new() { Text = "Record microphone", AutoSize = true };
    readonly CheckBox auto = new() { Text = "Start with Windows", AutoSize = true };
    public readonly Button SaveButton = new() { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 0) };
    readonly List<int> qps = new();
    public bool Saved, SavedAutoStart;
    public Settings SavedSettings;

    public SettingsForm(Settings s, bool autoStart, Icon icon)
    {
        Text = "TriRec settings";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        foreach (var q in Qualities) { quality.Items.Add(q.Label); qps.Add(q.Qp); }
        if (!qps.Contains(s.Qp)) { quality.Items.Add($"Custom (qp {s.Qp})"); qps.Add(s.Qp); }
        quality.SelectedIndex = qps.IndexOf(s.Qp);
        folder.Text = s.OutDir;
        Fps.Value = Math.Clamp(s.Fps, 1, 60);
        minutes.Value = Math.Clamp(s.SegmentMinutes, 1, 120);
        sys.Checked = s.SystemAudio; mic.Checked = s.Mic; auto.Checked = autoStart;

        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Folder for recordings", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0) };
        cancel.Click += (_, _) => Close();
        SaveButton.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(Path.GetFullPath(folder.Text.Trim())); }
            catch (Exception e) { MessageBox.Show(this, "That folder cannot be used:\n\n" + e.Message, "TriRec"); return; }
            SavedSettings = Result(); SavedAutoStart = auto.Checked; // capture now: the controls are gone once the window closes
            Saved = true;
            Close();
        };
        AcceptButton = SaveButton; CancelButton = cancel;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) });
            c.Anchor = AnchorStyles.Left; c.Margin = new Padding(3, 5, 3, 5);
            grid.Controls.Add(c);
        }
        FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in cs) { c.Anchor = AnchorStyles.Left; f.Controls.Add(c); }
            return f;
        }
        Label Hint(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 6, 3, 3) };

        Row("Recordings folder", Flow(folder, browse));
        Row("Frames per second", Flow(Fps, Hint("10 is light; higher is smoother and heavier")));
        Row("Quality", quality);
        Row("New file every", Flow(minutes, Hint("minutes")));
        Row("Sound", Flow(sys, mic));
        Row("Windows", auto);
        var buttons = Flow(SaveButton, cancel);
        buttons.Anchor = AnchorStyles.Right; buttons.Margin = new Padding(3, 14, 3, 0);
        grid.Controls.Add(Hint("Saving restarts the recording (if one is running); a new file begins."));
        grid.SetColumnSpan(grid.Controls[^1], 2);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
    }

    Settings Result() => new()
    {
        OutDir = Path.GetFullPath(folder.Text.Trim()), Fps = (int)Fps.Value, Qp = qps[quality.SelectedIndex],
        SegmentMinutes = (int)minutes.Value, SystemAudio = sys.Checked, Mic = mic.Checked,
    };
}

static class Tray
{
    const string StopEventName = "TriRecTray.Stop", SettingsEventName = "TriRecTray.Settings", MutexName = "TriRecTray.SingleInstance",
                 PauseEventName = "TriRecTray.StopRecording", ResumeEventName = "TriRecTray.StartRecording";

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
            MessageBox.Show("TriRec is already running. Look for its dot in the system tray.", "TriRec");
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
        icon = new NotifyIcon { Icon = gray, Text = "TriRec", Visible = true };

        var menu = new ContextMenuStrip();
        var status = menu.Items.Add("Recording");
        status.Enabled = false;
        var toggle = menu.Items.Add("Stop recording", null, (_, _) => { if (paused) Resume(); else Pause(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open recordings folder", null, (_, _) => Process.Start(new ProcessStartInfo(opts.OutDir) { UseShellExecute = true })); // works for folder names with spaces or commas
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        menu.Opening += (_, _) => { status.Text = State(); toggle.Text = paused ? "Start recording" : "Stop recording"; };
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowSettings();

        StartRecording();
        if (args.Contains("--settings")) ShowSettings();

        // UI-thread timer: keeps the tooltip current, notices ffmpeg dying, and listens for --stop / --settings.
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            if (stopEvent.WaitOne(0)) { Application.Exit(); return; }
            if (settingsEvent.WaitOne(0)) ShowSettings();
            if (pauseEvent.WaitOne(0) && !paused) Pause();
            if (resumeEvent.WaitOne(0) && paused) Resume();
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
        form = new SettingsForm(settings, AutoStart.Enabled, red);
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

    // TriRecTray --settings-shot FILE.png [--shot-save]: draw the settings window to a picture;
    // with --shot-save also set 12 fps and press Save, to prove the save path writes the file.
    static void Shot(string[] args)
    {
        var f = new SettingsForm(Settings.Load(), AutoStart.Enabled, red);
        f.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(Options.Str(args, "--settings-shot", "settings.png"), ImageFormat.Png);
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
