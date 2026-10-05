using Aimmy2.LocalCapture;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

string root = Path.Combine(Path.GetTempPath(), "AutoAimmy-LocalCaptureChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var options = new LocalCaptureOptions { MinimumFreeBytes = 0 };
using var image = new Bitmap(80, 60);
using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
DateTime timestamp = DateTime.UtcNow;
var recorder = new LocalCaptureRecorder(Path.Combine(root, "basic"), new { Game = "R6", Dpi = (int?)null }, options);
recorder.TryRecordInput(new(timestamp, 4, -2, true));
var boxes = new[] { new LocalDetectionBox(4, 5, 10, 20, .95, 0), new LocalDetectionBox(double.NaN, 0, 5, 5, .9, 0) };
Check(recorder.TryRecord(image, timestamp, "DetectionCrop", new(100, 200, 80, 60), boxes, "test.onnx", timestamp, new Rectangle(100, 200, 80, 60)), "exact detection frame accepted");
Check(recorder.TryRecord(image, timestamp.AddMilliseconds(40), "FullGame", new(0, 0, 160, 120), boxes, "test.onnx", timestamp, new Rectangle(100, 200, 80, 60)), "separate full frame accepted");
await recorder.DisposeAsync();
Check(!recorder.State.Active && recorder.State.FullFrames == 1 && recorder.State.CandidateFrames == 1, "stop drains both frame types");
Check(recorder.State.ConfidentCandidates == 1 && recorder.State.InputSamples == 1, "candidate and input counters");
var rows = File.ReadAllLines(Path.Combine(recorder.DirectoryPath, "frames.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
Check(rows.Length == 2 && rows[0].RootElement.GetProperty("Detections").GetArrayLength() == 1, "nonfinite pseudo box rejected");
Check(rows.All(row => row.RootElement.GetProperty("LabelStatus").GetString() == "UnverifiedCandidate"), "confidence never becomes ground truth");
Check(rows[1].RootElement.GetProperty("DetectionTimeOffsetMilliseconds").GetDouble() == 40, "asynchronous full frame offset explicit");
Check(rows[0].RootElement.GetProperty("LatestInput").GetProperty("RawDeltaX").GetDouble() == 4, "raw input snapshot timestamped with frame");
using (var decoded = new Bitmap(Path.Combine(recorder.DirectoryPath, "images", "detection-0000001.jpg")))
    Check(decoded.Width == 80 && decoded.Height == 60, "saved JPEG decodes at exact source dimensions");
Check(File.ReadAllText(Path.Combine(recorder.DirectoryPath, "playback.html")).Contains("playback.js") &&
    File.ReadAllText(Path.Combine(recorder.DirectoryPath, "playback.js")).Contains("UnverifiedCandidate"), "local playback index produced");
Check(!recorder.TryRecord(image, timestamp, "DetectionCrop", new(0, 0, 80, 60), boxes), "closed recorder rejects frames");
long actualBytes = Directory.EnumerateFiles(recorder.DirectoryPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
Check(actualBytes == recorder.State.BytesWritten, "reported bytes cover all files");

using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var bounded = new LocalCaptureRecorder(Path.Combine(root, "bounded"), new { }, options with { QueueCapacity = 1 }, bitmap => {
    entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); });
bounded.TryRecord(image, timestamp, "DetectionCrop", new(0, 0, 80, 60), boxes);
Check(entered.Wait(TimeSpan.FromSeconds(5)), "writer reaches injected slow encoder");
var timer = Stopwatch.StartNew();
for (int i = 0; i < 100; i++) bounded.TryRecord(image, timestamp, "DetectionCrop", new(0, 0, 80, 60), boxes);
timer.Stop();
Check(timer.ElapsedMilliseconds < 1000 && bounded.State.DroppedFrames >= 99, "bounded producer drops without waiting for disk encoder");
for (int i = 0; i < 3000; i++) bounded.TryRecordInput(new(timestamp, i, 0, false));
Check(bounded.State.DroppedInputSamples >= 952, "raw input queue is independently bounded under slow frame encoding");
using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Red);
release.Set();
await bounded.DisposeAsync();
Check(bounded.State.CandidateFrames == 2, "only in-flight and single queued bitmap retained");
using (var stored = new Bitmap(Path.Combine(bounded.DirectoryPath, "images", "detection-0000001.jpg")))
    Check(stored.GetPixel(40, 30).B > stored.GetPixel(40, 30).R, "queued image owns its pixels when inference reuses or changes caller bitmap");

var quota = new LocalCaptureRecorder(Path.Combine(root, "quota"), new { }, options with { MaximumSessionBytes = 65536, MaximumTotalBytes = 131072 }, _ => new byte[80000]);
quota.TryRecord(image, timestamp, "DetectionCrop", new(0, 0, 80, 60), boxes);
await quota.DisposeAsync();
Check(quota.State.Status == "QuotaReached" && quota.State.CandidateFrames == 0, "quota stops before oversized frame is written");
Check(quota.State.BytesWritten <= 65536, "metadata remains within session quota");

var diskError = new LocalCaptureRecorder(Path.Combine(root, "failure"), new { }, options, _ => throw new IOException("synthetic disk failure"));
diskError.TryRecord(image, timestamp, "DetectionCrop", new(0, 0, 80, 60), boxes);
await diskError.DisposeAsync();
Check(diskError.State.Status == "Failed" && diskError.State.Error!.Contains("synthetic disk failure"), "writer failure is surfaced without crashing inference");
using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(diskError.DirectoryPath, "manifest.json"))))
    Check(manifest.RootElement.GetProperty("Status").GetString() == "Failed", "failure persisted in manifest");

string totalPath = Path.Combine(root, "total", "local-capture");
Directory.CreateDirectory(totalPath);
File.WriteAllBytes(Path.Combine(totalPath, "existing.bin"), new byte[100000]);
bool totalRejected = false;
try { _ = new LocalCaptureRecorder(Path.Combine(root, "total"), new { }, options with { MaximumSessionBytes = 65536, MaximumTotalBytes = 131072 }); }
catch (IOException) { totalRejected = true; }
Check(totalRejected && File.Exists(Path.Combine(totalPath, "existing.bin")), "total quota rejects new session and preserves existing data");
Console.WriteLine($"{checks} local capture checks passed. Synthetic images only. Fixtures: {root}");
