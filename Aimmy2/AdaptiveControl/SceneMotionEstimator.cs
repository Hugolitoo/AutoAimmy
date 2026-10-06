namespace Aimmy2.AdaptiveControl;

public sealed record SceneMotion(double X, double Y, double Scale, double Confidence, int Matches)
{
    public bool Reliable => Matches >= 8 && Confidence >= .7 && double.IsFinite(X) && double.IsFinite(Y) && Scale is > .85 and < 1.15;
    public static SceneMotion Unknown { get; } = new(0, 0, 1, 0, 0);
}

/// <summary>Robust background patch registration. Pixels only; no claim about physical camera angles.</summary>
public static class SceneMotionEstimator
{
    public static SceneMotion Estimate(byte[] previous, byte[] current, int width, int height,
        IReadOnlyList<(double X, double Y, double Width, double Height)> excluded, int search = 8)
    {
        if (width < 48 || height < 48 || width > 320 || height > 240 || previous.Length != width * height || current.Length != previous.Length)
            return SceneMotion.Unknown;
        search = Math.Clamp(search, 1, 10);
        var matches = new List<(double X, double Y, double Dx, double Dy)>();
        bool Masked(int x, int y) => y > height * .82 || excluded.Any(b =>
            x >= b.X - 5 && x <= b.X + b.Width + 5 && y >= b.Y - 5 && y <= b.Y + b.Height + 5);
        int margin = search + 4;
        for (int y = margin; y < height - margin; y += 12)
        for (int x = margin; x < width - margin; x += 12)
        {
            if (Masked(x, y)) continue;
            double mean = 0, variance = 0;
            for (int py = -2; py <= 2; py++) for (int px = -2; px <= 2; px++)
            { double value = previous[(y + py) * width + x + px]; mean += value; variance += value * value; }
            if (variance / 25 - Math.Pow(mean / 25, 2) < 150) continue;
            double best = double.MaxValue, second = double.MaxValue;
            int bestX = 0, bestY = 0;
            for (int dy = -search; dy <= search; dy++) for (int dx = -search; dx <= search; dx++)
            {
                if (Masked(x + dx, y + dy)) continue;
                double cost = 0;
                for (int py = -2; py <= 2; py++) for (int px = -2; px <= 2; px++)
                    cost += Math.Abs(previous[(y + py) * width + x + px] - current[(y + dy + py) * width + x + dx + px]);
                if (cost < best) { second = best; best = cost; bestX = dx; bestY = dy; }
                else if (cost < second) second = cost;
            }
            if (best / 25 > 24 || second - best < 50 || Math.Abs(bestX) == search || Math.Abs(bestY) == search) continue;
            // Refine below one thumbnail pixel, otherwise slow mouse motion is quantized
            // too coarsely to measure an effective camera gain.
            double refinedX = bestX, refinedY = bestY;
            double Sample(double sx, double sy)
            {
                int ix = Math.Clamp((int)Math.Floor(sx), 0, width - 2), iy = Math.Clamp((int)Math.Floor(sy), 0, height - 2);
                double fx = Math.Clamp(sx - ix, 0, 1), fy = Math.Clamp(sy - iy, 0, 1);
                return (1 - fy) * ((1 - fx) * current[iy * width + ix] + fx * current[iy * width + ix + 1]) +
                    fy * ((1 - fx) * current[(iy + 1) * width + ix] + fx * current[(iy + 1) * width + ix + 1]);
            }
            for (int iteration = 0; iteration < 5; iteration++)
            {
                double xx = 0, xy = 0, yy = 0, rx = 0, ry = 0;
                for (int py = -2; py <= 2; py++) for (int px = -2; px <= 2; px++)
                {
                    double sx = x + refinedX + px, sy = y + refinedY + py;
                    double gx = (Sample(sx + 1, sy) - Sample(sx - 1, sy)) / 2;
                    double gy = (Sample(sx, sy + 1) - Sample(sx, sy - 1)) / 2;
                    double residual = previous[(y + py) * width + x + px] - Sample(sx, sy);
                    xx += gx * gx; xy += gx * gy; yy += gy * gy; rx += gx * residual; ry += gy * residual;
                }
                double determinant = xx * yy - xy * xy;
                if (determinant < 100) break;
                double stepX = Math.Clamp((yy * rx - xy * ry) / determinant, -.4, .4);
                double stepY = Math.Clamp((xx * ry - xy * rx) / determinant, -.4, .4);
                refinedX += stepX; refinedY += stepY;
                if (Math.Abs(stepX) + Math.Abs(stepY) < .01) break;
            }
            if (Math.Abs(refinedX - bestX) > 1 || Math.Abs(refinedY - bestY) > 1) continue;
            matches.Add((x - width / 2.0, y - height / 2.0, refinedX, refinedY));
        }
        if (matches.Count < 8) return SceneMotion.Unknown;
        double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
        // Fit translation plus isotropic scale to background consensus. Trim independent objects.
        double tx = Median(matches.Select(m => m.Dx)), ty = Median(matches.Select(m => m.Dy)), scale = 0;
        var inliers = matches;
        for (int pass = 0; pass < 3; pass++)
        {
            double mx = inliers.Average(m => m.X), my = inliers.Average(m => m.Y);
            double mdx = inliers.Average(m => m.Dx), mdy = inliers.Average(m => m.Dy);
            double denominator = inliers.Sum(m => Math.Pow(m.X - mx, 2) + Math.Pow(m.Y - my, 2));
            scale = denominator > 1 ? inliers.Sum(m => (m.X - mx) * (m.Dx - mdx) + (m.Y - my) * (m.Dy - mdy)) / denominator : 0;
            tx = mdx - scale * mx; ty = mdy - scale * my;
            inliers = matches.Where(m => Math.Abs(m.Dx - tx - scale * m.X) <= 1.5 && Math.Abs(m.Dy - ty - scale * m.Y) <= 1.5).ToList();
            if (inliers.Count < 8) return SceneMotion.Unknown;
        }
        double confidence = (double)inliers.Count / matches.Count;
        return new(tx, ty, 1 + scale, confidence, inliers.Count);
    }
}
