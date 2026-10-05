using Aimmy2.AILogic;
using System.Drawing;

int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}
Prediction Box(float x = 10, float y = 20, float width = 80, float height = 160, float confidence = .9f, int classId = 0) => new()
{
    Rectangle = new RectangleF(x, y, width, height), Confidence = confidence, ClassId = classId,
    ScreenCenterX = -1920 + x + width / 2, ScreenCenterY = y + height / 2,
    CenterXTranslated = (x + width / 2) / 640, CenterYTranslated = (y + height / 2) / 640
};

Check(DetectionPostProcessor.SuppressDuplicates(Array.Empty<Prediction>()).Count == 0, "empty detector output stays empty");
var low = Box(confidence: .65f);
var high = Box(x: 11, confidence: .95f);
var retained = DetectionPostProcessor.SuppressDuplicates(new[] { low, high });
Check(retained.Count == 1 && ReferenceEquals(retained[0], high), "highest-confidence overlapping proposal is retained with its original object identity");
Check(retained[0].ScreenCenterX < 0 && retained[0].CenterXTranslated == high.CenterXTranslated,
    "NMS preserves monitor offsets and coordinates needed by downstream selection");

var differentClass = Box(classId: 1);
retained = DetectionPostProcessor.SuppressDuplicates(new[] { high, differentClass });
Check(retained.Count == 2, "overlapping detections from different classes are not suppressed");
var first = Box();
var identical = Box();
retained = DetectionPostProcessor.SuppressDuplicates(new[] { first, identical });
Check(retained.Count == 1 && ReferenceEquals(retained[0], first), "equal-score duplicates have deterministic input-order tie breaking");
var edge = Box(x: 90);
Check(DetectionPostProcessor.SuppressDuplicates(new[] { first, edge }).Count == 2, "touching edges without intersection retain separate boxes");
var inside = Box(x: 30, y: 40, width: 20, height: 30);
Check(DetectionPostProcessor.SuppressDuplicates(new[] { first, inside }).Count == 2, "small contained boxes are judged by IoU rather than containment alone");
Check(DetectionPostProcessor.SuppressDuplicates(new[] { first, Box(x: 25) }, .9f).Count == 2,
    "configured higher IoU threshold preserves distinct overlapping proposals");
Check(DetectionPostProcessor.SuppressDuplicates(new[] { first, Box(x: 25) }, .2f).Count == 1,
    "configured lower IoU threshold suppresses overlapping proposals");
Check(DetectionPostProcessor.SuppressDuplicates(new[] { Box(x: -100), Box(x: -99, confidence: .7f) }).Count == 1,
    "IoU remains correct with negative coordinate origins");

var invalid = new[]
{
    Box(confidence: float.NaN), Box(confidence: float.PositiveInfinity), Box(confidence: -1), Box(confidence: 2),
    Box(x: float.NaN), Box(y: float.PositiveInfinity), Box(width: float.NaN), Box(height: float.PositiveInfinity),
    Box(width: 0), Box(height: -2), Box(width: 1), Box(height: 1)
};
Check(DetectionPostProcessor.SuppressDuplicates(invalid).Count == 0, "non-finite, degenerate and invalid-confidence proposals are removed");
retained = DetectionPostProcessor.SuppressDuplicates(invalid.Concat(new[] { high }));
Check(retained.Count == 1 && ReferenceEquals(retained[0], high), "invalid proposals cannot poison valid NMS output");

var many = Enumerable.Range(0, 130).Select(i => Box(x: i * 100, confidence: .99f - i * .001f)).ToArray();
retained = DetectionPostProcessor.SuppressDuplicates(many.Reverse());
Check(retained.Count == 100, "retained detections have a bounded count");
Check(retained[0].Confidence == many[0].Confidence && retained[^1].Confidence == many[99].Confidence,
    "detection count bound keeps the strongest candidates rather than original ordering");
for (int i = 1; i < retained.Count; i++)
    Check(retained[i - 1].Confidence >= retained[i].Confidence, "retained results remain confidence ordered");

Console.WriteLine($"{checks} local automation checks passed (actual Prediction/NMS implementation, synthetic detector outputs).");
