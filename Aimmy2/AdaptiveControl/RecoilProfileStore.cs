using System.IO;
using System.Text.Json;

namespace Aimmy2.AdaptiveControl;

/// <summary>Small, bounded checkpoint of measured recoil evidence for one calibrated view.</summary>
public static class RecoilProfileStore
{
    public static RecoilState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 4096) return null;
            var state = JsonSerializer.Deserialize<RecoilState>(stream);
            return state == null ? null : new RecoilEstimator(state).Snapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void Save(string path, RecoilState state)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new RecoilEstimator(state).Snapshot());
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(content); stream.Flush(true); }
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
