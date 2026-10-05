using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aimmy2.ModelLearning;

/// <summary>Local capture, reviewed datasets, measured model comparison and reversible model selection.</summary>
public sealed class ModelLearningCoordinator
{
    private readonly string _dataDirectory;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _importOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime ModifiedUtc, string Hash)> _modelHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public const int MaximumCaptures = 2000;
    public const long MaximumImageBytes = 8 * 1024 * 1024;
    public const long MaximumStoredImageBytes = 512L * 1024 * 1024;

    public ModelLearningCoordinator(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _root = Path.Combine(_dataDirectory, "learning");
    }

    public LearningState Inspect(string? activeModelPath)
    {
        lock (_gate)
        {
            var activeHash = !string.IsNullOrEmpty(activeModelPath) && File.Exists(activeModelPath) ? CachedModelHash(activeModelPath) : null;
            var captures = ReadCaptures().Where(x => activeHash == null || x.ModelSha256 == activeHash).ToArray();
            var weights = DiscoverSourceWeights(activeModelPath);
            var pending = captures.Count(x => x.ReviewStatus == "Unreviewed");
            var reviewed = captures.Count(x => x.ReviewStatus == "HumanReviewed");
            var blockers = new List<string>();
            if (string.IsNullOrEmpty(activeModelPath) || !File.Exists(activeModelPath)) blockers.Add("ActiveModelMissing");
            if (weights.Length == 0) blockers.Add("TrainableSourceWeightsMissing");
            if (!File.Exists(Path.Combine(_root, "runtime", "python.exe"))) blockers.Add("TrainingEnvironmentNotConfigured");
            if (reviewed < 40) blockers.Add("ReviewedDatasetMissing");
            if (captures.Where(x => x.ReviewStatus == "HumanReviewed").Select(x => x.SessionId).Distinct().Count() < 2)
                blockers.Add("IndependentValidationSessionMissing");
            var detail = weights.Length == 0
                ? "Captures locales prêtes à être vérifiées. Le fichier ONNX sert à détecter ; les poids source .pt manquent pour le réentraînement."
                : "Poids source trouvés. L'entraînement exige un environnement Python local et des annotations vérifiées, avec une session de validation séparée.";
            return new(blockers.Count > 0 ? "WaitingForPrerequisites" : "ReadyForLocalTraining", pending, reviewed,
                weights, blockers.ToArray(), detail, ResolveSelectedModel(activeModelPath));
        }
    }

    public string[] DiscoverSourceWeights(string? activeModelPath)
    {
        var directories = new[] { Path.Combine(_root, "source"), Path.Combine(_dataDirectory, "bin", "models"),
            activeModelPath == null ? null : Path.GetDirectoryName(Path.GetFullPath(activeModelPath)) };
        return directories.Where(x => x != null && System.IO.Directory.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(x => System.IO.Directory.EnumerateFiles(x!, "*.pt", SearchOption.TopDirectoryOnly))
            .Where(x => new FileInfo(x).Length > 0).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string GetCaptureImagePath(string captureId)
    {
        if (!IsHash(captureId)) throw new ArgumentException("Invalid capture id.");
        return Path.Combine(_root, "captures", captureId + ".jpg");
    }

    public int ImportRecordingCaptures()
    {
        lock (_gate) return ImportRecordingCapturesCore();
    }

    private int ImportRecordingCapturesCore()
    {
        var recordings = Path.Combine(_dataDirectory, "local-capture");
        if (!System.IO.Directory.Exists(recordings)) return 0;
        var count = 0;
        var captureDirectory = Path.Combine(_root, "captures");
        var existingFiles = System.IO.Directory.Exists(captureDirectory) ? System.IO.Directory.GetFiles(captureDirectory, "*.jpg") : [];
        if (existingFiles.Length >= MaximumCaptures || existingFiles.Sum(x => new FileInfo(x).Length) >= MaximumStoredImageBytes) return 0;
        foreach (var session in System.IO.Directory.EnumerateDirectories(recordings).OrderDescending().Take(200))
        {
            var frames = Path.Combine(session, "frames.jsonl");
            if (!File.Exists(frames) || new FileInfo(frames).Length > 64 * 1024 * 1024) continue;
            var start = _importOffsets.GetValueOrDefault(frames);
            if (start > new FileInfo(frames).Length) start = 0;
            foreach (var (line, endOffset) in ReadCompleteLines(frames, start))
            {
                _importOffsets[frames] = endOffset;
                if (line.Length > 1024 * 1024) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.GetProperty("Kind").GetString() != "DetectionCrop" || root.GetProperty("LabelStatus").GetString() != "UnverifiedCandidate") continue;
                    var path = ContainedPath(session, root.GetProperty("Image").GetString()!);
                    if (!File.Exists(path) || new FileInfo(path).Length > MaximumImageBytes) continue;
                    var bounds = root.GetProperty("CaptureBounds");
                    var width = bounds.GetProperty("Width").GetDouble();
                    var height = bounds.GetProperty("Height").GetDouble();
                    if (width <= 0 || height <= 0) continue;
                    var boxes = root.GetProperty("Detections").EnumerateArray().Select(b =>
                    {
                        var left = Math.Clamp(b.GetProperty("X").GetDouble() / width, 0, 1);
                        var top = Math.Clamp(b.GetProperty("Y").GetDouble() / height, 0, 1);
                        var right = Math.Clamp((b.GetProperty("X").GetDouble() + b.GetProperty("Width").GetDouble()) / width, 0, 1);
                        var bottom = Math.Clamp((b.GetProperty("Y").GetDouble() + b.GetProperty("Height").GetDouble()) / height, 0, 1);
                        return new LearningBox(b.GetProperty("ClassId").GetInt32(), left, top, right - left, bottom - top, b.GetProperty("Confidence").GetDouble());
                    }).Where(x => x.IsValid).ToArray();
                    var modelName = root.TryGetProperty("ModelName", out var modelProperty) ? Path.GetFileName(modelProperty.GetString()) : null;
                    var modelPath = modelName == null ? null : Path.Combine(_dataDirectory, "bin", "models", modelName);
                    if (modelName != null && !File.Exists(modelPath)) modelPath = Path.Combine(_root, "accepted-models", modelName);
                    var hash = modelPath != null && File.Exists(modelPath) ? CachedModelHash(modelPath) : new string('0', 64);
                    var reason = root.TryGetProperty("SelectionReason", out var reasonProperty) ? reasonProperty.GetString() : "Periodic";
                    reason = reason switch { "ConfidentPrediction" => "StableDetection", "LowConfidenceOrUncertain" => "UncertainDetection", "NoPrediction" => "NoDetection", _ => "Periodic" };
                    if (SubmitCapture(File.ReadAllBytes(path), Path.GetFileName(session), hash, reason, boxes) != null) count++;
                    if (count >= 100 || existingFiles.Length + count >= MaximumCaptures) return count;
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException) { }
            }
        }
        return count;
    }

    private static IEnumerable<(string Line, long EndOffset)> ReadCompleteLines(string path, long offset)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Position = offset;
        var buffer = new byte[16384];
        using var line = new MemoryStream();
        long position = offset;
        int read;
        while ((read = file.Read(buffer)) > 0)
            for (var index = 0; index < read; index++)
            {
                position++;
                if (buffer[index] == '\n')
                {
                    yield return (Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'), position);
                    line.SetLength(0);
                }
                else if (line.Length <= 1024 * 1024) line.WriteByte(buffer[index]);
            }
        // A final line without a newline may still be being written. Retry it next time.
    }

    private string CachedModelHash(string path)
    {
        var info = new FileInfo(path);
        if (_modelHashes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc) return cached.Hash;
        var hash = HashFile(path);
        _modelHashes[path] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    // Intended at model/session load, not per inference frame. The SHA-based overload avoids hashing altogether.
    public double? TryGetRecommendedConfidence(string modelPath)
    {
        lock (_gate) return ReadRecommendedConfidence(CachedModelHash(modelPath));
    }

    public double? ReadRecommendedConfidence(string modelSha256)
    {
        if (!IsHash(modelSha256)) return null;
        var path = Path.Combine(_root, "confidence-evaluations", modelSha256.ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var result = ReadJson<ConfidenceOptimization>(path);
            return result.Accepted && result.ModelSha256 == modelSha256 && double.IsFinite(result.ProposedConfidence) &&
                result.ProposedConfidence is >= .01 and <= .99 && PromotionPolicy.RejectionReasons(result.BaselineValidation, result.ProposedValidation).Length == 0
                ? result.ProposedConfidence : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // This method stores proposals only. Confidence, agreement and persistence never imply ground truth.
    public string? SubmitCapture(byte[] jpeg, string sessionId, string modelSha256, string reason, IEnumerable<LearningBox> proposals)
    {
        if (jpeg.Length < 4 || jpeg.LongLength > MaximumImageBytes || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9)
            throw new ArgumentException("A complete JPEG image is required.", nameof(jpeg));
        RequireId(sessionId);
        if (!IsHash(modelSha256)) throw new ArgumentException("Model SHA256 is required.");
        var boxes = proposals.Take(1001).ToArray();
        if (boxes.Length > 1000 || boxes.Any(x => !x.IsValid)) throw new ArgumentException("Invalid proposed boxes.");
        var allowedReasons = new[] { "StableDetection", "UncertainDetection", "LostTrack", "NoDetection", "Periodic" };
        if (!allowedReasons.Contains(reason)) throw new ArgumentException("Unknown capture reason.");
        lock (_gate)
        {
            var directory = Path.Combine(_root, "captures");
            System.IO.Directory.CreateDirectory(directory);
            var id = Hash(jpeg);
            if (File.Exists(Path.Combine(directory, id + ".json"))) return null;
            var existing = System.IO.Directory.GetFiles(directory, "*.jpg");
            if (existing.Length >= MaximumCaptures || existing.Sum(x => new FileInfo(x).Length) + jpeg.Length > MaximumStoredImageBytes)
                return null;
            File.WriteAllBytes(Path.Combine(directory, id + ".jpg"), jpeg);
            WriteJson(Path.Combine(directory, id + ".json"), new LearningCapture(id, sessionId, DateTime.UtcNow,
                id, modelSha256.ToLowerInvariant(), reason, boxes));
            return id;
        }
    }

    // Empty boxes explicitly mark a reviewed negative image. Caller must expose a real review action.
    public void RecordHumanReview(string captureId, IEnumerable<LearningBox> groundTruth)
    {
        if (!IsHash(captureId)) throw new ArgumentException("Invalid capture id.");
        var boxes = groundTruth.Take(1001).ToArray();
        if (boxes.Length > 1000 || boxes.Any(x => !x.IsValid)) throw new ArgumentException("Invalid ground truth boxes.");
        lock (_gate)
        {
            var path = Path.Combine(_root, "captures", captureId + ".json");
            var capture = ReadJson<LearningCapture>(path);
            VerifyImage(capture);
            WriteJson(path, capture with { ReviewStatus = "HumanReviewed", GroundTruth = boxes.Select(x => x with { Confidence = 1 }).ToArray() });
        }
    }

    public LearningDataset CreateDataset(string[] classNames, string? modelSha256 = null)
    {
        if (classNames.Length is < 1 or > 999 || classNames.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100))
            throw new ArgumentException("Class names must match the active detector.");
        if (modelSha256 != null && !IsHash(modelSha256)) throw new ArgumentException("Invalid source model hash.");
        lock (_gate)
        {
            var reviewed = ReadCaptures().Where(x => x.ReviewStatus == "HumanReviewed" && x.GroundTruth != null &&
                (modelSha256 == null || x.ModelSha256 == modelSha256)).ToArray();
            if (reviewed.Length < 40) throw new InvalidOperationException("ReviewedDatasetMissing: at least 40 reviewed frames are required.");
            var sessions = reviewed.GroupBy(x => x.SessionId).OrderBy(x => x.Min(c => c.CapturedUtc)).ToArray();
            if (sessions.Length < 2) throw new InvalidOperationException("IndependentValidationSessionMissing: record a second session.");
            // Entire sessions stay together; adjacent frames from one recording never cross the split.
            var validationSessionCount = Math.Max(1, sessions.Length / 5);
            var validationIds = sessions.TakeLast(validationSessionCount).Select(x => x.Key).ToHashSet();
            var train = reviewed.Where(x => !validationIds.Contains(x.SessionId)).ToArray();
            var validation = reviewed.Where(x => validationIds.Contains(x.SessionId)).ToArray();
            if (train.Length < 20 || validation.Length < 20 || validation.Sum(x => x.GroundTruth!.Length) < 20)
                throw new InvalidOperationException("InsufficientSplit: need 20 training frames and 20 validation frames with 20 labeled targets.");
            foreach (var capture in reviewed)
            {
                VerifyImage(capture);
                if (capture.GroundTruth!.Any(x => !x.IsValid || x.ClassId >= classNames.Length)) throw new InvalidDataException("Invalid reviewed labels.");
            }
            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            var destination = Path.Combine(_root, "datasets", id);
            DatasetFrame[] CopySplit(string name, LearningCapture[] captures)
            {
                var images = Path.Combine(destination, "images", name);
                var labels = Path.Combine(destination, "labels", name);
                System.IO.Directory.CreateDirectory(images);
                System.IO.Directory.CreateDirectory(labels);
                return captures.Select(capture =>
                {
                    var image = Path.Combine(images, capture.Id + ".jpg");
                    File.Copy(Path.Combine(_root, "captures", capture.Id + ".jpg"), image);
                    File.WriteAllLines(Path.Combine(labels, capture.Id + ".txt"), capture.GroundTruth!.Select(b =>
                        FormattableString.Invariant($"{b.ClassId} {b.X + b.Width / 2:R} {b.Y + b.Height / 2:R} {b.Width:R} {b.Height:R}")));
                    return new DatasetFrame(capture.Id, capture.SessionId, Path.GetRelativePath(destination, image), capture.ImageSha256, capture.GroundTruth!);
                }).ToArray();
            }
            var trainFrames = CopySplit("train", train);
            var validationFrames = CopySplit("val", validation);
            var manifestPath = Path.Combine(destination, "manifest.json");
            WriteJson(manifestPath, new { Schema = 1, Id = id, ClassNames = classNames, Train = trainFrames, Validation = validationFrames,
                AnnotationSource = "HumanReviewed", SplitUnit = "RecordingSession", ModelSha256 = modelSha256 });
            // JSON is valid YAML and avoids any YAML string injection from class names or paths.
            WriteJson(Path.Combine(destination, "dataset.yaml"), new { path = destination.Replace('\\', '/'), train = "images/train", val = "images/val",
                names = classNames.Select((name, index) => (name, index)).ToDictionary(x => x.index.ToString(CultureInfo.InvariantCulture), x => x.name) });
            return new(id, destination, classNames.ToArray(), trainFrames, validationFrames, HashFile(manifestPath));
        }
    }

    public EvaluationResult EvaluateCandidate(LearningDataset dataset, string baselinePath, string candidatePath,
        Func<string, ILocalDetector> detectorFactory, CancellationToken cancellationToken = default)
    {
        ValidateDataset(dataset);
        var baselineHash = HashFile(baselinePath);
        var candidateHash = HashFile(candidatePath);
        using var baseline = detectorFactory(baselinePath);
        using var candidate = detectorFactory(candidatePath);
        if (baseline.ClassCount != dataset.ClassNames.Length || candidate.ClassCount != baseline.ClassCount ||
            !baseline.ClassNames.SequenceEqual(dataset.ClassNames, StringComparer.Ordinal) || !candidate.ClassNames.SequenceEqual(baseline.ClassNames, StringComparer.Ordinal))
            throw new InvalidDataException("Detector classes do not match the reviewed dataset.");
        var baselineAccumulator = new MetricAccumulator();
        var candidateAccumulator = new MetricAccumulator();
        foreach (var frame in dataset.Validation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ContainedPath(dataset.Directory, frame.ImagePath);
            if (HashFile(path) != frame.ImageSha256) throw new InvalidDataException("Validation image changed.");
            // Alternate order to reduce systematic CPU warm-up/load bias. First frame is warmed up separately.
            if (baselineAccumulator.Images == 0) { baseline.Detect(path, .25); candidate.Detect(path, .25); }
            if (baselineAccumulator.Images % 2 == 0) { Measure(baseline, baselineAccumulator); Measure(candidate, candidateAccumulator); }
            else { Measure(candidate, candidateAccumulator); Measure(baseline, baselineAccumulator); }
            void Measure(ILocalDetector detector, MetricAccumulator accumulator)
            {
                var watch = Stopwatch.StartNew();
                var detected = detector.Detect(path, .25);
                watch.Stop();
                accumulator.Add(frame.GroundTruth, detected, watch.Elapsed.TotalMilliseconds);
            }
        }
        if (baselineHash != HashFile(baselinePath) || candidateHash != HashFile(candidatePath)) throw new InvalidDataException("Model changed during evaluation.");
        var before = baselineAccumulator.Result();
        var after = candidateAccumulator.Result();
        var reasons = PromotionPolicy.RejectionReasons(before, after);
        return new(dataset.Id, dataset.ManifestSha256, baselineHash, candidateHash, before, after, reasons.Length == 0, reasons, DateTime.UtcNow);
    }

    public ConfidenceOptimization OptimizeConfidence(LearningDataset dataset, string modelPath, double originalConfidence,
        Func<string, ILocalDetector> detectorFactory, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(originalConfidence) || originalConfidence is < .01 or > .99) throw new ArgumentOutOfRangeException(nameof(originalConfidence));
        ValidateDataset(dataset);
        var modelHash = HashFile(modelPath);
        using var detector = detectorFactory(modelPath);
        if (detector.ClassCount != dataset.ClassNames.Length || !detector.ClassNames.SequenceEqual(dataset.ClassNames, StringComparer.Ordinal)) throw new InvalidDataException("Class semantics mismatch.");
        var thresholds = Enumerable.Range(0, 13).Select(x => .25 + .05 * x).Append(originalConfidence).Distinct().Order().ToArray();
        var trainScores = thresholds.ToDictionary(x => x, _ => new MetricAccumulator());
        foreach (var frame in dataset.Train)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ContainedPath(dataset.Directory, frame.ImagePath);
            if (HashFile(path) != frame.ImageSha256) throw new InvalidDataException("Training image changed.");
            var predictions = detector.Detect(path, Math.Min(.25, originalConfidence));
            foreach (var threshold in thresholds)
                trainScores[threshold].Add(frame.GroundTruth, predictions.Where(x => x.Confidence >= threshold), 0);
        }
        // Choose on the training partition only, then evaluate ONE selected threshold on unseen sessions.
        var chosen = trainScores.OrderByDescending(x => x.Value.Result().F1).ThenBy(x => Math.Abs(x.Key - originalConfidence)).First().Key;
        var before = new MetricAccumulator();
        var after = new MetricAccumulator();
        foreach (var frame in dataset.Validation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ContainedPath(dataset.Directory, frame.ImagePath);
            if (HashFile(path) != frame.ImageSha256) throw new InvalidDataException("Validation image changed.");
            var predictions = detector.Detect(path, Math.Min(chosen, originalConfidence));
            before.Add(frame.GroundTruth, predictions.Where(x => x.Confidence >= originalConfidence), 0);
            after.Add(frame.GroundTruth, predictions.Where(x => x.Confidence >= chosen), 0);
        }
        if (modelHash != HashFile(modelPath)) throw new InvalidDataException("Model changed during evaluation.");
        var reasons = PromotionPolicy.RejectionReasons(before.Result(), after.Result());
        var result = new ConfidenceOptimization(modelHash, dataset.ManifestSha256, originalConfidence, chosen,
            before.Result(), after.Result(), reasons.Length == 0, reasons, DateTime.UtcNow);
        WriteJson(Path.Combine(_root, "confidence-evaluations", modelHash + ".json"), result);
        return result;
    }

    private static void ValidateDataset(LearningDataset dataset)
    {
        var manifest = Path.Combine(dataset.Directory, "manifest.json");
        if (HashFile(manifest) != dataset.ManifestSha256) throw new InvalidDataException("Dataset manifest changed.");
        // Compare the supplied object to its persisted manifest before trusting the split/labels.
        using var persisted = JsonDocument.Parse(File.ReadAllText(manifest));
        if (persisted.RootElement.GetProperty("AnnotationSource").GetString() != "HumanReviewed" ||
            persisted.RootElement.GetProperty("SplitUnit").GetString() != "RecordingSession")
            throw new InvalidDataException("Verified labels and a recording-session split are required.");
        if (JsonSerializer.Serialize(dataset.Train) != JsonSerializer.Serialize(persisted.RootElement.GetProperty("Train").Deserialize<DatasetFrame[]>()) ||
            JsonSerializer.Serialize(dataset.Validation) != JsonSerializer.Serialize(persisted.RootElement.GetProperty("Validation").Deserialize<DatasetFrame[]>()) ||
            JsonSerializer.Serialize(dataset.ClassNames) != JsonSerializer.Serialize(persisted.RootElement.GetProperty("ClassNames").Deserialize<string[]>()))
            throw new InvalidDataException("Dataset does not match its manifest.");
        if (dataset.Train.Select(x => x.SessionId).Intersect(dataset.Validation.Select(x => x.SessionId)).Any() ||
            dataset.Train.Select(x => x.ImageSha256).Intersect(dataset.Validation.Select(x => x.ImageSha256)).Any())
            throw new InvalidDataException("Training and validation overlap.");
    }

    // Only this combined operation can promote. It always runs real evaluation with the supplied inference engine.
    public ModelSelection EvaluateAndPromote(LearningDataset dataset, string baselinePath, string candidatePath,
        Func<string, ILocalDetector> detectorFactory, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var evaluation = EvaluateCandidate(dataset, baselinePath, candidatePath, detectorFactory, cancellationToken);
            var evaluationPath = Path.Combine(_root, "evaluations", evaluation.CandidateSha256 + ".json");
            WriteJson(evaluationPath, evaluation);
            if (!evaluation.Accepted) throw new InvalidOperationException("CandidateRejected: " + string.Join(", ", evaluation.Reasons));
            if (HashFile(candidatePath) != evaluation.CandidateSha256 || HashFile(baselinePath) != evaluation.BaselineSha256)
                throw new InvalidDataException("Model changed after evaluation.");
            var models = Path.Combine(_root, "accepted-models");
            System.IO.Directory.CreateDirectory(models);
            string StoreModel(string source, string sha)
            {
                var path = Path.Combine(models, sha + ".onnx");
                if (!File.Exists(path)) File.Copy(source, path);
                if (HashFile(path) != sha) throw new InvalidDataException("Stored model hash mismatch.");
                return path;
            }
            var accepted = StoreModel(candidatePath, evaluation.CandidateSha256);
            var previous = StoreModel(baselinePath, evaluation.BaselineSha256);
            var selection = new ModelSelection(accepted, evaluation.CandidateSha256, previous, evaluation.BaselineSha256, DateTime.UtcNow, evaluationPath);
            WriteJson(Path.Combine(_root, "model-selection.json"), selection);
            return selection;
        }
    }

    public string? ResolveSelectedModel(string? fallback)
    {
        var path = Path.Combine(_root, "model-selection.json");
        if (!File.Exists(path)) return fallback;
        try
        {
            var selection = ReadJson<ModelSelection>(path);
            var active = ContainedPath(Path.Combine(_root, "accepted-models"), Path.GetFileName(selection.ActivePath));
            return Path.GetFullPath(selection.ActivePath) == active && HashFile(active) == selection.ActiveSha256 ? active : fallback;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return fallback; }
    }

    public string Rollback()
    {
        lock (_gate)
        {
            var path = Path.Combine(_root, "model-selection.json");
            var selection = ReadJson<ModelSelection>(path);
            var previous = ContainedPath(Path.Combine(_root, "accepted-models"), Path.GetFileName(selection.PreviousPath));
            if (Path.GetFullPath(selection.PreviousPath) != previous || HashFile(previous) != selection.PreviousSha256)
                throw new InvalidDataException("Previous model is unavailable or changed.");
            WriteJson(path, new ModelSelection(previous, selection.PreviousSha256, selection.ActivePath, selection.ActiveSha256, DateTime.UtcNow, selection.EvaluationPath));
            return previous;
        }
    }

    public LearningCapture[] GetCaptures() { lock (_gate) return ReadCaptures().ToArray(); }
    private IEnumerable<LearningCapture> ReadCaptures()
    {
        var directory = Path.Combine(_root, "captures");
        if (!System.IO.Directory.Exists(directory)) yield break;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.json").Take(MaximumCaptures))
        {
            LearningCapture? capture = null;
            try
            {
                capture = ReadJson<LearningCapture>(path);
                if (!IsHash(capture.Id) || capture.ImageSha256 != capture.Id || Path.GetFileNameWithoutExtension(path) != capture.Id ||
                    capture.Proposals == null || capture.Proposals.Any(x => !x.IsValid) ||
                    capture.ReviewStatus is not ("Unreviewed" or "HumanReviewed")) capture = null;
                if (capture != null) RequireId(capture.SessionId);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { capture = null; }
            if (capture != null) yield return capture;
        }
    }
    private void VerifyImage(LearningCapture capture)
    {
        if (!IsHash(capture.Id) || capture.ImageSha256 != capture.Id || HashFile(Path.Combine(_root, "captures", capture.Id + ".jpg")) != capture.ImageSha256)
            throw new InvalidDataException("Capture image is missing or changed.");
    }
    private static T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Oversized learning metadata.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid learning metadata.");
    }
    private static void WriteJson<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    internal static string ContainedPath(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes dataset.");
        return path;
    }
    private static void RequireId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("Invalid recording session id.");
    }
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}
