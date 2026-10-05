namespace Aimmy2.AILogic;

internal static class DetectionPostProcessor
{
    // Raw YOLO output contains overlapping proposals. Keep one box per object/class.
    internal static List<Prediction> SuppressDuplicates(IEnumerable<Prediction> predictions, float threshold = .45f)
    {
        var kept = new List<Prediction>();
        foreach (var next in predictions.Where(p => float.IsFinite(p.Confidence) && p.Confidence is >= 0 and <= 1 &&
            float.IsFinite(p.Rectangle.X) && float.IsFinite(p.Rectangle.Y) && float.IsFinite(p.Rectangle.Width) &&
            float.IsFinite(p.Rectangle.Height) && p.Rectangle.Width > 1 && p.Rectangle.Height > 1)
            .OrderByDescending(p => p.Confidence).Take(1000))
        {
            if (kept.Any(previous => previous.ClassId == next.ClassId && IoU(previous, next) > threshold)) continue;
            kept.Add(next);
            if (kept.Count >= 100) break;
        }
        return kept;
    }
    private static double IoU(Prediction a, Prediction b)
    {
        var overlap = System.Drawing.RectangleF.Intersect(a.Rectangle, b.Rectangle);
        double area = Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
        return area / (a.Rectangle.Width * a.Rectangle.Height + b.Rectangle.Width * b.Rectangle.Height - area);
    }
}
