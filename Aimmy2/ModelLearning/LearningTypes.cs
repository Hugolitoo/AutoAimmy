namespace Aimmy2.ModelLearning;

// Coordinates are normalized to the captured detector frame, never the desktop cursor.
public sealed record LearningBox(int ClassId, double X, double Y, double Width, double Height, double Confidence = 1)
{
    public bool IsValid => ClassId >= 0 && ClassId < 1000 &&
        new[] { X, Y, Width, Height, Confidence }.All(double.IsFinite) &&
        Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X + Width <= 1.000001 && Y + Height <= 1.000001 &&
        Confidence is >= 0 and <= 1;
}

public sealed record LearningCapture(string Id, string SessionId, DateTime CapturedUtc, string ImageSha256,
    string ModelSha256, string Reason, LearningBox[] Proposals, string ReviewStatus = "Unreviewed",
    LearningBox[]? GroundTruth = null);

public sealed record LearningState(string Status, int PendingImages, int ReviewedImages, string[] SourceWeights,
    string[] Blockers, string Detail, string? ActiveModel = null);

public sealed record DatasetFrame(string CaptureId, string SessionId, string ImagePath, string ImageSha256, LearningBox[] GroundTruth);
public sealed record LearningDataset(string Id, string Directory, string[] ClassNames, DatasetFrame[] Train,
    DatasetFrame[] Validation, string ManifestSha256);

public sealed record DetectionMetrics(int Images, int GroundTruthBoxes, int TruePositives, int FalsePositives,
    int FalseNegatives, double MeanMilliseconds)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 0 : (double)TruePositives / (TruePositives + FalsePositives);
    public double Recall => TruePositives + FalseNegatives == 0 ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);
}

public sealed record EvaluationResult(string DatasetId, string DatasetSha256, string BaselineSha256,
    string CandidateSha256, DetectionMetrics Baseline, DetectionMetrics Candidate, bool Accepted,
    string[] Reasons, DateTime EvaluatedUtc);

public sealed record ModelSelection(string ActivePath, string ActiveSha256, string PreviousPath, string PreviousSha256,
    DateTime SelectedUtc, string EvaluationPath);

public sealed record ConfidenceOptimization(string ModelSha256, string DatasetSha256, double OriginalConfidence,
    double ProposedConfidence, DetectionMetrics BaselineValidation, DetectionMetrics ProposedValidation,
    bool Accepted, string[] Reasons, DateTime EvaluatedUtc);

public interface ILocalDetector : IDisposable
{
    int ClassCount { get; }
    string[] ClassNames { get; }
    IReadOnlyList<LearningBox> Detect(string imagePath, double minimumConfidence);
}
