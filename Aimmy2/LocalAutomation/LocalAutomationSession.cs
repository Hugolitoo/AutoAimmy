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
internal sealed class LocalAutomationSession
{
    public static LocalAutomationSession Instance { get; } = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private AdaptiveAimEngine engine = new();
    private GuidedCalibration? calibration;
    private GuidedCalibration? backgroundCalibration;
    private readonly SceneMotionObserver sceneObserver = new();
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
                        calibration = null; engine.Reset(); ResetSceneCalibration();
                        message = "Calibration terminée sans assez de mesures. Revenez sur R6, puis recommencez sans tirer ni déplacer le personnage.";
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
            requestedAssistance = false;
            calibration = null;
            engine.Reset();
            SaveProfile();
            rawBaseline = false;
            message = "Session arrêtée. Enregistrements et profil conservés sur ce PC.";
            Publish();
        }
        await profileWrite.ConfigureAwait(false);
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
            calibration = new GuidedCalibration(contextKey, DisplayManager.ScreenHeight);
            calibrationStart = clock.Elapsed.TotalSeconds + 5;
            rawBaseline = false;
            message = "Dans 5 s : cible immobile, restez sur place, sans tirer. Maintenez votre touche de visée et faites de petits mouvements gauche/droite puis haut/bas, dans les deux sens.";
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
        DateTime capturedUtc, double capturedSeconds, R6ForegroundSnapshot foreground, long rawX, long rawY, bool rawAvailable)
    {
        lock (gate)
        {
            if (!active) return null;
            if (!R6ForegroundGuard.StillMatches(foreground)) { PauseFrame("En pause : R6 doit rester au premier plan."); return null; }
            double now = capturedSeconds;
            if (clock.Elapsed.TotalSeconds - now > .15) { PauseFrame("Images trop anciennes pour corriger la visée. Réduisez la charge du PC."); return null; }
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
                bool walking = Key(0x57) || Key(0x41) || Key(0x53) || Key(0x44) || Key(0x20) || Key(0x51) || Key(0x45);
                gameplayEvidence.Observe(capturedUtc, scene, sceneRawY, engine.Calibration,
                    rawAvailable && held && calibration == null && now - lastOutput > .25,
                    walking, Key(1), DisplayManager.ScreenHeight, sceneObserver.Interval);
                if (scene.Reliable && Math.Abs(scene.Scale - 1) > .035)
                {
                    assistance = false; viewNeedsMeasurement = true; engine.Reset(); backgroundCalibration = null; backgroundFits = 0;
                    message = "Changement de vue probable : assistance suspendue pendant la mesure de la nouvelle réponse.";
                }
                if (scene.Reliable && Math.Abs(scene.Scale - 1) < .02 && rawAvailable && held && !Key(1) && !walking && now - lastOutput > .25)
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
                        backgroundCalibration = null;
                        if (calibration != null || backgroundFits >= 2)
                        {
                            ApplyCameraResponse(fit);
                            calibration = null;
                            assistance = requestedAssistance;
                            message = "Réponse de caméra mesurée sur le décor ; profil de cette vue chargé automatiquement.";
                        }
                    }
                }
                else backgroundCalibration?.BreakInterval();
                sceneRawX = sceneRawY = 0;
            }
            double interval = sceneObserver.Interval;
            var decision = engine.Update(new AdaptiveFrame(now, DisplayManager.ScreenLeft + DisplayManager.ScreenWidth / 2.0,
                DisplayManager.ScreenTop + DisplayManager.ScreenHeight / 2.0, DisplayManager.ScreenHeight, detections,
                held, assistance && !viewNeedsMeasurement && calibration == null && rawAvailable, contextKey,
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
                    message = "Calibration insuffisante. Recommencez sur une cible immobile, dans la même vue, sans déplacer le personnage ni tirer.";
                    calibration = null;
                }
                else if (held && !Key(1) && rawAvailable && decision.Target is { } target)
                {
                    var result = calibration.Observe(new CalibrationObservation(now, target.Id, target.Detection.X,
                        target.Detection.Y, dx, dy, true, false, target.Detection.Height));
                    if (result.IsUsable)
                    {
                        ApplyCameraResponse(result);
                        calibration = null;
                        SaveProfile();
                        message = "Calibration enregistrée pour cette vue. Revenez dans AUTO pour activer l’assistance expérimentale.";
                    }
                    else message = $"Calibration : horizontal {calibration.SamplesX}/12 · vertical {calibration.SamplesY}/12. Petits mouvements dans les deux sens, sans tirer ni marcher.";
                }
                else
                {
                    calibration.BreakInterval();
                    message = !rawAvailable ? "Souris brute indisponible : calibration suspendue." : "Gardez une cible immobile détectée, maintenez la touche de visée, sans tirer.";
                }
            }
            else if (assistance)
            {
                message = "Assistance : " + TranslateStatus(decision.Status) + ". F8 pour arrêter.";
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
                rawAvailable ? dy : null, Key(1), Key(2), assistance,
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
    public string GameplayDetail { get { lock (gate) return gameplayEvidence.Detail; } }
    public long EstimatedShots { get { lock (gate) return gameplayEvidence.EstimatedShots; } }
    private void ResetSceneCalibration()
    {
        sceneObserver.Reset(); backgroundCalibration = null; sceneRawX = sceneRawY = 0;
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
