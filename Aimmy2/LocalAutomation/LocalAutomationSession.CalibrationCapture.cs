using Aimmy2.Adaptive;
using Aimmy2.AdaptiveControl;
using Aimmy2.Class;
using Aimmy2.LocalCapture;
using System.Drawing;

namespace Aimmy2.LocalAutomation;

internal sealed partial class LocalAutomationSession
{
    private void StartCalibrationCapture()
    {
        calibrationCaptureStop?.Cancel();
        var owner = new CancellationTokenSource();
        calibrationCaptureStop = owner;
        fullCalibrationSampler = true;
        calibrationCaptureTask = Task.Run(async () =>
        {
            long lastRawX = 0, lastRawY = 0; bool baseline = false;
            try
            {
                using var capture = new global::AILogic.CaptureManager();
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await timer.WaitForNextTickAsync(owner.Token).ConfigureAwait(false))
                {
                    lock (gate)
                    {
                        if (!active) break;
                        if ((calibration == null && !viewNeedsMeasurement && assistance) ||
                            calibration != null && clock.Elapsed.TotalSeconds < calibrationStart)
                        { baseline = false; calibrationSceneObserver.Reset(); backgroundCalibration?.BreakInterval(); continue; }
                    }
                    if (!R6ForegroundGuard.TryGet(out var window))
                    { baseline = false; lock (gate) { calibrationSceneObserver.Reset(); backgroundCalibration?.BreakInterval(); } continue; }
                    var monitor = new Rectangle(DisplayManager.ScreenLeft, DisplayManager.ScreenTop, DisplayManager.ScreenWidth, DisplayManager.ScreenHeight);
                    var bounds = Rectangle.Intersect(window.ClientBounds, monitor);
                    if (bounds.Width < 640 || bounds.Height < 360) continue;
                    using var frame = capture.ScreenGrab(bounds, requireFresh: true);
                    if (frame == null || !R6ForegroundGuard.StillMatches(window)) continue;
                    double at = clock.Elapsed.TotalSeconds;
                    bool rawAvailable = RawMouseObserver.Shared.TryRead(out long rawX, out long rawY, out _);
                    var input = ReadCalibrationInput();
                    double dx = baseline && rawAvailable ? rawX - lastRawX : 0;
                    double dy = baseline && rawAvailable ? rawY - lastRawY : 0;
                    lastRawX = rawX; lastRawY = rawY; baseline = rawAvailable;
                    lock (gate)
                    {
                        if (!active || owner.IsCancellationRequested) break;
                        var motion = calibrationSceneObserver.Observe(frame, bounds, Array.Empty<DetectionSample>(), at,
                            CalibrationSceneMasks.ForBounds(bounds));
                        if (calibration != null)
                        {
                            calibrationSceneFrames++; calibrationSceneRawTravel += Math.Abs(dx) + Math.Abs(dy);
                            if (motion.Reliable) calibrationSceneReliableFrames++;
                            if (motion.Reliable && Math.Abs(motion.X) + Math.Abs(motion.Y) >= 2) calibrationSceneMovingFrames++;
                        }
                        visionDetail = motion.Reliable ? $"Calibration sur le décor autour de l’arme : {motion.Confidence:P0}." :
                            "Calibration : décor autour de l’arme insuffisant ou mouvement trop rapide.";
                        if (!rawAvailable || !input.ActivationHeld || input.Firing || input.Walking || !motion.Reliable ||
                            Math.Abs(motion.Scale - 1) >= .02 || at - lastOutput <= .25)
                        { backgroundCalibration?.BreakInterval(); continue; }
                        backgroundCalibration ??= new GuidedCalibration(contextKey, DisplayManager.ScreenHeight);
                        backgroundX += motion.X; backgroundY += motion.Y;
                        var fit = backgroundCalibration.Observe(new(at, 1, backgroundX, backgroundY, dx, dy, true, false, 100));
                        if (!fit.IsUsable) { if (calibration != null) { message = CalibrationProgressMessage(); Publish(); } continue; }
                        bool agrees = lastBackgroundFit != null && Math.Abs(fit.PixelsPerCountX / lastBackgroundFit.PixelsPerCountX - 1) < .15 &&
                            Math.Abs(fit.PixelsPerCountY / lastBackgroundFit.PixelsPerCountY - 1) < .15;
                        backgroundFits = agrees ? backgroundFits + 1 : 1; lastBackgroundFit = fit;
                        var progress = backgroundCalibration.Progress; backgroundCalibration = null;
                        if (calibration != null || backgroundFits >= 2)
                        {
                            if (calibration != null) RecordCalibrationAttempt("Accepted", progress, "Décor autour de l’arme");
                            ApplyCameraResponse(fit); calibration = null; assistance = requestedAssistance;
                            message = "Calibration enregistrée sur le décor autour de l’arme. Revenez dans AUTO pour activer l’assistance.";
                            Publish();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            catch (Exception error)
            {
                lock (gate)
                { visionDetail = "Capture du décor indisponible : " + error.Message; fullCalibrationSampler = false; }
            }
            finally
            {
                lock (gate) { if (ReferenceEquals(calibrationCaptureStop, owner)) { fullCalibrationSampler = false; calibrationCaptureStop = null; } }
                owner.Dispose();
            }
        });
    }
}
