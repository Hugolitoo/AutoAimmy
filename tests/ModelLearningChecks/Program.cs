using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Aimmy2.ModelLearning;

var fixture = Path.Combine(Path.GetTempPath(), "AutoAimmy-ModelLearning-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Throws(Action action, string name) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { checks++; Console.WriteLine("PASS " + name); return; } throw new Exception(name); }
byte[] Image(int index)
{
    using var bitmap = new Bitmap(64, 64);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.FromArgb(255, index % 256, (index * 7) % 256, (index * 17) % 256));
        graphics.FillRectangle(Brushes.White, index % 32, 16, 8, 16);
    }
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Jpeg);
    return stream.ToArray();
}

try
{
    var service = new ModelLearningCoordinator(fixture);
    var baseline = Path.Combine(fixture, "baseline.onnx");
    var candidate = Path.Combine(fixture, "candidate.onnx");
    File.WriteAllText(baseline, "baseline fixture for hash/invariants only");
    File.WriteAllText(candidate, "candidate fixture for hash/invariants only");
    var box = new LearningBox(0, .2, .2, .2, .4, .9);
    Check(box.IsValid && !(box with { X = double.NaN }).IsValid, "normalized boxes reject non-finite values");
    Check(!(box with { Width = 1 }).IsValid, "out-of-frame boxes rejected");
    var initial = service.Inspect(baseline);
    Check(initial.Blockers.Contains("TrainableSourceWeightsMissing") && initial.Blockers.Contains("ReviewedDatasetMissing"), "missing weights and reviewed data explicit");
    var first = service.SubmitCapture(Image(0), "session-a", ModelLearningCoordinator.HashFile(baseline), "StableDetection", [box]);
    Check(first != null && service.GetCaptures().Single().ReviewStatus == "Unreviewed", "confident detections remain proposals");
    Check(service.SubmitCapture(Image(0), "session-b", ModelLearningCoordinator.HashFile(baseline), "StableDetection", [box]) == null, "duplicate image cannot enter different session split");
    Throws(() => service.SubmitCapture(Image(1), "../escape", ModelLearningCoordinator.HashFile(baseline), "StableDetection", [box]), "session traversal rejected");
    Throws(() => service.GetCaptureImagePath("../escape"), "capture traversal rejected");
    Throws(() => service.CreateDataset(["enemy"]), "unreviewed data cannot train");
    service.RecordHumanReview(first!, []);
    Check(service.GetCaptures().Single().GroundTruth?.Length == 0, "reviewed negative image supported");
    service.RecordHumanReview(first!, [box]);
    for (var i = 1; i < 40; i++)
    {
        var id = service.SubmitCapture(Image(i), i < 20 ? "session-a" : "session-b", ModelLearningCoordinator.HashFile(baseline), i % 2 == 0 ? "StableDetection" : "UncertainDetection", [box]);
        service.RecordHumanReview(id!, [box]);
    }
    var dataset = service.CreateDataset(["enemy"]);
    Throws(() => service.CreateDataset(["enemy"], ModelLearningCoordinator.HashFile(candidate)), "other-model annotations cannot form a dataset for the active model");
    Check(dataset.Train.Length == 20 && dataset.Validation.Length == 20, "40 reviewed frames produce disjoint session partitions");
    Check(!dataset.Train.Select(x => x.SessionId).Intersect(dataset.Validation.Select(x => x.SessionId)).Any(), "no session leakage");
    Check(!dataset.Train.Select(x => x.ImageSha256).Intersect(dataset.Validation.Select(x => x.ImageSha256)).Any(), "no duplicate leakage");
    var accumulator = new MetricAccumulator();
    accumulator.Add([box], [box, box with { Confidence = .7 }], 1);
    var metrics = accumulator.Result();
    Check(metrics.TruePositives == 1 && metrics.FalsePositives == 1 && metrics.FalseNegatives == 0, "duplicate prediction cannot match the same target twice");
    var wrongClass = new MetricAccumulator();
    wrongClass.Add([box], [box with { ClassId = 1 }], 1);
    Check(wrongClass.Result().FalsePositives == 1 && wrongClass.Result().FalseNegatives == 1, "class mismatch is a false positive and missed target");
    Check(PromotionPolicy.RejectionReasons(new(20, 20, 20, 1, 0, 1), new(20, 20, 20, 1, 0, 1)).Contains("NoMeasuredAccuracyGain"), "unchanged model not promoted");
    Check(PromotionPolicy.RejectionReasons(new(20, 20, 18, 3, 2, 1), new(20, 20, 20, 0, 0, 10)).Contains("LatencyRegression"), "slower candidate gated");
    Check(PromotionPolicy.RejectionReasons(new(20, 20, 18, 3, 2, 1), new(20, 20, 20, 0, 0, 1)).Length == 0, "measured improvement accepted by policy");
    // Deterministic detector fixtures test transaction behavior, not ML quality or training.
    ILocalDetector Factory(string path) => new FixtureDetector(path == baseline ? [box, new(0,.65,.2,.2,.4,.3)] : [box]);
    var comparison = service.EvaluateCandidate(dataset, baseline, candidate, Factory);
    Check(comparison.Accepted && comparison.Candidate.TruePositives == 20 && comparison.Baseline.FalsePositives == 20, "comparison uses complete verified validation set");
    var optimized = service.OptimizeConfidence(dataset, baseline, .25, Factory);
    Check(optimized.Accepted && optimized.ProposedConfidence > .3 && optimized.ProposedValidation.FalsePositives == 0, "threshold picked on training split and confirmed on validation split");
    Check(service.TryGetRecommendedConfidence(baseline) == optimized.ProposedConfidence && service.ReadRecommendedConfidence(ModelLearningCoordinator.HashFile(baseline)) == optimized.ProposedConfidence, "accepted recommendation survives model-hash lookup");
    var repeated = service.OptimizeConfidence(dataset, baseline, optimized.ProposedConfidence, Factory);
    Check(!repeated.Accepted && service.TryGetRecommendedConfidence(baseline) == optimized.ProposedConfidence,
        "rejected later optimization preserves the last accepted threshold");
    Throws(() => service.EvaluateCandidate(dataset, baseline, candidate, path => new FixtureDetector([box], path == candidate ? "friend" : "enemy")), "same class count with different semantics is rejected");
    var selected = service.EvaluateAndPromote(dataset, baseline, candidate, Factory);
    Check(File.Exists(selected.ActivePath) && service.ResolveSelectedModel(baseline) == selected.ActivePath, "accepted candidate stored without replacing source");
    Check(service.CreateDataset(["enemy"], ModelLearningCoordinator.HashFile(candidate)).Train.Length == 20,
        "verified labels remain usable after a class-compatible model upgrade");
    Throws(() => service.CreateDataset(["friend"], ModelLearningCoordinator.HashFile(candidate)),
        "label lineage is not reused for different class semantics");

    // Exercise real promotion transactions past the ancestry bound. Detector outputs
    // are deterministic fixtures; this checks state retention, not model accuracy.
    var ancestry = new ModelLearningCoordinator(Path.Combine(fixture, "ancestry"));
    var generationBase = baseline;
    for (int generation = 0; generation < 40; generation++)
    {
        string generationCandidate = Path.Combine(fixture, "generation-" + generation + ".onnx");
        File.WriteAllText(generationCandidate, "generation fixture " + generation);
        var generationSelection = ancestry.EvaluateAndPromote(dataset, generationBase, generationCandidate,
            path => new FixtureDetector(path == generationBase ? [box, new(0, .65, .2, .2, .4, .3)] : [box]));
        generationBase = generationSelection.ActivePath;
    }
    var ancestors = ancestry.RelatedModelHashes(ModelLearningCoordinator.HashFile(generationBase), ["enemy"]);
    Check(ancestors.Count == 33 && ancestors.Contains(ModelLearningCoordinator.HashFile(baseline)),
        "original reviewed source survives forty accepted generations with bounded ancestry");

    var regressionRoot = Path.Combine(fixture, "regression");
    var regression = new ModelLearningCoordinator(regressionRoot);
    var secondBox = new LearningBox(0, .65, .2, .2, .4, 1);
    for (int index = 0; index < 40; index++)
    {
        string id = regression.SubmitCapture(Image(index), index < 20 ? "old-train" : "old-validation",
            ModelLearningCoordinator.HashFile(baseline), "StableDetection", [box, secondBox], DateTime.UtcNow.AddDays(-1))!;
        regression.RecordHumanReview(id, [box, secondBox]);
    }
    var oldRegressionData = regression.CreateDataset(["enemy"]);
    var upgraded = regression.EvaluateAndPromote(oldRegressionData, baseline, candidate,
        path => new FixtureDetector(path == baseline ? [box] : [box, secondBox]));
    int calls = 0;
    ILocalDetector Degraded(string path) { calls++; return new FixtureDetector(path == upgraded.ActivePath ? [box] : [box, secondBox]); }
    Check(!regression.RevalidateSelectedModel(oldRegressionData, upgraded.ActivePath, Degraded) && calls == 0,
        "automatic regression check refuses evidence captured before model selection");
    for (int index = 40; index < 60; index++)
    {
        string id = regression.SubmitCapture(Image(index), "fresh-validation", ModelLearningCoordinator.HashFile(candidate),
            "StableDetection", [box, secondBox])!;
        regression.RecordHumanReview(id, [box, secondBox]);
    }
    var freshRegressionData = regression.CreateDataset(["enemy"], ModelLearningCoordinator.HashFile(candidate));
    Check(freshRegressionData.Validation.Length == 20 && freshRegressionData.Validation.Sum(x => x.GroundTruth.Length) == 40,
        "automatic regression test uses forty fresh reviewed targets from a separate session");
    Check(!regression.RevalidateSelectedModel(freshRegressionData, upgraded.ActivePath, _ => new FixtureDetector([box, secondBox])) &&
        regression.ResolveSelectedModel(baseline) == upgraded.ActivePath, "equivalent fresh accuracy retains the accepted model");
    var correctedId = freshRegressionData.Validation[0].CaptureId;
    regression.RecordHumanReview(correctedId, [box]);
    calls = 0;
    Check(!regression.RevalidateSelectedModel(freshRegressionData, upgraded.ActivePath, Degraded) && calls == 0,
        "changed human labels invalidate an outdated regression dataset");
    regression.RecordHumanReview(correctedId, [box, secondBox]);
    Check(regression.RevalidateSelectedModel(freshRegressionData, upgraded.ActivePath, Degraded) &&
        regression.ResolveSelectedModel(baseline) == upgraded.PreviousPath,
        "measured degradation on fresh verified targets automatically restores the previous model");

    var runtimeRoot = Path.Combine(fixture, "runtime-extraction");
    Directory.CreateDirectory(runtimeRoot);
    string runtimeArchive = Path.Combine(runtimeRoot, "cached-runtime.zip");
    using (var zip = ZipFile.Open(runtimeArchive, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("python.exe").Open())) writer.Write("fixture - never executed");
        using (var writer = new StreamWriter(zip.CreateEntry("Lib/site-packages/torch/__init__.py").Open())) writer.Write("fixture");
        using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("must not escape");
    }
    Throws(() => TrainingRuntimeSetup.ExtractVerifiedArchive(runtimeArchive, runtimeRoot), "runtime archive path traversal rejected");
    Check(File.Exists(runtimeArchive) && !File.Exists(Path.Combine(runtimeRoot, "escaped.txt")) &&
        Directory.GetDirectories(runtimeRoot, "runtime-staging-*").Length == 0 && !Directory.Exists(Path.Combine(runtimeRoot, "runtime")),
        "failed extraction cleans its partial staging and preserves the cached archive");
    using (var zip = ZipFile.Open(runtimeArchive, ZipArchiveMode.Update)) zip.GetEntry("../escaped.txt")!.Delete();
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { TrainingRuntimeSetup.ExtractVerifiedArchive(runtimeArchive, runtimeRoot, cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }
    Check(File.Exists(runtimeArchive) && Directory.GetDirectories(runtimeRoot, "runtime-staging-*").Length == 0,
        "cancelled extraction preserves resumable archive without orphaned staging");
    TrainingRuntimeSetup.ExtractVerifiedArchive(runtimeArchive, runtimeRoot);
    Check(File.Exists(Path.Combine(runtimeRoot, "runtime", "python.exe")) && Directory.GetDirectories(runtimeRoot, "runtime-staging-*").Length == 0,
        "retry atomically installs the completed runtime directory");

    Check(File.ReadAllText(baseline).StartsWith("baseline fixture"), "baseline preserved");
    Check(service.Rollback() == selected.PreviousPath, "rollback restores verified previous model");
    File.AppendAllText(selected.PreviousPath, "tampered");
    Check(service.ResolveSelectedModel(baseline) == baseline, "tampered accepted model falls back");
    var altered = dataset with { Validation = dataset.Validation.Select(x => x with { GroundTruth = [] }).ToArray() };
    Throws(() => service.EvaluateCandidate(altered, baseline, candidate, Factory), "changed label object rejected against manifest");
    File.AppendAllText(Path.Combine(dataset.Directory, dataset.Validation[0].ImagePath), "tampered");
    Throws(() => service.EvaluateCandidate(dataset, baseline, candidate, Factory), "changed image rejected against hash");

    var recordings = Path.Combine(fixture, "local-capture", "recorded-session");
    Directory.CreateDirectory(Path.Combine(recordings, "images"));
    File.WriteAllBytes(Path.Combine(recordings, "images", "detection-00001.jpg"), Image(100));
    File.WriteAllText(Path.Combine(recordings, "frames.jsonl"), JsonSerializer.Serialize(new { Kind = "DetectionCrop", Image = "images/detection-00001.jpg", LabelStatus = "UnverifiedCandidate", CaptureBounds = new { X = 100, Y = 50, Width = 640, Height = 640 }, Detections = new[] { new { X = 128, Y = 128, Width = 128, Height = 256, ClassId = 0, Confidence = .9 } }, SelectionReason = "ConfidentPrediction", ModelName = "missing.onnx" }) + Environment.NewLine);
    Check(service.ImportRecordingCaptures() == 1, "capture recorder format imports without treating labels as truth");
    var imported = service.GetCaptures().Single(x => x.SessionId == "recorded-session");
    Check(imported.Proposals.Single().X == .2 && imported.ReviewStatus == "Unreviewed", "capture pixels normalized to original detector frame");
    Check(imported.CapturedUtc == DateTime.MinValue, "recording with unknown capture time is never fresh rollback evidence");
    Check(service.ImportRecordingCaptures() == 0, "import idempotent");
    File.WriteAllBytes(Path.Combine(recordings, "images", "detection-00002.jpg"), Image(101));
    var originalCaptureTime = DateTime.UtcNow.AddDays(-2);
    var partialLine = JsonSerializer.Serialize(new { Kind = "DetectionCrop", CapturedUtc = originalCaptureTime, Image = "images/detection-00002.jpg", LabelStatus = "UnverifiedCandidate", CaptureBounds = new { X = 100, Y = 50, Width = 640, Height = 640 }, Detections = Array.Empty<object>(), SelectionReason = "NoPrediction", ModelName = "missing.onnx" });
    File.AppendAllText(Path.Combine(recordings, "frames.jsonl"), partialLine);
    Check(service.ImportRecordingCaptures() == 0, "incomplete final recording line is not consumed");
    File.AppendAllText(Path.Combine(recordings, "frames.jsonl"), Environment.NewLine);
    Check(service.ImportRecordingCaptures() == 1, "completed recording line resumes from byte cursor");
    Check(service.GetCaptures().Single(x => x.SessionId == "recorded-session" && x.Proposals.Length == 0).CapturedUtc == originalCaptureTime,
        "late import preserves original recording time for fresh-evidence checks");
    var training = new TrainingJobCoordinator(fixture);
    await training.StartAsync(baseline);
    Check(training.State.Status == "Blocked" && training.State.Blockers.Contains("PythonRuntimeMissing"), "training blocks without a local runtime and never executes a discovered checkpoint");
    var sourceFixture = Path.Combine(fixture, "source-fixture.pt");
    File.WriteAllText(sourceFixture, "not a checkpoint - tests trust record only, never executed");
    training.RegisterTrustedSource(baseline, sourceFixture);
    Check(training.State.Status == "SourceRegistered", "source trust binds explicit checkpoint to active model");
    typeof(TrainingJobCoordinator).GetMethod("RegisterDerivedSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(training, new object[] { candidate, sourceFixture, ModelLearningCoordinator.HashFile(sourceFixture), "OwnedOfflineTrainingJob" });
    Check(File.Exists(Path.Combine(fixture, "learning", "trusted-sources", ModelLearningCoordinator.HashFile(candidate) + ".json")),
        "accepted derived weights can be bound to the next model without another import");
    var trustedCopy = Directory.GetFiles(Path.Combine(fixture, "learning", "source"), "*.pt").Single();
    File.AppendAllText(trustedCopy, "changed");
    await training.StartAsync(baseline);
    Check(training.State.Status == "Blocked" && training.State.Blockers.Contains("SourceWeightsChanged"), "changed source checkpoint loses execution trust");

    if (args.Length > 0)
    {
        using var detector = new OnnxLearningDetector(Path.GetFullPath(args[0]));
        var imagePath = service.GetCaptureImagePath(first!);
        var detected = detector.Detect(imagePath, .25);
        Check(detector.ClassCount > 0 && detected.All(x => x.IsValid), "real ONNX model loads and executes on synthetic image");
        Console.WriteLine("ONNX classes: " + string.Join(", ", detector.ClassNames));
        // Evaluating the same real model must produce identical detection accuracy and cannot pass improvement gate.
        var realDataset = service.CreateDataset(detector.ClassNames);
        var real = service.EvaluateCandidate(realDataset, args[0], args[0], p => new OnnxLearningDetector(p));
        Check(!real.Accepted && real.Reasons.Contains("NoMeasuredAccuracyGain"), "same real ONNX candidate cannot claim accuracy improvement");
        if (args.Length >= 3)
        {
            string realRoot = Path.GetFullPath(args[2]);
            string manifestPath = Path.Combine(realRoot, "manifest.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var integration = new LearningDataset(manifest.RootElement.GetProperty("Id").GetString()!, realRoot,
                manifest.RootElement.GetProperty("ClassNames").Deserialize<string[]>()!,
                manifest.RootElement.GetProperty("Train").Deserialize<DatasetFrame[]>()!,
                manifest.RootElement.GetProperty("Validation").Deserialize<DatasetFrame[]>()!, ModelLearningCoordinator.HashFile(manifestPath));
            var evaluation = service.EvaluateCandidate(integration, args[0], args[1], p => new OnnxLearningDetector(p));
            Check(evaluation.Candidate.Images == 20 && double.IsFinite(evaluation.Candidate.F1),
                "actual locally trained export executes through independent C# evaluation");
            Console.WriteLine("Synthetic training evaluation (not promoted): " + JsonSerializer.Serialize(evaluation));
        }
    }
    Console.WriteLine($"{checks} model learning checks passed. Synthetic images; no gameplay training performed.");
}
finally
{
    // The fixture was created under an explicit isolated temp directory in this process.
    if (Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture, true);
}

sealed class FixtureDetector(LearningBox[] boxes, string name = "enemy") : ILocalDetector
{
    public int ClassCount => 1;
    public string[] ClassNames => [name];
    public IReadOnlyList<LearningBox> Detect(string imagePath, double minimumConfidence) => boxes.Where(x => x.Confidence >= minimumConfidence).ToArray();
    public void Dispose() { }
}
