// trirec.exe: console front end.
//
//   trirec [--fps 10] [--qp 26] [--gop 20] [--segment 600] [--out DIR]
//          [--seconds N] [--no-audio] [--no-mic]
//   trirec crop FILE MONITOR [--qp 24]
//   trirec devices
using System.Diagnostics;
using System.Text.Json;

namespace TriRec;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "devices") return Devices();
            return args.Length > 0 && args[0] == "crop" ? Crop(args) : Record(args);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("trirec: " + e.Message);
            return 1;
        }
    }

    static int Record(string[] args)
    {
        var s = Recorder.Start(Options.Parse(args, "recordings", hidden: false));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; s.RequestStop(); }; // let ffmpeg close the file cleanly
        Console.WriteLine("(Ctrl+C to stop)");
        s.Ff.WaitForExit();
        s.Wall.Stop();
        s.Cts.Cancel();

        double secs = s.Wall.Elapsed.TotalSeconds, ffCpu = s.Ff.TotalProcessorTime.TotalSeconds;
        double self = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"Stopped after {secs:F0}s, ffmpeg exit code {s.Ff.ExitCode}.");
        Console.WriteLine($"CPU: ffmpeg {ffCpu / secs:P1} of one core, trirec {self / secs:P1} " +
                          $"({(ffCpu + self) / secs / Environment.ProcessorCount:P2} of the whole CPU)");
        foreach (var f in new DirectoryInfo(s.OutDir).GetFiles("*.mkv").Where(f => f.LastWriteTime >= s.Started.AddSeconds(-1)))
            Console.WriteLine($"  {f.Name}  {f.Length / 1048576.0:F1} MB");
        return s.Ff.ExitCode;
    }

    // trirec devices: the sound devices and their ids (for SystemDevice / MicDevice in settings.json).
    static int Devices()
    {
        foreach (bool render in new[] { true, false })
        {
            string def = AudioDevices.DefaultId(render);
            Console.WriteLine(render ? "Playback (system sound):" : "Recording (microphone):");
            foreach (var d in AudioDevices.List(render))
                Console.WriteLine($"  {(d.Id == def ? "*" : " ")} {d.Name}\n      {d.Id}");
        }
        Console.WriteLine("(* = Windows default)");
        return 0;
    }

    static int Crop(string[] args)
    {
        if (args.Length < 3) throw new Exception("usage: trirec crop FILE MONITOR [--qp 24]");
        string file = Path.GetFullPath(args[1]);
        int n = int.Parse(args[2]), qp = Options.Int(args, "--qp", 24);
        string layout = Path.Combine(Path.GetDirectoryName(file), "layout.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(layout));
        var m = doc.RootElement.GetProperty("monitors").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("number").GetInt32() == n);
        if (m.ValueKind == JsonValueKind.Undefined) throw new Exception($"no monitor {n} in {layout}");
        string dst = Path.Combine(Path.GetDirectoryName(file), $"{Path.GetFileNameWithoutExtension(file)}.mon{n}.mkv");
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false };
        foreach (var s in new[] { "-hide_banner", "-loglevel", "warning", "-stats", "-y", "-i", file, "-vf",
                     $"crop={m.GetProperty("w")}:{m.GetProperty("h")}:{m.GetProperty("x")}:{m.GetProperty("y")}",
                     "-map", "0", "-c:v", "hevc_nvenc", "-preset", "p4", "-rc", "constqp", "-qp", qp.ToString(),
                     "-c:a", "copy", dst }) psi.ArgumentList.Add(s);
        using var ff = Process.Start(psi);
        ff.WaitForExit();
        if (ff.ExitCode == 0) Console.WriteLine("Wrote " + dst);
        return ff.ExitCode;
    }
}
