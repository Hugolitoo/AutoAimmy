using Aimmy2.Adaptive;
using Aimmy2.AdaptiveControl;

namespace Aimmy2.LocalAutomation;

internal sealed class GameplayEvidenceObserver
{
    private sealed record Motion(DateTime At, double ResidualY, double Seconds, bool Clean);
    private readonly Queue<Motion> motions = new();
    private VisualHudObservation? previousAmmo;
    private DateTime consumedAmmo, rateAt;
    private double estimatedShotRate;
    private int coherentReserve;
    public RecoilEstimator Recoil { get; private set; } = new();
    public long EstimatedShots { get; private set; }
    public void Reset(RecoilState? state = null)
    { BreakInterval(); Recoil = new(state); }
    public void BreakInterval()
    { motions.Clear(); previousAmmo = null; coherentReserve = 0; consumedAmmo = default; rateAt = default; estimatedShotRate = 0; }
    public void Observe(DateTime at, SceneMotion motion, double rawY, CalibrationResult? calibration,
        bool independent, bool walking, bool leftHeld, double screenHeight, double seconds)
    {
        bool measurable = calibration?.IsUsable == true && motion.Reliable && double.IsFinite(rawY) &&
            double.IsFinite(screenHeight) && screenHeight >= 200 && double.IsFinite(seconds) && seconds is > 0 and <= .2;
        motions.Enqueue(new(at, measurable ? (motion.Y - rawY * calibration!.PixelsPerCountY) / screenHeight : 0,
            measurable ? seconds : 0, measurable && independent && !walking && leftHeld && Math.Abs(motion.Scale - 1) < .02));
        while (motions.TryPeek(out var first) && at - first.At > TimeSpan.FromSeconds(3)) motions.Dequeue();
        var ammo = Aimmy2.VisualAnalysis.LiveHudObserver.Instance.Latest;
        if (ammo.Status != "ReadingHUD" || !ammo.FrameUtc.HasValue || ammo.FrameUtc.Value <= consumedAmmo ||
            ammo.FrameUtc.Value > at || at - ammo.FrameUtc.Value > TimeSpan.FromSeconds(2) ||
            !ammo.AmmoMagazine.HasValue || !ammo.AmmoReserve.HasValue) return;
        consumedAmmo = ammo.FrameUtc.Value;
        bool weaponChanged = previousAmmo?.WeaponText is string beforeWeapon && ammo.WeaponText is string currentWeapon && beforeWeapon != currentWeapon;
        if (weaponChanged) { Recoil = new(); motions.Clear(); }
        if (previousAmmo?.AmmoReserve == ammo.AmmoReserve && !weaponChanged) coherentReserve++;
        else { coherentReserve = 0; estimatedShotRate = 0; }
        if (previousAmmo?.FrameUtc is DateTime before && coherentReserve >= 1)
        {
            double interval = (ammo.FrameUtc.Value - before).TotalSeconds;
            int decrease = previousAmmo.AmmoMagazine!.Value - ammo.AmmoMagazine.Value;
            if (leftHeld && interval is >= .4 and <= 2 && decrease is >= 1 and <= 20)
            {
                EstimatedShots += decrease; estimatedShotRate = decrease / interval; rateAt = ammo.FrameUtc.Value;
                var window = motions.Where(m => m.At > before && m.At <= ammo.FrameUtc.Value).ToArray();
                if (window.Length >= 3 && window.All(m => m.Clean) && window.Sum(m => m.Seconds) >= interval * .75)
                    Recoil.Observe(decrease, window.Sum(m => m.ResidualY), true, true);
            }
            else estimatedShotRate = 0;
        }
        if (ammo.AmmoMagazine == 0) estimatedShotRate = 0;
        previousAmmo = ammo;
    }
    public double RecoilPixelsPerSecond(DateTime at, double height, bool leftHeld) =>
        leftHeld && Recoil.Reliable && double.IsFinite(height) && height >= 200 && at >= rateAt &&
        at - rateAt < TimeSpan.FromSeconds(1.2) ? Math.Clamp(Recoil.MeanKick * height * estimatedShotRate, 0, height * .2) : 0;
    public string Detail => $"Tirs estimés par le HUD : {EstimatedShots} · recul : {Recoil.Windows}/6 fenêtres propres" +
        (Recoil.Reliable ? " · estimation cohérente pour cette vue." : " · compensation spécifique en attente de mesures fiables.");
}
