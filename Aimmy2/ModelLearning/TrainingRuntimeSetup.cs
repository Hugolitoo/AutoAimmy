using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aimmy2.ModelLearning;

public static class TrainingRuntimeSetup
{
    private sealed record Package(int Schema, string Version, string Runtime, string Sha256, long Size, string Url);
    private static readonly SemaphoreSlim Gate = new(1);
    public static async Task EnsureAsync(string dataDirectory, CancellationToken cancellationToken = default,
        IProgress<(long Received, long Total)>? progress = null)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string learningRoot = Path.Combine(Path.GetFullPath(dataDirectory), "learning");
            string destination = Path.Combine(learningRoot, "runtime");
            if (File.Exists(Path.Combine(destination, "python.exe"))) return;
            string manifest = Path.Combine(AppContext.BaseDirectory, "learning-runtime.json");
            if (!File.Exists(manifest)) throw new InvalidOperationException("PackagedLearningRuntimeMissing");
            var package = JsonSerializer.Deserialize<Package>(await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Missing runtime manifest.");
            var url = new Uri(package.Url);
            if (package.Schema != 1 || package.Runtime != "win-x64" || package.Size is <= 0 or > 1024L * 1024 * 1024 ||
                package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit) || url.Scheme != "https" || url.Host != "github.com" ||
                !url.AbsolutePath.StartsWith("/Hugolitoo/AutoAimmy/releases/download/", StringComparison.Ordinal) ||
                !url.AbsolutePath.EndsWith("/AutoAimmy-learning-win-x64.zip", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid local learning package.");
            Directory.CreateDirectory(learningRoot);
            if (new DriveInfo(Path.GetPathRoot(learningRoot)!).AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                throw new IOException("L’apprentissage local demande au moins 2 Go libres pour son installation.");
            string archive = Path.Combine(learningRoot, "runtime-download-" + package.Sha256.ToLowerInvariant() + ".zip");
            long existing = File.Exists(archive) ? new FileInfo(archive).Length : 0;
            if (existing > package.Size) { File.Delete(archive); existing = 0; }
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoAimmy-LocalLearning/1");
            if (existing < package.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (append && response.Content.Headers.ContentRange?.From != existing) throw new InvalidDataException("Invalid resumed runtime response.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(archive, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536]; long total = append ? existing : 0, lastProgress = total;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > package.Size) throw new InvalidDataException("Runtime download exceeds manifest size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (total - lastProgress > 1048576) { progress?.Report((total, package.Size)); lastProgress = total; }
                }
                if (total != package.Size) throw new InvalidDataException("Incomplete local learning download.");
            }
            string checksum;
            await using (var input = File.OpenRead(archive))
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!checksum.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            { File.Delete(archive); throw new InvalidDataException("Runtime download checksum mismatch."); }
            ExtractVerifiedArchive(archive, learningRoot, cancellationToken);
            File.Delete(archive);
        }
        finally { Gate.Release(); }
    }

    // The caller verifies size and SHA256 first. A failed extraction leaves the verified
    // archive available for retry, but never leaves a partially installed runtime.
    internal static void ExtractVerifiedArchive(string archive, string learningRoot, CancellationToken cancellationToken = default)
    {
        learningRoot = Path.GetFullPath(learningRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string destination = Path.Combine(learningRoot, "runtime");
        string staging = Path.GetFullPath(Path.Combine(learningRoot, "runtime-staging-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded += entry.Length;
                string target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (expanded > 2L * 1024 * 1024 * 1024 || entry.FullName.Contains(':') ||
                    !target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe runtime archive.");
                if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
            if (!File.Exists(Path.Combine(staging, "python.exe")) || !Directory.Exists(Path.Combine(staging, "Lib", "site-packages", "torch")))
                throw new InvalidDataException("Incomplete local training runtime.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            // Verify the absolute target before recursive deletion. This is only the
            // unique directory created by this call; never the runtime or archive.
            string owned = Path.GetFullPath(staging);
            if (Directory.Exists(owned) &&
                string.Equals(Path.GetDirectoryName(owned), learningRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(owned).StartsWith("runtime-staging-", StringComparison.Ordinal) &&
                Guid.TryParseExact(Path.GetFileName(owned)["runtime-staging-".Length..], "N", out _) &&
                (File.GetAttributes(owned) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(owned, recursive: true);
        }
    }
}
