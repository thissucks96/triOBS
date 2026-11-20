// Save clip (Ctrl+Alt+S): the last N seconds of the recording as a file of its own, without re-encoding.
//
// The clip is cut from the file being written (ffmpeg reads it as far as it has been written). If that file began
// less than N seconds ago (a new file was just started, or the recording restarted), the end of the previous
// file is joined in front when it ended at most 15 s before. Cuts start on a keyframe, so a clip can begin up to
// one keyframe interval (20 s) earlier than asked.
using System.Diagnostics;
using System.Globalization;

namespace TriRec;

static class Clipper
{
    static DateTime Start(FileInfo f) => DateTime.ParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    // Returns the clip's path. Throws with a readable message when there is nothing to clip or ffmpeg fails.
    public static string Save(string recDir, string clipDir, int seconds, DateTime now, Action<string> log)
    {
        // The file on disk trails real time (measured about 3 s: 1 s write chunks plus ffmpeg's own buffering);
        // wait so the moment of the request is in it.
        var due = now.AddSeconds(4);
        if (DateTime.Now < due && DateTime.Now - now < TimeSpan.FromSeconds(10)) Thread.Sleep(due - DateTime.Now);
        var files = new DirectoryInfo(recDir).GetFiles("*.mkv").Where(f => Retention.OwnName.IsMatch(f.Name)).OrderBy(f => f.Name).ToList();
        if (files.Count == 0) throw new Exception("no recording found in " + recDir);
        var cur = files[^1];
        double into = (now - Start(cur)).TotalSeconds;
        Directory.CreateDirectory(clipDir);
        string dst = Path.Combine(clipDir, $"clip {now:yyyy-MM-dd HH-mm-ss}.mkv");
        var sw = Stopwatch.StartNew();

        if (into < seconds && files.Count > 1)
        {
            var prev = files[^2];
            double prevLen = (prev.LastWriteTime - Start(prev)).TotalSeconds;
            if ((Start(cur) - prev.LastWriteTime).TotalSeconds <= 15 && prevLen > 0)
            {
                string list = Path.Combine(Path.GetTempPath(), $"trirec-clip-{Environment.ProcessId}.txt");
                string Q(FileInfo f) => "'" + f.FullName.Replace('\\', '/').Replace("'", @"'\''") + "'";
                File.WriteAllText(list, $"file {Q(prev)}\ninpoint {Math.Max(0, prevLen - (seconds - into)).ToString("0.###", CultureInfo.InvariantCulture)}\nfile {Q(cur)}\n");
                try
                {
                    if (Ffmpeg(out string err, "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", dst))
                    {
                        log($"clip: saved {Path.GetFileName(dst)} (last {seconds} s, from {prev.Name} and {cur.Name}) in {sw.Elapsed.TotalSeconds:F1} s");
                        return dst;
                    }
                    log("clip: joining the previous file failed, using the current one only: " + err);
                }
                finally { try { File.Delete(list); } catch { } }
            }
        }

        double ss = Math.Max(0, into - seconds);
        if (!Ffmpeg(out string e2, "-ss", ss.ToString("0.###", CultureInfo.InvariantCulture), "-i", cur.FullName, "-map", "0", "-c", "copy", dst))
            throw new Exception("ffmpeg could not cut the clip: " + e2);
        log($"clip: saved {Path.GetFileName(dst)} (last {Math.Min(seconds, (int)into)} s of {cur.Name}) in {sw.Elapsed.TotalSeconds:F1} s");
        return dst;
    }

    static bool Ffmpeg(out string err, params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var s in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(s);
        using var p = Process.Start(psi);
        p.StandardOutput.ReadToEndAsync();
        err = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit();
        // a file still being written ends mid-cluster; ffmpeg may say so but the clip is fine
        string out_ = args[^1];
        return p.ExitCode == 0 && File.Exists(out_) && new FileInfo(out_).Length > 0;
    }
}
