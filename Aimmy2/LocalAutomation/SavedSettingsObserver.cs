using Aimmy2.Adaptive;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Aimmy2.LocalAutomation;

/// <summary>Rereads saved R6 settings after a file changes. Never reads or writes game memory.</summary>
internal sealed class SavedSettingsObserver : IDisposable
{
    public static SavedSettingsObserver Instance { get; } = new();
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private string signature = "";
    public string? Error { get; private set; }
    public void Start() => worker ??= Task.Run(RunAsync);
    private static string FileSignature()
    {
        var documents = new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents") };
        var files = new List<string>();
        foreach (string documentsPath in documents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root = Path.Combine(documentsPath, "My Games", "Rainbow Six - Siege");
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root).Take(100).Prepend(root))
            {
                string path = Path.Combine(directory, "GameSettings.ini");
                if (File.Exists(path)) files.Add(path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length);
            }
        }
        return string.Join("|", files.Order(StringComparer.Ordinal));
    }
    private async Task RunAsync()
    {
        string module = Path.Combine(AppContext.BaseDirectory, "AutomaticSettings.psm1");
        if (!File.Exists(module) || Environment.GetEnvironmentVariable("AUTOAIMMY_DATA_DIR") == null) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    string current = FileSignature();
                    if (current == signature) continue;
                    string Quote(string value) => "'" + value.Replace("'", "''") + "'";
                    string root = Path.GetFullPath(Path.Combine(ObservationMode.DataDirectory, ".."));
                    string script = "$ErrorActionPreference='Stop'; Import-Module " + Quote(module) + " -Force; $null=Sync-AutomaticPlayerProfile -Root " + Quote(root);
                    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                        start.ArgumentList.Add(argument);
                    using var process = Process.Start(start) ?? throw new IOException("Import R6 indisponible.");
                    using var cancel = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } });
                    Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                    await output.ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new IOException(detail);
                    signature = current; Error = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { Error = error.Message; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public void Dispose() => stop.Cancel();
}
