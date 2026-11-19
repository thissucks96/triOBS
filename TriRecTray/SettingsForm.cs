// The settings window: three tabs (Recording, Sound, Advanced) and a status line.
// Changes are kept in the window until Save; Tray applies them (and restarts a running recording).
using System.Diagnostics;

namespace TriRec;

static class Ui
{
    public static readonly Font Body = new("Segoe UI", 9f);
    public static readonly Font Heading = new("Segoe UI Semibold", 10f);
    public static readonly Color Gray = Color.FromArgb(96, 96, 96);

    public static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true };
    public static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Gray, MaximumSize = new Size(470, 0) };
    public static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(0, 27) };

    public static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in cs) { c.Anchor = AnchorStyles.Left; f.Controls.Add(c); }
        return f;
    }
}

// A two-column page: section headings, "label | control" rows, and grey hints.
sealed class Page
{
    public readonly TableLayoutPanel T = new()
    {
        ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 10, 14, 10),
    };

    public void Section(string text)
    {
        var l = new Label { Text = text, AutoSize = true, Font = Ui.Heading, Margin = new Padding(0, T.Controls.Count == 0 ? 2 : 16, 0, 4) };
        Full(l);
    }

    public void Row(string label, Control c)
    {
        T.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 18, 6) });
        c.Anchor = AnchorStyles.Left; c.Margin = new Padding(3, 4, 3, 4);
        T.Controls.Add(c);
    }

    public void HintRow(string text) { var h = Ui.Hint(text); T.Controls.Add(new Label { AutoSize = true, Text = "" }); h.Margin = new Padding(3, 0, 3, 6); T.Controls.Add(h); }

    public void Full(Control c)
    {
        T.Controls.Add(c);
        T.SetColumnSpan(c, 2);
    }
}

// Live level bar, -60 to 0 dB: green, amber from -12 dB, red from -3 dB, with a falling peak line.
sealed class LevelMeter : Control
{
    float peak, hold;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(390, 8);
    }

    public float Peak { set { peak = value; hold = Math.Max(value, hold * 0.93f); Invalidate(); } }

    static float Frac(float v) => v <= 0.001f ? 0 : Math.Clamp((float)(20 * Math.Log10(v) + 60) / 60f, 0, 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var r = ClientRectangle;
        using (var bg = new SolidBrush(Enabled ? Color.FromArgb(222, 225, 230) : Color.FromArgb(238, 238, 238))) g.FillRectangle(bg, r);
        if (!Enabled) return;
        int w = (int)(r.Width * Frac(peak)), a = (int)(r.Width * 0.8f), b = (int)(r.Width * 0.95f);
        void Fill(Color c, int x, int width) { if (width <= 0) return; using var br = new SolidBrush(c); g.FillRectangle(br, x, 0, width, r.Height); }
        Fill(Color.FromArgb(46, 160, 67), 0, Math.Min(w, a));
        Fill(Color.FromArgb(219, 160, 0), a, Math.Min(w, b) - a);
        Fill(Color.FromArgb(207, 34, 46), b, w - b);
        int h = (int)(r.Width * Frac(hold));
        if (h > 1) using (var p = new Pen(Color.FromArgb(70, 70, 70))) g.DrawLine(p, h - 1, 0, h - 1, r.Height);
    }
}

// The controls for one sound source (system sound or microphone), in a group box.
sealed class SourcePanel
{
    public readonly bool Render;
    public readonly CheckBox On;
    public readonly ComboBox Device = Ui.Combo(390), Backup = Ui.Combo(390), Channels = Ui.Combo(110);
    public readonly TrackBar Volume = new() { Minimum = 0, Maximum = 200, TickFrequency = 25, SmallChange = 5, LargeChange = 25, AutoSize = false, Size = new Size(240, 30) };
    readonly Label volumeText = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly LevelMeter meter = new();
    readonly List<string> ids = new(), backupIds = new();
    readonly List<Control> parts = new();
    IAudioMeterInformation source;
    public readonly GroupBox Box;

    public event Action Changed;

