// Storage limit for the recordings folder, so it works as a rolling buffer.
//
// Only TriRec's own recordings are ever deleted: files directly in the folder named like 2026-11-17_10-54-44.mkv.
// Crops (*.monN.mkv), other programs' files and subfolders are never touched. The newest recording (being written)
// and anything changed in the last 2 minutes are kept. With keepMarked, a recording that has a .marks.txt
// (Ctrl+Alt+M) is kept too, so marking a moment also means "keep this file".
// Deleting is permanent (not the Recycle Bin): the point is to free the space.
using System.Text.RegularExpressions;

namespace TriRec;

static class Retention
{
    const long GB = 1L << 30;
    static readonly Regex OwnName = new(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.mkv$", RegexOptions.IgnoreCase);

    public sealed class Report
    {
        public readonly List<(FileInfo File, string Why)> Delete = new();
        public int Files, Marked;     // TriRec recordings in the folder, and how many of them have marks
        public long Bytes, MarkedBytes, AfterBytes;
    }

    static string Marks(FileInfo f) => Path.ChangeExtension(f.FullName, ".marks.txt");

    // What would be deleted now. days / maxGb: 0 = no limit of that kind. Oldest first.
    public static Report Plan(string dir, int days, int maxGb, bool keepMarked, DateTime now)
    {
        var r = new Report();
        if (!Directory.Exists(dir)) return r;
        var files = new DirectoryInfo(dir).GetFiles("*.mkv").Where(f => OwnName.IsMatch(f.Name)).OrderBy(f => f.Name).ToList();
        r.Files = files.Count;
        r.Bytes = r.AfterBytes = files.Sum(f => f.Length);
        foreach (var f in files) if (File.Exists(Marks(f))) { r.Marked++; r.MarkedBytes += f.Length; }
        if (days <= 0 && maxGb <= 0) return r;

        var newest = files.LastOrDefault();
        foreach (var f in files)
        {
            if (f == newest || now - f.LastWriteTime < TimeSpan.FromMinutes(2)) continue; // being recorded
            if (keepMarked && File.Exists(Marks(f))) continue;
            string why = days > 0 && now - f.LastWriteTime > TimeSpan.FromDays(days) ? $"older than {days} days"
                       : maxGb > 0 && r.AfterBytes > maxGb * GB ? $"folder over {maxGb} GB"
                       : null;
            if (why == null) continue;
            r.Delete.Add((f, why));
            r.AfterBytes -= f.Length;
        }
        return r;
    }

    // Delete what Plan picks, one log line per file. A file that cannot be deleted (in use) is skipped.
    public static Report Run(string dir, int days, int maxGb, bool keepMarked, Action<string> log)
    {
        var r = Plan(dir, days, maxGb, keepMarked, DateTime.Now);
        foreach (var (f, why) in r.Delete)
            try
            {
                long len = f.Length;
                f.Delete();
                if (File.Exists(Marks(f))) File.Delete(Marks(f)); // only when marked files are not kept
                log($"storage: deleted {f.Name} ({len / 1048576.0:F0} MB, {why})");
            }
            catch (Exception e)
            {
                r.AfterBytes += f.Length;
                log($"storage: could not delete {f.Name}: {e.Message}");
            }
        if (r.Delete.Count > 0)
            log($"storage: {r.Files - r.Delete.Count} recordings left, {r.AfterBytes / (double)GB:F1} GB" +
                (maxGb > 0 && r.AfterBytes > maxGb * GB ? $" (still over {maxGb} GB: the rest is marked or in use)" : ""));
        return r;
    }

    public static string Size(long bytes) => bytes >= GB ? $"{bytes / (double)GB:F1} GB" : $"{bytes / 1048576.0:F0} MB";
}
