namespace Aimmy2.Adaptive;

/// <summary>Null denotes an unobserved metric, never a fabricated zero.</summary>
public sealed record EngagementFeatures(long TargetId, string Context, string EndReason,
    bool Truncated, IReadOnlyDictionary<string, double?> Metrics);

public sealed class FeatureExtractor
{
    public double MovementThresholdPixels { get; init; } = 2;
    public double CorrectionThresholdPixels { get; init; } = 3;

    public EngagementFeatures Extract(Engagement engagement)
    {
        var s = engagement.Samples;
        var metrics = new Dictionary<string, double?>();
        if (s.Count == 0) return new(engagement.TargetId, "Unknown", engagement.EndReason, engagement.Truncated, metrics);
        var first = s[0];
        var target = first.Target;
        double path = 0, peak = 0, acceleration = 0, jerk = 0, curvature = 0;
        double? reaction = null, acquisition = null, click = null;
        double previousVelocity = 0, previousAcceleration = 0;
        double errorSum = 0, normalizedErrorSum = 0, errorTime = 0;
        double errorVelocitySquared = 0, errorVelocityTime = 0;
        double overshoot = 0, correctionAmplitude = 0, correctionTotal = 0;
        int corrections = 0;
        bool moved = false, passed = false;
        double lastProjection = 0;
        double axisX = (target?.X ?? first.CursorX) - first.CursorX;
        double axisY = (target?.Y ?? first.CursorY) - first.CursorY;
        double axisLength = Math.Sqrt(axisX * axisX + axisY * axisY);
        double lastError = double.NaN, decreasingDistance = 0;
        for (int i = 0; i < s.Count; i++)
        {
            var a = s[i];
            var t = a.Target;
            double elapsed = a.Timestamp - first.Timestamp;
            double fromStart = Distance(a.CursorX - first.CursorX, a.CursorY - first.CursorY);
            if (!moved && fromStart >= MovementThresholdPixels)
            { moved = true; reaction = elapsed * 1000; }
            if (moved && t != null && acquisition == null &&
                Math.Abs(a.CursorX - t.X) <= t.Width / 2 && Math.Abs(a.CursorY - t.Y) <= t.Height / 2)
                acquisition = elapsed * 1000;
            if (a.LeftClick && (i == 0 || !s[i - 1].LeftClick)) click = elapsed * 1000;
            if (i == 0) continue;
            double dt = a.Timestamp - s[i - 1].Timestamp;
            if (dt <= 0) continue;
            double dx = a.CursorX - s[i - 1].CursorX, dy = a.CursorY - s[i - 1].CursorY;
            double step = Distance(dx, dy), velocity = step / dt;
            path += step; peak = Math.Max(peak, velocity);
            double acc = (velocity - previousVelocity) / dt;
            if (i > 1) acceleration = Math.Max(acceleration, Math.Abs(acc));
            if (i > 2) jerk = Math.Max(jerk, Math.Abs((acc - previousAcceleration) / dt));
            previousVelocity = velocity; previousAcceleration = acc;
            if (i > 1 && step > .1)
            {
                double px = s[i - 1].CursorX - s[i - 2].CursorX;
                double py = s[i - 1].CursorY - s[i - 2].CursorY;
                double previousStep = Distance(px, py);
                if (previousStep > .1) curvature += Math.Acos(Math.Clamp((px * dx + py * dy) / (previousStep * step), -1, 1));
            }
            if (t == null) { lastError = double.NaN; continue; }
            double error = Distance(a.CursorX - t.X, a.CursorY - t.Y);
            errorSum += error * dt;
            normalizedErrorSum += error / Math.Max(1, Math.Min(t.Width, t.Height) / 2) * dt;
            errorTime += dt;
            // Correction = sustained return toward the target after moving away, with hysteresis.
            if (double.IsFinite(lastError))
            {
                errorVelocitySquared += Math.Pow((error - lastError) / dt, 2) * dt;
                errorVelocityTime += dt;
                if (error > lastError) { correctionAmplitude += step; decreasingDistance = 0; }
                else
                {
                    decreasingDistance += step;
                    if (correctionAmplitude >= CorrectionThresholdPixels && decreasingDistance >= CorrectionThresholdPixels)
                    { corrections++; correctionTotal += correctionAmplitude; correctionAmplitude = 0; }
                }
            }
            lastError = error;
            // Overshoot along the initial approach axis, beyond the current target's far edge.
            if (axisLength > MovementThresholdPixels)
            {
                double projection = ((a.CursorX - t.X) * axisX + (a.CursorY - t.Y) * axisY) / axisLength;
                double radius = (Math.Abs(axisX) * t.Width + Math.Abs(axisY) * t.Height) / (2 * axisLength);
                if (moved && lastProjection <= radius && projection > radius) passed = true;
                if (passed) overshoot = Math.Max(overshoot, projection - radius);
                lastProjection = projection;
            }
        }
        double duration = s[^1].Timestamp - first.Timestamp;
        double direct = Distance(s[^1].CursorX - first.CursorX, s[^1].CursorY - first.CursorY);
        metrics["ReactionTimeMs"] = reaction;
        metrics["AcquisitionTimeMs"] = acquisition;
        metrics["PeakVelocityPxPerSecond"] = moved ? peak : null;
        metrics["AverageVelocityPxPerSecond"] = moved && duration > 0 ? path / duration : null;
        metrics["PeakAccelerationPxPerSecond2"] = moved && s.Count > 2 ? acceleration : null;
        metrics["PeakJerkPxPerSecond3"] = moved && s.Count > 3 ? jerk : null;
        metrics["PathLengthPx"] = path;
        metrics["PathEfficiency"] = path > 0 ? Math.Clamp(direct / path, 0, 1) : null;
        metrics["PathCurvatureRadiansPerPx"] = path > 0 ? curvature / path : null;
        metrics["OvershootDistancePx"] = moved ? overshoot : null;
        metrics["OvershootRate"] = moved ? (passed ? 1 : 0) : null;
        metrics["CorrectionCount"] = moved ? corrections : null;
        metrics["CorrectionAmplitudePx"] = corrections > 0 ? correctionTotal / corrections : null;
        metrics["ClickDelayMs"] = click.HasValue && acquisition.HasValue && click >= acquisition ? click - acquisition : null;
        metrics["TrackingErrorPx"] = errorTime > 0 ? errorSum / errorTime : null;
        metrics["TrackingErrorTargetRadii"] = errorTime > 0 ? normalizedErrorSum / errorTime : null;
        metrics["TrackingErrorVelocityRmsPxPerSecond"] = errorVelocityTime > 0 ? Math.Sqrt(errorVelocitySquared / errorVelocityTime) : null;
        // Time between targets requires cross-engagement ground truth; leave unavailable in V0.1.
        metrics["TargetSwitchTimeMs"] = null;
        string context = target == null ? "Unknown" : Math.Min(target.Width, target.Height) < 30 ? "SmallTarget" : Math.Min(target.Width, target.Height) < 100 ? "MediumTarget" : "LargeTarget";
        return new(engagement.TargetId, context, engagement.EndReason, engagement.Truncated, metrics);
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}