    public SourcePanel(string title, string onText, bool render, bool on, string device, string backup, int channels, int volume, string note)
    {
        Render = render;
        On = Ui.Check(onText); On.Checked = on;

        string now = AudioDevices.Name(AudioDevices.DefaultId(render));
        Device.Items.Add("Windows default" + (now != null ? $" ({now})" : "")); ids.Add(null);
        foreach (var d in AudioDevices.List(render)) { Device.Items.Add(d.Name); ids.Add(d.Id); }
        if (device != null && !ids.Contains(device)) { Device.Items.Add("Not connected: " + (AudioDevices.Name(device) ?? "saved device")); ids.Add(device); }
        Device.SelectedIndex = Math.Max(0, ids.IndexOf(device));
        Device.DropDownWidth = 420;

        Backup.Items.Add("None (go to the Windows default)"); backupIds.Add(null);
        foreach (var d in AudioDevices.List(render)) { Backup.Items.Add(d.Name); backupIds.Add(d.Id); }
        if (backup != null && !backupIds.Contains(backup)) { Backup.Items.Add("Not connected: " + (AudioDevices.Name(backup) ?? "saved device")); backupIds.Add(backup); }
        Backup.SelectedIndex = Math.Max(0, backupIds.IndexOf(backup));
        Backup.DropDownWidth = 420;

        Channels.Items.AddRange(new object[] { "Stereo", "Mono" });
        Channels.SelectedIndex = channels == 1 ? 1 : 0;
        Volume.Value = Math.Clamp(volume, 0, 200);
        ShowVolume();

        Device.SelectedIndexChanged += (_, _) => { Hook(); Backup.Enabled = On.Checked && DeviceId != null; Changed?.Invoke(); };
        Channels.SelectedIndexChanged += (_, _) => Changed?.Invoke();
        Volume.ValueChanged += (_, _) => ShowVolume();
        On.CheckedChanged += (_, _) => { Enable(); Changed?.Invoke(); };
        Hook();

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 4) };
        void Row(string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 14, 5) };
            c.Anchor = AnchorStyles.Left; c.Margin = new Padding(3, 3, 3, 3);
            t.Controls.Add(l); t.Controls.Add(c); parts.Add(l); parts.Add(c);
        }
        t.Controls.Add(On); t.SetColumnSpan(On, 2); On.Margin = new Padding(0, 2, 0, 6);
        Row("Device", Device);
        Row("If not connected", Backup);
        Row("Level", meter);
        Row("Channels", Channels);
        Row("Volume", Ui.Flow(Volume, volumeText));
        var hint = Ui.Hint(note); hint.Margin = new Padding(0, 4, 0, 0);
        t.Controls.Add(hint); t.SetColumnSpan(hint, 2); parts.Add(hint);
        Box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6), Font = Ui.Body };
        Box.Controls.Add(t);
        Enable();
    }

    public string DeviceId => ids[Device.SelectedIndex];
    public string BackupId => DeviceId == null ? null : backupIds[Backup.SelectedIndex]; // no backup when following the default
    public int ChannelCount => Channels.SelectedIndex == 1 ? 1 : 2;

    void ShowVolume() => volumeText.Text = Volume.Value == 100 ? "100 % (as is)" : $"{Volume.Value} %";
    void Hook() => source = AudioDevices.Meter(Render, DeviceId);
    void Enable() { foreach (var c in parts) c.Enabled = On.Checked; Backup.Enabled = On.Checked && DeviceId != null; }

    // Called about 20 times a second: the bar shows the device level after the volume setting.
    public void Tick()
    {
        float p = 0;
        if (On.Checked && source != null) try { source.GetPeakValue(out p); } catch { source = null; }
        meter.Peak = Math.Min(1f, p * Volume.Value / 100f);
    }
}

sealed class SettingsForm : Form
{
    static readonly (string Label, int Qp)[] Qualities =
    {
        ("Sharpest (largest files)", 20), ("High", 23), ("Standard (recommended)", 26), ("Smaller files", 30), ("Smallest (soft text)", 34),
    };

