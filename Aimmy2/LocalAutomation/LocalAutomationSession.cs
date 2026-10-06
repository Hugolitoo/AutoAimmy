using Aimmy2.Adaptive;
using Aimmy2.AdaptiveControl;
using Aimmy2.AILogic;
using Aimmy2.Class;
using Aimmy2.LocalCapture;
using Aimmy2.ModelLearning;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aimmy2.LocalAutomation;

internal sealed record LocalAutomationState(bool Active, bool Calibrating, bool AssistanceEnabled,
    bool Calibrated, string Message, string Context, int SamplesX = 0, int SamplesY = 0,
    int ProfileCount = 0, double Gain = 0, string? ProfilePath = null, long ComparedWindows = 0, int ValidatedProfiles = 0,
    bool NeedsMeasurement = true);

/// <summary>One local workflow. No network, no game memory, no implicit mouse output on startup.</summary>
internal sealed partial class LocalAutomationSession
{
    public static LocalAutomationSession Instance { get; } = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private AdaptiveAimEngine engine = new();
    private GuidedCalibration? calibration;
    private GuidedCalibration? backgroundCalibration;
    private readonly SceneMotionObserver sceneObserver = new();
    private readonly SceneMotionObserver calibrationSceneObserver = new(320, 180);
    private CancellationTokenSource? calibrationCaptureStop;
    private Task calibrationCaptureTask = Task.CompletedTask;
    private bool fullCalibrationSampler;
    private readonly GameplayEvidenceObserver gameplayEvidence = new();
    private double sceneRawX, sceneRawY, backgroundX = 1000, backgroundY = 1000, lastOutput = -10;
    private int backgroundFits;
    private CalibrationResult? lastBackgroundFit;
    private bool requestedAssistance;
    private bool viewNeedsMeasurement = true;
    private long emergencyEpoch;
    private string visionDetail = "Décor : en attente d’images.";
    private bool active, assistance;
    private double calibrationStart, lastSave;
    private string lastCalibrationDetail = "";
    private int calibrationFrames, calibrationHeldFrames, calibrationWalkingFrames, calibrationRawMissingFrames, calibrationLateFrames;
    private int calibrationSceneFrames, calibrationSceneReliableFrames, calibrationSceneMovingFrames;
    private double calibrationSceneRawTravel;
    private double lastSettingsCheck;
    private Task profileWrite = Task.CompletedTask;
    private double minimumConfidence = .45;
    private long previousRawX, previousRawY;
    private bool rawBaseline;
    private readonly CancellationTokenSource emergencyStop = new();
    private string modelPath = "", profilePath = "", contextKey = "", settingsKey = "";
    private string message = "Chargez un modèle, puis démarrez une session locale.";
    private LocalAutomationState state = new(false, false, false, false,
        "Chargez un modèle, puis démarrez une session locale.", "Aucun");
    public LocalAutomationState State => Volatile.Read(ref state);
    public bool Active => State.Active;
    public double Timestamp => clock.Elapsed.TotalSeconds;
    public double MinimumConfidence => Volatile.Read(ref minimumConfidence);
    public string ModelPath { get { lock (gate) return modelPath; } }
    public bool AssistanceRequested { get { lock (gate) return requestedAssistance; } }
    public long EmergencyEpoch => Interlocked.Read(ref emergencyEpoch);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static bool Key(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool ActivationHeld => InputLogic.InputBindingManager.IsHoldingBinding("Aim Keybind") ||
        InputLogic.InputBindingManager.IsHoldingBinding("Second Aim Keybind");
    internal static CalibrationInput ReadCalibrationInput() => new(ActivationHeld, Key(1),
        Key(0x57) || Key(0x41) || Key(0x53) || Key(0x44) || Key(0x20) || Key(0x51) || Key(0x45), Key(2));
    private LocalAutomationSession() => _ = Task.Run(WatchEmergencyAsync);
    private async Task WatchEmergencyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        bool previouslyHeld = false;
        try
        {
            while (await timer.WaitForNextTickAsync(emergencyStop.Token).ConfigureAwait(false))
            {
                bool held = Key(0x77);
                bool pressed = held && !previouslyHeld;
                previouslyHeld = held;
                lock (gate)
                {
                    if (pressed)
                    {
                        emergencyEpoch++; assistance = requestedAssistance = false; engine.Reset();
                        gameplayEvidence.BreakInterval(); message = "Assistance arrêtée avec F8."; Publish();
                    }
                    // No-frame/black-screen/focus pauses must not leave the UI calibrating forever.
                    if (calibration != null && clock.Elapsed.TotalSeconds - calibrationStart > 45)
                    {
                        FinishCalibrationFailure();
                        Publish();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (emergencyStop.IsCancellationRequested) { }
    }
    public void Shutdown() { emergencyStop.Cancel(); StopAsync().GetAwaiter().GetResult(); }

    public void SetModel(string path)
    {
        lock (gate)
        {
            string next = Path.GetFullPath(path);
            if (string.Equals(next, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            SaveProfile();
            modelPath = next;
            assistance = requestedAssistance = false;
            calibration = null;
            rawBaseline = false;
            LoadProfile();
            message = "Modèle prêt. La session locale enregistre R6 et prépare les exemples sur ce PC.";
            Publish();
        }
    }

    private string SettingsFingerprint()
    {
        // Saved settings affect the measured response. Do not silently reuse calibration after a change.
        var settings = PlayerSessionContext.Load(ObservationMode.DataDirectory).Settings;
        string? accountKey = null;
        string accountPath = Path.Combine(ObservationMode.DataDirectory, "active-profile.json");
        try
        {
            if (File.Exists(accountPath) && new FileInfo(accountPath).Length <= 32768)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(accountPath));
                if (document.RootElement.TryGetProperty("SettingsFileKey", out var key) && key.ValueKind == JsonValueKind.String &&
                    key.GetString() is string value && value.Length == 64 && value.All(Uri.IsHexDigit)) accountKey = value;
            }
        }
        catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException) { }
        string material = JsonSerializer.Serialize(new { DisplayManager.ScreenWidth, DisplayManager.ScreenHeight,
            settings?.HorizontalSensitivity, settings?.VerticalSensitivity, settings?.Fov, settings?.Resolution,
            settings?.AspectRatioSetting, settings?.AdsSensitivityByScope, settings?.MouseSensitivityMultiplier,
            settings?.AdsMouseMultiplier, settings?.AdsUseSpecific, settings?.AdsGlobalSensitivity,
            Method = AimSettings.MouseMovementMethod, Account = accountKey, Model = Path.GetFileName(modelPath) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..24];
    }
    private void LoadProfile()
    {
        contextKey = settingsKey = SettingsFingerprint();
        profilePath = Path.Combine(ObservationMode.DataDirectory, "local-profiles", contextKey + ".json");
        string viewPointer = Path.Combine(ObservationMode.DataDirectory, "local-profiles", settingsKey + "-active-view.txt");
        try
        {
            if (File.Exists(viewPointer))
            {
                string view = File.ReadAllText(viewPointer).Trim();
                if (view.Length == 12 && view.All(Uri.IsHexDigit)) profilePath = Path.Combine(ObservationMode.DataDirectory, "local-profiles", settingsKey + "-view-" + view + ".json");
            }
        }
        catch (IOException) { }
        minimumConfidence = Math.Clamp(new ModelLearningCoordinator(ObservationMode.DataDirectory).TryGetRecommendedConfidence(modelPath)
            ?? (double)AimSettings.MinimumConfidence, .1, .99);
        engine = new AdaptiveAimEngine(new AdaptiveControlOptions { MinimumConfidence = minimumConfidence }, AdaptiveProfileStore.Load(profilePath));
        viewNeedsMeasurement = true;
        gameplayEvidence.Reset(RecoilProfileStore.Load(profilePath + ".recoil.json"));
        ResetSceneCalibration();
    }
    public void ReloadConfiguration() { lock (gate) { if (!active) { LoadProfile(); Publish(); } } }

    public void Start()
    {
        lock (gate)
        {
            if (active) return;
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                throw new InvalidOperationException("Chargez d’abord votre modèle dans l’onglet Modèles.");
            LoadProfile();
            // End the passive report before any generated input can contaminate player-only metrics.
            global::Other.FileManager.AIManager?.Observation?.Dispose();
            LocalCaptureService.Instance.Start(ObservationMode.DataDirectory, PlayerSessionContext.Load(ObservationMode.DataDirectory));
            active = true;
            StartCalibrationCapture();
            assistance = false;
            requestedAssistance = false;
            rawBaseline = false;
            message = "Session locale active. Remettez R6 au premier plan. La souris reste manuelle jusqu’à l’activation de l’assistance.";
            Publish();
        }
    }

    public async Task StopAsync()
    {
        lock (gate)
        {
            active = assistance = false;
            calibrationCaptureStop?.Cancel();
            requestedAssistance = false;
            calibration = null;
            engine.Reset();
            SaveProfile();
            rawBaseline = false;
            message = "Session arrêtée. Enregistrements et profil conservés sur ce PC.";
            Publish();
        }
        await profileWrite.ConfigureAwait(false);
        await calibrationCaptureTask.ConfigureAwait(false);
        await LocalCaptureService.Instance.StopAsync().ConfigureAwait(false);
    }

    public void BeginCalibration()
    {
        Start();
        lock (gate)
        {
            assistance = false;
            requestedAssistance = false;
            engine.Reset();
            viewNeedsMeasurement = true;
            ResetSceneCalibration();
            calibration = new GuidedCalibration(contextKey, DisplayManager.ScreenHeight);
            calibrationStart = clock.Elapsed.TotalSeconds + 5;
            rawBaseline = false;
            lastCalibrationDetail = "";
            calibrationFrames = calibrationHeldFrames = calibrationWalkingFrames = calibrationRawMissingFrames = calibrationLateFrames = 0;
            calibrationSceneFrames = calibrationSceneReliableFrames = calibrationSceneMovingFrames = 0; calibrationSceneRawTravel = 0;
            message = "Dans 5 s : même vue, sans marcher ni tirer. Maintenez la touche de visée. Balayez lentement gauche/droite pendant 15 s, puis haut/bas pendant 15 s ; gardez le décor visible et la cible immobile.";
            Publish();
        }
    }

    public void EnableAssistance(bool enabled)
    {
        lock (gate)
        {
            if (enabled && (!active || calibration != null || engine.Snapshot().Calibration?.IsUsable != true))
                throw new InvalidOperationException("Terminez une calibration valide dans la vue utilisée avant d’activer l’assistance.");
            assistance = enabled && !viewNeedsMeasurement;
            requestedAssistance = enabled;
            engine.Reset();
            message = enabled ? viewNeedsMeasurement
                ? "Activation demandée : mesure de la vue actuelle nécessaire. Maintenez la touche de visée et bougez doucement dans les deux axes, sans tirer ni marcher. F8 annule."
                : "Assistance expérimentale active tant que la touche de visée est maintenue. F8 l’arrête. Recalibrez après changement de DPI, sensibilité ou zoom."
                : "Assistance arrêtée ; observation locale active.";
            Publish();
        }
    }
    public void RequestResumeAfterMeasurement()
    {
        lock (gate)
        {
            if (!active) return;
            requestedAssistance = true;
            assistance = false;
            viewNeedsMeasurement = true;
            ResetSceneCalibration();
            message = "Reprise de l’assistance en attente de mesures cohérentes dans cette vue. F8 annule la reprise.";
            Publish();
        }
    }

    public void PauseFrame(string reason)
    {
        lock (gate)
        {
            if (!active) return;
            assistance = false;
            viewNeedsMeasurement = true;
            engine.Reset(); calibration?.BreakInterval(); rawBaseline = false;
            ResetSceneCalibration();
            message = reason;
            Publish();
        }
    }

    public Prediction? ProcessFrame(Bitmap frame, Rectangle bounds, IReadOnlyList<Prediction> predictions,
        DateTime capturedUtc, double capturedSeconds, R6ForegroundSnapshot foreground, long rawX, long rawY, bool rawAvailable,
        CalibrationInput? capturedInput = null)
    {
        lock (gate)
        {
            if (!active) return null;
            if (!R6ForegroundGuard.StillMatches(foreground)) { PauseFrame("En pause : R6 doit rester au premier plan."); return null; }
            double now = capturedSeconds;
            double age = clock.Elapsed.TotalSeconds - now;
            bool freshForOutput = age <= .15;
            if (calibration != null)
            {
                calibrationFrames++;
                if (capturedInput?.ActivationHeld == true) calibrationHeldFrames++;
                if (capturedInput?.Walking == true) calibrationWalkingFrames++;
                if (!rawAvailable) calibrationRawMissingFrames++;
                if (!freshForOutput) calibrationLateFrames++;
            }
            // Passive matched image/input measurements have a separate bound. Live mouse
            // output retains its original 150 ms cutoff below and in the final output guard.
            if (age > GuidedCalibration.MaximumObservationGapSeconds)
            {
                if (fullCalibrationSampler)
                { assistance = false; engine.Reset(); rawBaseline = false; message = "Détection lente : calibration sur le décor en cours, assistance suspendue."; Publish(); }
                else PauseFrame("Images trop anciennes pour mesurer la caméra. Réduisez la charge du PC.");
                return null;
            }
            if (now - lastSettingsCheck >= 1)
            {
                lastSettingsCheck = now;
                if (SettingsFingerprint() != settingsKey)
                {
                    SaveProfile(); assistance = requestedAssistance = false; calibration = null; LoadProfile(); rawBaseline = false;
                    message = "Réglages modifiés : profil correspondant chargé. Vérifiez la calibration avant de réactiver l’assistance.";
                    Publish(); return null;
                }
            }
            long dx = rawAvailable && rawBaseline ? rawX - previousRawX : 0;
            long dy = rawAvailable && rawBaseline ? rawY - previousRawY : 0;
            previousRawX = rawX; previousRawY = rawY;
            rawBaseline = rawAvailable;
            bool held = ActivationHeld;
            var measuredInput = capturedInput ?? ReadCalibrationInput();
            if (Key(0x77)) { assistance = requestedAssistance = false; message = "Assistance arrêtée avec F8."; }
            var detections = predictions.Select((p, i) => new DetectionSample(i, p.ScreenCenterX, p.ScreenCenterY,
                p.Rectangle.Width, p.Rectangle.Height, p.Confidence, p.ClassId)).ToArray();
            sceneRawX += dx; sceneRawY += dy;
            var scene = sceneObserver.Observe(frame, bounds, detections, now);
            if (sceneObserver.Blank)
            { PauseFrame("Image de jeu noire : calibration et correction en pause. Vérifiez le mode de capture et l’affichage de R6."); return null; }
            visionDetail = scene.Reliable ? $"Décor suivi : {scene.Confidence:P0} · caméra {scene.X:0.0}/{scene.Y:0.0} px · échelle {scene.Scale:0.000}." : "Décor insuffisant : mouvement des cibles non séparé de la caméra.";
            if (sceneObserver.Updated)
            {
                bool walking = measuredInput.Walking;
                gameplayEvidence.Observe(capturedUtc, scene, sceneRawY, engine.Calibration,
                    rawAvailable && held && calibration == null && now - lastOutput > .25,
                    walking, Key(1), DisplayManager.ScreenHeight, sceneObserver.Interval);
                if (scene.Reliable && Math.Abs(scene.Scale - 1) > .035)
                {
                    assistance = false; viewNeedsMeasurement = true; engine.Reset(); backgroundCalibration = null; backgroundFits = 0;
                    message = "Changement de vue probable : assistance suspendue pendant la mesure de la nouvelle réponse.";
                }
                if (!fullCalibrationSampler && (calibration == null || now >= calibrationStart) && scene.Reliable && Math.Abs(scene.Scale - 1) < .02 &&
                    rawAvailable && measuredInput.ActivationHeld && !measuredInput.Firing && !walking && now - lastOutput > .25)
                {
                    backgroundCalibration ??= new GuidedCalibration(contextKey, DisplayManager.ScreenHeight);
                    backgroundX += scene.X; backgroundY += scene.Y;
                    var fit = backgroundCalibration.Observe(new CalibrationObservation(now, 1, backgroundX, backgroundY,
                        sceneRawX, sceneRawY, true, false, 100));
                    if (fit.IsUsable)
                    {
                        bool agrees = lastBackgroundFit != null && Math.Abs(fit.PixelsPerCountX / lastBackgroundFit.PixelsPerCountX - 1) < .15 &&
                            Math.Abs(fit.PixelsPerCountY / lastBackgroundFit.PixelsPerCountY - 1) < .15;
                        backgroundFits = agrees ? backgroundFits + 1 : 1;
                        lastBackgroundFit = fit;
                        var completedProgress = backgroundCalibration.Progress;
                        backgroundCalibration = null;
                        if (calibration != null || backgroundFits >= 2)
                        {
                            if (calibration != null) RecordCalibrationAttempt("Accepted", completedProgress, "Décor");
                            ApplyCameraResponse(fit);
                            calibration = null;
                            assistance = requestedAssistance;
                            message = "Réponse de caméra mesurée sur le décor ; profil de cette vue chargé automatiquement.";
                        }
                    }
                }
                else if (!fullCalibrationSampler) backgroundCalibration?.BreakInterval();
                sceneRawX = sceneRawY = 0;
            }
            double interval = sceneObserver.Interval;
            var decision = engine.Update(new AdaptiveFrame(now, DisplayManager.ScreenLeft + DisplayManager.ScreenWidth / 2.0,
                DisplayManager.ScreenTop + DisplayManager.ScreenHeight / 2.0, DisplayManager.ScreenHeight, detections,
                held, freshForOutput && assistance && !viewNeedsMeasurement && calibration == null && rawAvailable, contextKey,
                scene.Reliable && interval > 0 ? scene.X / interval : 0,
                scene.Reliable && interval > 0 ? scene.Y / interval : 0, scene.Reliable,
                gameplayEvidence.RecoilPixelsPerSecond(capturedUtc, DisplayManager.ScreenHeight, Key(1))));
            int generatedX = 0, generatedY = 0;
            DateTime? generatedUtc = null;
            if (calibration != null)
            {
                if (now < calibrationStart) message = $"Calibration dans {Math.Ceiling(calibrationStart - now):0} s. Placez-vous devant une cible immobile.";
                else if (now - calibrationStart > 45)
                {
                    FinishCalibrationFailure();
                }
                else if (fullCalibrationSampler)
                    message = backgroundCalibration != null ? CalibrationProgressMessage() :
                        "Calibration : gardez le décor visible autour de l’arme. Maintenez la touche de visée, sans tirer ni marcher.";
                else if (measuredInput.ActivationHeld && !measuredInput.Firing && !measuredInput.Walking && rawAvailable && decision.Target is { } target)
                {
                    var result = calibration.Observe(new CalibrationObservation(now, target.Id, target.Detection.X,
                        target.Detection.Y, dx, dy, true, false, target.Detection.Height));
                    if (result.IsUsable)
                    {
                        RecordCalibrationAttempt("Accepted", calibration.Progress, "Cible");
                        ApplyCameraResponse(result);
                        calibration = null;
                        SaveProfile();
                        message = "Calibration enregistrée pour cette vue. Revenez dans AUTO pour activer l’assistance expérimentale.";
                    }
                    else message = CalibrationProgressMessage();
                }
                else
                {
                    calibration.BreakInterval();
                    message = !rawAvailable ? "Souris brute indisponible : calibration suspendue." : measuredInput.Walking ?
                        "Restez sur place : les mouvements du personnage ne servent pas à la calibration." :
                        backgroundCalibration != null ? CalibrationProgressMessage() : "Gardez le décor visible, maintenez la touche de visée, sans tirer ni marcher.";
                }
            }
            else if (assistance)
            {
                message = freshForOutput ? "Assistance : " + TranslateStatus(decision.Status) + ". F8 pour arrêter." :
                    "Assistance en pause : calcul trop lent pour corriger cette image. F8 pour arrêter.";
                if (decision.HasCorrection && held && clock.Elapsed.TotalSeconds - now <= .15 && R6ForegroundGuard.StillMatches(foreground) && !Key(0x77))
                {
                    if (!LocalMouseOutput.TryMove(decision.CountsX, decision.CountsY, foreground, out string? error))
                    {
                        assistance = requestedAssistance = false;
                        message = "Assistance arrêtée : " + error;
                    }
                    else { lastOutput = now; generatedX = decision.CountsX; generatedY = decision.CountsY; generatedUtc = DateTime.UtcNow; }
                }
            }
            if (now - lastSave >= 15) { SaveProfile(); lastSave = now; }
            LocalCaptureService.Instance.PublishInput(new LocalInputSample(capturedUtc, rawAvailable ? dx : null,
                rawAvailable ? dy : null, measuredInput.Firing, measuredInput.RightHeld, assistance,
                sceneObserver.Updated && scene.Reliable ? scene.X : null, sceneObserver.Updated && scene.Reliable ? scene.Y : null,
                scene.Reliable ? scene.Confidence : null, scene.Reliable ? scene.Scale : null,
                sceneObserver.Updated && scene.Reliable ? interval : null, generatedX, generatedY, generatedUtc));
            LocalCaptureService.Instance.PublishDetections(frame, bounds, predictions.Select(p => new LocalDetectionBox(
                p.Rectangle.X, p.Rectangle.Y, p.Rectangle.Width, p.Rectangle.Height, p.Confidence, p.ClassId)).ToArray(),
                capturedUtc, Path.GetFileName(modelPath), foreground);
            Publish(decision);
            return decision.TargetSourceIndex is int index && index >= 0 && index < predictions.Count ? predictions[index] : null;
        }
    }

    public string VisionDetail => visionDetail;
    public string CalibrationDetail => Volatile.Read(ref lastCalibrationDetail);
    public string GameplayDetail { get { lock (gate) return gameplayEvidence.Detail; } }
    public long EstimatedShots { get { lock (gate) return gameplayEvidence.EstimatedShots; } }
    private (CalibrationProgress? Progress, string Source) BestCalibrationProgress()
    {
        var target = calibration?.Progress;
        var scene = backgroundCalibration?.Progress;
        double Score(CalibrationProgress p) => (p.X.Complete ? 10000 : 0) + (p.Y.Complete ? 10000 : 0) +
            p.X.CleanSamples + p.Y.CleanSamples + 20 * (p.X.Fit + p.Y.Fit);
        return scene != null && (target == null || Score(scene) > Score(target)) ? (scene, "Décor") : (target, "Cible");
    }
    private static string AxisCalibrationMessage(CalibrationAxisProgress axis, bool horizontal)
    {
        string directions = horizontal ? "gauche/droite" : "haut/bas";
        return axis.MissingReason switch
        {
            "Ready" => "mesures cohérentes",
            "SamplesRequired" => $"continuez lentement {directions}, avec des balayages un peu plus amples",
            "BothDirectionsRequired" => $"il manque un sens : balayez {directions} dans les deux sens",
            "MoreMovementRequired" => "amplitude totale trop faible : faites des balayages un peu plus amples",
            "TooManyOutliers" => "trop de mesures incohérentes : gardez la même vue et une cible immobile",
            "InconsistentMotion" => axis.RelativeSpread > .25 ? $"dispersion {axis.RelativeSpread:P0} (maximum 25 %) : mouvement mesuré irrégulier" :
                $"accord image/souris {axis.Fit:P0} (minimum 88 %) : mesure instable",
            "InvalidGain" => "réponse de caméra hors plage : mesure à vérifier",
            _ => "mesure en attente"
        };
    }
    private string CalibrationProgressMessage()
    {
        var (progress, source) = BestCalibrationProgress();
        return progress == null ? "Calibration : en attente d’images et de mouvements." :
            $"Calibration ({source.ToLowerInvariant()}) · H {Math.Min(12, progress.X.CleanSamples)}/12 · V {Math.Min(12, progress.Y.CleanSamples)}/12.\n" +
            "Horizontal : " + AxisCalibrationMessage(progress.X, true) + ".\nVertical : " + AxisCalibrationMessage(progress.Y, false) + ".";
    }
    private void FinishCalibrationFailure()
    {
        var (progress, source) = BestCalibrationProgress();
        RecordCalibrationAttempt("Rejected", progress, source);
        calibration = null; engine.Reset(); ResetSceneCalibration();
        message = lastCalibrationDetail;
    }
    private void RecordCalibrationAttempt(string outcome, CalibrationProgress? progress, string source)
    {
        string detail = outcome == "Accepted" ? "Dernier essai : calibration validée sur " + source.ToLowerInvariant() + "." :
            calibrationFrames == 0 ? "Dernier essai : aucune image reçue. Vérifiez R6 au premier plan et le moniteur sélectionné." :
            calibrationRawMissingFrames == calibrationFrames ? "Dernier essai : souris brute indisponible." :
            calibrationHeldFrames == 0 ? "Dernier essai : touche d’activation Aimmy non maintenue. Vérifiez Aim Keybind." :
            calibrationSceneFrames > 0 && calibrationSceneReliableFrames == 0 ?
                "Dernier essai : le décor autour de l’arme n’a pas pu être suivi. Balayez plus lentement devant un décor texturé, dans la même vue." :
            calibrationSceneFrames > 0 && calibrationSceneMovingFrames == 0 && calibrationSceneRawTravel >= 160 ?
                "Dernier essai : mouvements de souris reçus, mais aucun déplacement exploitable du décor. Gardez R6 au premier plan et faites des balayages lents un peu plus amples." :
            progress == null ? "Dernier essai : aucune mesure exploitable. Gardez le décor visible, sans tirer ni marcher." :
            "Dernier essai non validé (" + source.ToLowerInvariant() + "). Horizontal : " + AxisCalibrationMessage(progress.X, true) +
                ". Vertical : " + AxisCalibrationMessage(progress.Y, false) + ".";
        lastCalibrationDetail = detail;
        try
        {
            string directory = Path.Combine(ObservationMode.DataDirectory, "local-profiles", "calibration-diagnostics");
            Directory.CreateDirectory(directory);
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json";
            File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new { Schema = 1, Utc = DateTime.UtcNow,
                Outcome = outcome, Source = source, Detail = detail, Progress = progress,
                Frames = calibrationFrames, ActivationHeldFrames = calibrationHeldFrames, WalkingFrames = calibrationWalkingFrames,
                RawUnavailableFrames = calibrationRawMissingFrames, FramesTooOldForOutput = calibrationLateFrames,
                WideSceneFrames = calibrationSceneFrames, ReliableWideSceneFrames = calibrationSceneReliableFrames,
                MovingWideSceneFrames = calibrationSceneMovingFrames, WideSceneRawTravel = calibrationSceneRawTravel,
                CalibrationRegion = "WideGameOutsideWeaponAndHUD",
                MouseOutputFreshnessMilliseconds = 150 }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { lastCalibrationDetail += " Diagnostic non sauvegardé : " + error.Message; }
    }
    private void ResetSceneCalibration()
    {
        sceneObserver.Reset(); backgroundCalibration = null; sceneRawX = sceneRawY = 0;
        calibrationSceneObserver.Reset();
        backgroundX = backgroundY = 1000; backgroundFits = 0; lastBackgroundFit = null;
        gameplayEvidence.BreakInterval();
    }
    private void ApplyCameraResponse(CalibrationResult result)
    {
        string material = Math.Sign(result.PixelsPerCountX) + "/" + Math.Sign(result.PixelsPerCountY) + "/" + Math.Round(Math.Log(Math.Abs(result.PixelsPerCountX)), 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
            Math.Round(Math.Log(Math.Abs(result.PixelsPerCountY)), 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string view = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..12];
        string nextPath = Path.Combine(ObservationMode.DataDirectory, "local-profiles", settingsKey + "-view-" + view + ".json");
        if (profilePath != nextPath)
        {
            SaveProfile();
            profilePath = nextPath;
            engine = new AdaptiveAimEngine(new AdaptiveControlOptions { MinimumConfidence = minimumConfidence }, AdaptiveProfileStore.Load(profilePath));
            gameplayEvidence.Reset(RecoilProfileStore.Load(profilePath + ".recoil.json"));
        }
        engine.SetCalibration(result);
        viewNeedsMeasurement = false;
        SaveProfile();
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(profilePath)!, settingsKey + "-active-view.txt"), view);
    }

    private static string TranslateStatus(string status) => status switch
    {
        "CalibratedAssistance" => "correction calibrée", "ObservationOnly" => "observation", "WaitingForActivation" => "touche de visée relâchée",
        "CalibrationRequiredForThisView" => "calibration nécessaire", "NoCurrentTarget" => "aucune cible", "ConfirmingTarget" => "confirmation de la cible",
        "CameraGainChangedRecalibrate" => "réponse de caméra modifiée : recalibrez", "InvalidFrame" => "image invalide", _ => status
    };
    private void SaveProfile()
    {
        if (string.IsNullOrEmpty(profilePath)) return;
        string path = profilePath;
        var snapshot = engine.Snapshot(contextKey);
        var recoilSnapshot = gameplayEvidence.Recoil.Snapshot();
        Task previous = profileWrite;
        profileWrite = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { AdaptiveProfileStore.Save(path, snapshot); RecoilProfileStore.Save(path + ".recoil.json", recoilSnapshot); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { lock (gate) { message = "Profil non sauvegardé : " + error.Message; assistance = false; Publish(); } }
        });
    }
    private void Publish(AdaptiveDecision? decision = null)
    {
        var profile = engine.Snapshot(contextKey);
        Volatile.Write(ref state, new(active, calibration != null, assistance, profile.Calibration?.IsUsable == true,
            message, decision?.ContextKey ?? "En attente", calibration?.SamplesX ?? 0, calibration?.SamplesY ?? 0,
            profile.Profiles.Length, decision?.Gain ?? 0, profilePath,
            profile.Profiles.Sum(p => p.ComparedWindows), profile.Profiles.Count(p => p.AdjustmentEvidence == "MeasuredRandomizedTrackingWindows"),
            viewNeedsMeasurement));
    }
}
