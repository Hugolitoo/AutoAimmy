using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Aimmy2.LocalCapture;

/// <summary>Bounded writer. Inference only clones an accepted bitmap; encoding and file I/O run here.</summary>
public sealed class LocalCaptureRecorder : IAsyncDisposable
{
    private sealed record PendingFrame(Bitmap Bitmap, LocalCaptureFrame Metadata);
    private readonly LocalCaptureOptions options;
    private readonly Channel<PendingFrame> frames;
    private readonly Channel<LocalInputSample> inputs = Channel.CreateBounded<LocalInputSample>(2048);
    private readonly Task worker;
    private readonly long priorBytes;
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private readonly Func<Bitmap, byte[]> encode;
    private readonly object producerGate = new();
    private LocalCaptureState state;
    private LocalInputSample? latestInput;
    private long sequence, droppedFrames, droppedInputs;
    private int accepting = 1;
    private const long FinalManifestReserve = 8192;
    private static readonly UTF8Encoding Utf8 = new(false);
    public string DirectoryPath { get; }
    public Task Completion => worker;
    public LocalCaptureState State => Volatile.Read(ref state) with {
        DroppedFrames = Interlocked.Read(ref droppedFrames), DroppedInputSamples = Interlocked.Read(ref droppedInputs) };

    public LocalCaptureRecorder(string dataDirectory, object context, LocalCaptureOptions? options = null,
        Func<Bitmap, byte[]>? encoder = null)
    {
        this.options = options ?? new();
        this.options.Validate();
        encode = encoder ?? EncodeJpeg;
        string root = Path.GetFullPath(Path.Combine(dataDirectory, "local-capture"));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Local capture directory must not be a link.");
        priorBytes = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Sum(file => new FileInfo(file).Length);
        if (priorBytes + FinalManifestReserve + 32768 >= this.options.MaximumTotalBytes)
            throw new IOException("La limite totale des enregistrements locaux est atteinte. Archivez des sessions avant de reprendre.");
        CheckFreeSpace(root);
        DirectoryPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "images"));
        state = new(true, "Recording", DirectoryPath, PlaybackPath: Path.Combine(DirectoryPath, "playback.html"));
        frames = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(this.options.QueueCapacity) {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        string contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });
        worker = Task.Run(() => WriteAsync(contextJson));
    }

    public bool TryRecord(Bitmap bitmap, DateTime capturedUtc, string kind, Rectangle captureBounds,
        IReadOnlyList<LocalDetectionBox> detections, string? modelName = null,
        DateTime? detectionsCapturedUtc = null, Rectangle? detectionBounds = null, LocalVisualCue? visualCue = null)
    {
        if (kind is not ("FullGame" or "DetectionCrop")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || captureBounds.Width <= 0 || captureBounds.Height <= 0) return false;
        lock (producerGate)
        {
            if (Volatile.Read(ref accepting) == 0) return false;
            if (frames.Reader.CanCount && frames.Reader.Count >= options.QueueCapacity)
            { Interlocked.Increment(ref droppedFrames); return false; }
            int sourceWidth = detectionBounds?.Width ?? captureBounds.Width;
            int sourceHeight = detectionBounds?.Height ?? captureBounds.Height;
            var valid = detections.Take(512).Where(box => new[] { box.X, box.Y, box.Width, box.Height, box.Confidence }.All(double.IsFinite) &&
                box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 && box.X + box.Width <= sourceWidth + 1 &&
                box.Y + box.Height <= sourceHeight + 1 && box.Confidence is >= 0 and <= 1 && box.ClassId >= 0).ToArray();
            string reason = valid.Length == 0 ? "NoPrediction" : valid.All(b => b.Confidence >= .8) ? "ConfidentPrediction" : "LowConfidenceOrUncertain";
            if (visualCue?.ProbableHeadMarker == true || visualCue?.ProbableBlood == true) reason = "UnverifiedVisualCue";
            long next = Interlocked.Increment(ref sequence);
            string image = $"images/{(kind == "FullGame" ? "full" : "detection")}-{next:D7}.jpg";
            var meta = new LocalCaptureFrame(next, capturedUtc, kind, image, bitmap.Width, bitmap.Height,
                LocalCaptureBounds.From(captureBounds), valid, "UnverifiedCandidate", reason,
                string.IsNullOrWhiteSpace(modelName) ? null : Path.GetFileName(modelName)[..Math.Min(180, Path.GetFileName(modelName).Length)],
                Volatile.Read(ref latestInput), detectionsCapturedUtc,
                detectionBounds.HasValue ? LocalCaptureBounds.From(detectionBounds.Value) : null,
                detectionsCapturedUtc.HasValue ? (capturedUtc - detectionsCapturedUtc.Value).TotalMilliseconds : null, visualCue);
            Bitmap? clone = null;
            try
            {
                clone = (Bitmap)bitmap.Clone();
                if (frames.Writer.TryWrite(new(clone, meta))) { clone = null; return true; }
                Interlocked.Increment(ref droppedFrames);
                return false;
            }
            finally { clone?.Dispose(); }
        }
    }

    public bool TryRecordInput(LocalInputSample sample)
    {
        if (Volatile.Read(ref accepting) == 0) return false;
        if ((sample.RawDeltaX.HasValue && !double.IsFinite(sample.RawDeltaX.Value)) ||
            (sample.RawDeltaY.HasValue && !double.IsFinite(sample.RawDeltaY.Value))) return false;
        Volatile.Write(ref latestInput, sample);
        if (inputs.Writer.TryWrite(sample)) return true;
        Interlocked.Increment(ref droppedInputs);
        return false;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
        bitmap.Save(memory, encoder, parameters);
        return memory.ToArray();
    }

    private void CheckFreeSpace(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.AvailableFreeSpace < options.MinimumFreeBytes)
            throw new IOException("Espace disque libre insuffisant pour continuer l’enregistrement local.");
    }

    private bool HasBudget(long additionalBytes) => state.BytesWritten + additionalBytes + FinalManifestReserve <= options.MaximumSessionBytes &&
        priorBytes + state.BytesWritten + additionalBytes + FinalManifestReserve <= options.MaximumTotalBytes;

    private void AddBytes(long bytes) => Volatile.Write(ref state, state with { BytesWritten = state.BytesWritten + bytes });

    private void StopAccepting()
    {
        Interlocked.Exchange(ref accepting, 0);
        frames.Writer.TryComplete();
        inputs.Writer.TryComplete();
    }

    private async Task WriteAsync(string contextJson)
    {
        string finalStatus = "Stopped";
        string? error = null;
        try
        {
            foreach (var file in new[] { ("context.json", contextJson), ("playback.html", PlaybackHtml), ("README.txt", Readme) })
            {
                byte[] bytes = Utf8.GetBytes(file.Item2);
                if (!HasBudget(bytes.Length)) throw new IOException("Recording quota is too small for session metadata.");
                await File.WriteAllBytesAsync(Path.Combine(DirectoryPath, file.Item1), bytes).ConfigureAwait(false);
                AddBytes(bytes.Length);
            }
            await WriteManifestAsync("Recording", null).ConfigureAwait(false);
            using var timeline = new StreamWriter(Path.Combine(DirectoryPath, "frames.jsonl"), false, Utf8);
            using var inputWriter = new StreamWriter(Path.Combine(DirectoryPath, "inputs.jsonl"), false, Utf8);
            using var playback = new StreamWriter(Path.Combine(DirectoryPath, "playback.js"), false, Utf8);
            const string header = "window.AUTOAIMMY_FRAMES=[];\n";
            playback.Write(header); AddBytes(Utf8.GetByteCount(header));
            bool quota = false;
            while (!quota)
            {
                if ((DateTime.UtcNow - startedUtc).TotalMinutes >= options.MaximumSessionMinutes)
                { finalStatus = "DurationLimit"; StopAccepting(); break; }
                int read = 0;
                while (read++ < 512 && inputs.Reader.TryRead(out var input))
                {
                    string line = JsonSerializer.Serialize(input) + "\n";
                    int bytes = Utf8.GetByteCount(line);
                    if (!HasBudget(bytes)) { quota = true; break; }
                    inputWriter.Write(line); AddBytes(bytes);
                    Volatile.Write(ref state, state with { InputSamples = state.InputSamples + 1 });
                }
                if (quota) break;
                if (frames.Reader.TryRead(out var frame))
                {
                    using (frame.Bitmap)
                    {
                        if ((state.FullFrames + state.CandidateFrames) % 10 == 0) CheckFreeSpace(DirectoryPath);
                        byte[] jpeg;
                        try { jpeg = encode(frame.Bitmap); }
                        catch { Interlocked.Increment(ref droppedFrames); throw; }
                        if (jpeg.Length > 8 * 1024 * 1024) { Interlocked.Increment(ref droppedFrames); continue; }
                        string json = JsonSerializer.Serialize(frame.Metadata);
                        string row = json + "\n", script = "window.AUTOAIMMY_FRAMES.push(" + json + ");\n";
                        long bytes = jpeg.LongLength + Utf8.GetByteCount(row) + Utf8.GetByteCount(script);
                        if (!HasBudget(bytes)) { Interlocked.Increment(ref droppedFrames); quota = true; break; }
                        await File.WriteAllBytesAsync(Path.Combine(DirectoryPath, frame.Metadata.Image.Replace('/', Path.DirectorySeparatorChar)), jpeg).ConfigureAwait(false);
                        timeline.Write(row); playback.Write(script); AddBytes(bytes);
                        bool candidate = frame.Metadata.Kind == "DetectionCrop";
                        Volatile.Write(ref state, state with {
                            FullFrames = state.FullFrames + (candidate ? 0 : 1), CandidateFrames = state.CandidateFrames + (candidate ? 1 : 0),
                            ConfidentCandidates = state.ConfidentCandidates + (candidate && frame.Metadata.SelectionReason == "ConfidentPrediction" ? 1 : 0),
                            HardCandidates = state.HardCandidates + (candidate && frame.Metadata.SelectionReason != "ConfidentPrediction" ? 1 : 0) });
                    }
                }
                else if (frames.Reader.Completion.IsCompleted && inputs.Reader.Completion.IsCompleted) break;
                else await Task.Delay(25).ConfigureAwait(false);
                if ((state.FullFrames + state.CandidateFrames) % 10 == 0)
                { timeline.Flush(); playback.Flush(); inputWriter.Flush(); }
            }
            if (quota) finalStatus = "QuotaReached";
        }
        catch (Exception failure)
        {
            finalStatus = "Failed";
            error = failure.GetType().Name + ": " + failure.Message;
        }
        finally
        {
            StopAccepting();
            while (frames.Reader.TryRead(out var remaining)) { remaining.Bitmap.Dispose(); Interlocked.Increment(ref droppedFrames); }
            while (inputs.Reader.TryRead(out _)) Interlocked.Increment(ref droppedInputs);
            Volatile.Write(ref state, state with { Active = false, Status = finalStatus, Error = error });
            try { await WriteManifestAsync(finalStatus, error).ConfigureAwait(false); }
            catch (Exception manifestFailure) { Volatile.Write(ref state, state with {
                Status = "Failed", Error = (error == null ? "" : error + " | ") + "Manifest: " + manifestFailure.Message }); }
        }
    }

    private async Task WriteManifestAsync(string status, string? error)
    {
        string target = Path.Combine(DirectoryPath, "manifest.json");
        long previous = File.Exists(target) ? new FileInfo(target).Length : 0;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            Schema = 1, Game = "R6", Format = "JPEGSequence", RequestedFramesPerSecond = options.FramesPerSecond,
            StartedUtc = startedUtc, EndedUtc = status == "Recording" ? (DateTime?)null : DateTime.UtcNow,
            Status = status, Error = error, Options = options, State = State,
            Labels = "UnverifiedCandidate: detector predictions are not ground truth. High confidence is not validation.",
            Timing = "DetectionCrop boxes belong to that exact crop. FullGame boxes are a recent separate inference snapshot; offset is explicit.",
            Inputs = "Raw mouse counts and button states; clicks are not confirmed shots or hits. Hardware DPI and actual ADS are unknown.",
            Retention = "Stops at session or total quota. No automatic deletion, upload or model training."
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
        AddBytes(bytes.LongLength - previous);
    }

    public async ValueTask DisposeAsync() { StopAccepting(); await worker.ConfigureAwait(false); }

    private const string Readme = "AutoAimmy — enregistrement local R6\r\n" +
        "Ouvrez playback.html pour revoir les images. Format : séquence JPEG, fréquence demandée 5 images/s par défaut, pas un fichier vidéo.\r\n" +
        "Les images DetectionCrop sont exactement celles analysées par le modèle. Leurs boîtes sont des propositions NON VALIDÉES.\r\n" +
        "Les images FullGame couvrent la partie du jeu sur le moniteur choisi ; leurs détections proviennent d’une capture séparée horodatée.\r\n" +
        "Les événements de souris sont des comptes bruts : ni des angles, ni une mesure du DPI, ni une confirmation de touche ou de visée.\r\n" +
        "Limites par défaut : 30 minutes, 512 Mio/session, 2 Gio au total. Arrêt quand une limite est atteinte. Aucune suppression automatique.\r\n" +
        "Tout reste dans ce dossier. Les images peuvent inclure les éléments visibles du jeu. Rien n’est envoyé automatiquement.\r\n";

    private const string PlaybackHtml = """
<!doctype html><html lang="fr"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>AutoAimmy — lecture locale</title><style>body{background:#101322;color:#eef1ff;font:16px system-ui;margin:24px}button,select,input{font:inherit;margin:5px;padding:8px}canvas{display:block;max-width:100%;background:#000;border:1px solid #596086}#details{white-space:pre-wrap;color:#c1c8dc}input[type=range]{width:70%}.muted{color:#b8bdd0}</style>
<h1>Enregistrement local R6</h1><p class="muted">Séquence d’images JPEG. Les boîtes sont des propositions du détecteur, jamais des annotations validées.</p>
<select id="kind"><option value="FullGame">Vue du jeu</option><option value="DetectionCrop">Images candidates du modèle</option></select><button id="play">Lire / pause</button><button id="prev">Précédente</button><button id="next">Suivante</button><label><input id="boxes" type="checkbox" checked>Afficher les boîtes</label><br><input id="position" type="range" min="0" value="0" max="0"><span id="count"></span><canvas id="view"></canvas><p id="details"></p>
<script src="playback.js"></script><script>
const all=window.AUTOAIMMY_FRAMES||[],q=id=>document.getElementById(id),cv=q('view'),cx=cv.getContext('2d');let items=[],index=0,playing=false,timer=null,drawSerial=0;
function update(){items=all.filter(f=>f.Kind===q('kind').value);index=0;q('position').max=Math.max(0,items.length-1);draw()}
function draw(){const f=items[index],serial=++drawSerial;q('position').value=index;q('count').textContent=items.length?(index+1)+' / '+items.length:'Aucune image';if(!f){q('details').textContent='Aucune image dans cette catégorie. Fermez la session puis actualisez cette page.';return}q('details').textContent=f.CapturedUtc+' · '+f.Kind+' · '+f.SelectionReason+'\nModèle : '+(f.ModelName||'inconnu')+' · boîtes proposées : '+f.Detections.length+' · '+f.LabelStatus+(f.Kind==='FullGame'?'\nDécalage des détections : '+(f.DetectionTimeOffsetMilliseconds??'inconnu')+' ms':'');const im=new Image();im.onload=()=>{if(serial!==drawSerial)return;cv.width=im.width;cv.height=im.height;cx.drawImage(im,0,0);if(!q('boxes').checked)return;const origin=f.DetectionBounds||f.CaptureBounds,sx=im.width/f.CaptureBounds.Width,sy=im.height/f.CaptureBounds.Height;cx.lineWidth=2;cx.font='14px system-ui';for(const b of f.Detections){const x=(b.X+origin.X-f.CaptureBounds.X)*sx,y=(b.Y+origin.Y-f.CaptureBounds.Y)*sy;cx.strokeStyle='#61edb0';cx.fillStyle='#61edb0';cx.strokeRect(x,y,b.Width*sx,b.Height*sy);cx.fillText('proposition '+Math.round(b.Confidence*100)+'%',x,Math.max(15,y-4))}};im.src=f.Image}
function step(delta){index=Math.max(0,Math.min(items.length-1,index+delta));draw()}
function tick(){if(!playing||items.length<2)return;const a=items[index],b=items[index+1];if(!b){playing=false;return}timer=setTimeout(()=>{step(1);tick()},Math.min(2000,Math.max(40,Date.parse(b.CapturedUtc)-Date.parse(a.CapturedUtc))))}
q('play').onclick=()=>{playing=!playing;clearTimeout(timer);if(playing)tick()};q('prev').onclick=()=>step(-1);q('next').onclick=()=>step(1);q('kind').onchange=update;q('boxes').onchange=draw;q('position').oninput=()=>{index=Number(q('position').value);draw()};update();
</script></html>
""";
}