    public readonly TabControl Tabs = new();
    readonly TextBox folder = new() { Width = 330 };
    public readonly NumericUpDown Fps = new() { Minimum = 1, Maximum = 60, Width = 64 };
    readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 240, Width = 64 };
    readonly ComboBox quality = Ui.Combo(230);
    readonly CheckBox auto = Ui.Check("Start TriRec when I sign in to Windows");
    readonly CheckBox recordOnLaunch = Ui.Check("Start recording as soon as TriRec opens");
    readonly SourcePanel sys, mic;
    readonly CheckBox mixOn = Ui.Check("Add a Mix track: system sound and microphone together");
    readonly ComboBox mixCh = Ui.Combo(110), first = Ui.Combo(150);
    readonly Label plan = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly RadioButton syncAuto = new() { AutoSize = true }, syncManual = new() { Text = "Set by hand:", AutoSize = true };
    readonly NumericUpDown syncMs = new() { Minimum = -2000, Maximum = 2000, Increment = 10, Width = 72 };
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Ui.Gray };
    public readonly Button SaveButton = new() { Text = "Save", AutoSize = true, MinimumSize = new Size(96, 30) };
    readonly List<int> qps = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 50 };
    readonly Func<string> state;
    readonly Settings original;
    int ticks;
    public bool Saved, SavedAutoStart;
    public Settings SavedSettings;

    public SettingsForm(Settings s, bool autoStart, Icon icon, Func<string> state)
    {
        original = s; this.state = state;
        Text = "TriRec settings";
        Icon = icon;
        Font = Ui.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        // ---- values
        foreach (var q in Qualities) { quality.Items.Add(q.Label); qps.Add(q.Qp); }
        if (!qps.Contains(s.Qp)) { quality.Items.Add($"Custom (qp {s.Qp})"); qps.Add(s.Qp); }
        quality.SelectedIndex = qps.IndexOf(s.Qp);
        folder.Text = s.OutDir;
        Fps.Value = Math.Clamp(s.Fps, 1, 60);
        minutes.Value = Math.Clamp(s.SegmentMinutes, 1, 240);
        auto.Checked = autoStart;
        recordOnLaunch.Checked = s.RecordOnLaunch;
        sys = new SourcePanel("System sound (what you hear)", "Record system sound", true, s.SystemAudio, s.SystemDevice, s.SystemBackup, s.SystemChannels, s.SystemVolume,
            "\"Windows default\" follows the playback device you pick in Windows, e.g. when you switch to headphones.");
        mic = new SourcePanel("Microphone", "Record microphone", false, s.Mic, s.MicDevice, s.MicBackup, s.MicChannels, s.MicVolume,
            "If the chosen device is unplugged, the backup is used, then the Windows default; TriRec switches back " +
            "within 2 s when it returns. The level bar moves only while something records from the microphone.");
        mixOn.Checked = s.MixTrack;
        mixCh.Items.AddRange(new object[] { "Stereo", "Mono" }); mixCh.SelectedIndex = s.MixChannels == 1 ? 1 : 0;
        syncAuto.Checked = s.AudioShiftMs == null; syncManual.Checked = !syncAuto.Checked;
        syncMs.Value = Math.Clamp(s.AudioShiftMs ?? Options.AutoShift(s.Fps), -2000, 2000);

        // ---- Recording tab
        var rec = new Page();
        rec.Section("Files");
        var browse = Ui.Button("Browse...");
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "Folder for recordings", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
        };
        var openDir = Ui.Button("Open");
        openDir.Click += (_, _) => { if (Directory.Exists(folder.Text)) Process.Start(new ProcessStartInfo(folder.Text) { UseShellExecute = true }); };
        rec.Row("Recordings folder", Ui.Flow(folder, browse, openDir));
        rec.HintRow("Files are named by when they start, e.g. 2026-11-17_10-54-44.mkv. The log (trirec.log) is kept there too.");
        rec.Row("New file every", Ui.Flow(minutes, Ui.Hint("minutes")));
        rec.Section("Picture");
        rec.Row("Monitors", new Label { Text = Monitors(), AutoSize = true, MaximumSize = new Size(470, 0) });
        rec.Row("Frames per second", Ui.Flow(Fps, Ui.Hint("10 is light; 23 to 30 is smooth")));
        rec.Row("Quality", quality);
        rec.HintRow("The quality stays the same; file size depends on how much changes on screen.");
        rec.Section("Starting");
        rec.Full(auto);
        rec.Full(recordOnLaunch);

        // ---- Sound tab
        var snd = new Page();
        snd.Full(sys.Box);
        snd.Full(mic.Box);

        // ---- Tracks tab
        var trk = new Page();
        trk.Section("Sound tracks in each file");
        trk.Full(Ui.Hint("Separate tracks let you change the balance of voice and game sound later in an editor. Players play track 1."));
        trk.Full(mixOn);
        trk.Row("Mix channels", mixCh);
        trk.Row("Track 1 (plays first)", first);
        trk.Row("Each file gets", plan);
        sys.Changed += UpdateTracks; mic.Changed += UpdateTracks;
        mixOn.CheckedChanged += (_, _) => UpdateTracks();
        mixCh.SelectedIndexChanged += (_, _) => UpdateTracks();
        first.SelectedIndexChanged += (_, _) => { if (!updating) UpdateTracks(); };
        UpdateTracks(s.FirstTrack);

        // ---- Advanced tab
        var adv = new Page();
        adv.Section("Sound and picture timing");
        adv.Full(syncAuto);
        adv.Full(Ui.Flow(syncManual, syncMs, Ui.Hint("ms   (positive = sound later, negative = earlier)")));
        adv.HintRow("Automatic uses values measured on this PC. Change it only if sound and picture look out of step.");
        syncMs.Enabled = syncManual.Checked;
        syncManual.CheckedChanged += (_, _) => syncMs.Enabled = syncManual.Checked;
        Fps.ValueChanged += (_, _) => ShowAutoShift();
        ShowAutoShift();
        adv.Section("Marking moments");
        adv.Full(Ui.Hint("Press Ctrl+Alt+M while recording (or use the tray menu) to note the time. It is written to <file>.marks.txt next to the recording, e.g. \"00:12:34  (clock 10:54:19)\"."));
        adv.Section("Files TriRec uses");
        var openSettings = Ui.Button("Open");
        openSettings.Click += (_, _) => { if (File.Exists(Settings.FilePath)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"")); };
        adv.Row("Settings", Ui.Flow(new Label { Text = Settings.FilePath, AutoSize = true, ForeColor = Ui.Gray, MaximumSize = new Size(380, 0) }, openSettings));
        adv.Row("ffmpeg", new Label { Text = FindFfmpeg() ?? "Not found on PATH: recording will fail until ffmpeg is installed", AutoSize = true, ForeColor = Ui.Gray, MaximumSize = new Size(470, 0) });

        foreach (var (name, page) in new[] { ("Recording", rec), ("Sound", snd), ("Tracks", trk), ("Advanced", adv) })
        {
            var tp = new TabPage(name) { UseVisualStyleBackColor = true };
            tp.Controls.Add(page.T);
            Tabs.TabPages.Add(tp);
        }

        // ---- bottom bar
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 30) };
        cancel.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();
        AcceptButton = SaveButton; CancelButton = cancel;
        var bar = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(status); bar.Controls.Add(SaveButton); bar.Controls.Add(cancel);

        var outer = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        outer.Controls.Add(Tabs); outer.Controls.Add(bar);
        Controls.Add(outer);

        tick.Tick += (_, _) =>
        {
            sys.Tick(); mic.Tick();
            if (ticks++ % 10 == 0) status.Text = state?.Invoke() ?? "";
        };
        status.Text = state?.Invoke() ?? "";
        tick.Start();
        FormClosed += (_, _) => tick.Dispose();
    }

    // Size the tabs to their largest page (works at any display scaling, since the pages size themselves).
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var max = Size.Empty;
        foreach (TabPage p in Tabs.TabPages)
        {
            var ps = p.Controls[0].GetPreferredSize(Size.Empty);
            max = new Size(Math.Max(max.Width, ps.Width), Math.Max(max.Height, ps.Height));
        }
        var d = Tabs.DisplayRectangle;
        Tabs.Size = new Size(max.Width + (Tabs.Width - d.Width) + 4, max.Height + (Tabs.Height - d.Height) + 4);
    }

    void ShowAutoShift()
    {
        int ms = Options.AutoShift((int)Fps.Value);
        syncAuto.Text = $"Automatic: at {Fps.Value} fps, sound is moved {Math.Abs(ms)} ms {(ms < 0 ? "earlier" : "later")}";
    }

    bool updating;

    // Rebuild the "Track 1" choices and the track list from the current sound settings.
    void UpdateTracks() => UpdateTracks(first.SelectedItem as string);

    void UpdateTracks(string want)
    {
        updating = true;
        bool both = sys.On.Checked && mic.On.Checked;
        mixOn.Enabled = both;
        mixCh.Enabled = both && mixOn.Checked;
        var p = Options.TrackPlan(sys.On.Checked, sys.ChannelCount, mic.On.Checked, mic.ChannelCount, mixOn.Checked, mixCh.SelectedIndex == 1 ? 1 : 2, "Mix");
        first.Items.Clear();
        foreach (var t in p) first.Items.Add(t.Title);
        first.Enabled = first.Items.Count > 1;
        int i = want == null ? -1 : first.Items.IndexOf(want);
        if (first.Items.Count > 0) first.SelectedIndex = Math.Max(0, i);
        updating = false;

        var final = Options.TrackPlan(sys.On.Checked, sys.ChannelCount, mic.On.Checked, mic.ChannelCount, mixOn.Checked,
                                      mixCh.SelectedIndex == 1 ? 1 : 2, first.SelectedItem as string ?? "Mix");
        plan.Text = final.Count == 0 ? "No sound (video only)"
            : string.Join("\n", final.Select((t, n) => $"Track {n + 1}: {t.Title}, {(t.Channels == 1 ? "mono" : "stereo")}{(n == 0 ? " (default)" : "")}"));
    }

    void Save()
    {
        try { Directory.CreateDirectory(Path.GetFullPath(folder.Text.Trim())); }
        catch (Exception e) { MessageBox.Show(this, "That folder cannot be used:\n\n" + e.Message, "TriRec"); Tabs.SelectedIndex = 0; return; }
        var r = original.Clone();
        r.OutDir = Path.GetFullPath(folder.Text.Trim());
        r.Fps = (int)Fps.Value; r.Qp = qps[quality.SelectedIndex]; r.SegmentMinutes = (int)minutes.Value;
        r.SystemAudio = sys.On.Checked; r.SystemDevice = sys.DeviceId; r.SystemBackup = sys.BackupId; r.SystemChannels = sys.ChannelCount; r.SystemVolume = sys.Volume.Value;
        r.Mic = mic.On.Checked; r.MicDevice = mic.DeviceId; r.MicBackup = mic.BackupId; r.MicChannels = mic.ChannelCount; r.MicVolume = mic.Volume.Value;
        r.MixTrack = mixOn.Checked; r.MixChannels = mixCh.SelectedIndex == 1 ? 1 : 2; r.FirstTrack = first.SelectedItem as string ?? "Mix";
        r.RecordOnLaunch = recordOnLaunch.Checked;
        r.AudioShiftMs = syncManual.Checked ? (int)syncMs.Value : null;
        SavedSettings = r; SavedAutoStart = auto.Checked; // capture now: the controls are gone once the window closes
        Saved = true;
        Close();
    }

    static string Monitors()
    {
        var s = Screen.AllScreens.OrderBy(x => x.Bounds.X).ThenBy(x => x.Bounds.Y).ToList();
        string list = string.Join(", ", s.Select(x => $"{x.Bounds.Width}x{x.Bounds.Height}"));
        return s.Count == 1 ? $"1 monitor ({list})" : $"{s.Count} monitors ({list}), all recorded together into one video";
    }

    static string FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { var f = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(f)) return f; } catch { }
        return null;
    }
}
