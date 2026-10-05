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
    int ProfileCount = 0, double Gain = 0, string? ProfilePath = null);

/// <summary>One local workflow. No network, no game memory, no implicit mouse output on startup.</summary>
internal sealed class LocalAutomationSession
{
    public static LocalAutomationSession Instance { get; } = new();
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private AdaptiveAimEngine engine = new();
    private GuidedCalibration? calibration;
    private bool active, assistance;
    private double calibrationStart, lastSave;
    private double lastSettingsCheck;
    private Task profileWrite = Task.CompletedTask;
    private double minimumConfidence = .45;
    private long previousRawX, previousRawY;
    private bool rawBaseline;
    private readonly CancellationTokenSource emergencyStop = new();
    private string modelPath = "", profilePath = "", contextKey = "";
    private string message = "Chargez un modèle, puis démarrez une session locale.";
    private LocalAutomationState state = new(false, false, false, false,
        "Chargez un modèle, puis démarrez une session locale.", "Aucun");
    public LocalAutomationState State => Volatile.Read(ref state);
    public bool Active => State.Active;
    public double Timestamp => clock.Elapsed.TotalSeconds;
    public double MinimumConfidence => Volatile.Read(ref minimumConfidence);
    public string ModelPath { get { lock (gate) return modelPath; } }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static bool Key(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool ActivationHeld => InputLogic.InputBindingManager.IsHoldingBinding("Aim Keybind") ||
        InputLogic.InputBindingManager.IsHoldingBinding("Second Aim Keybind");
    private LocalAutomationSession() => _ = Task.Run(WatchEmergencyAsync);
    private async Task WatchEmergencyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(emergencyStop.Token).ConfigureAwait(false))
            {
                if (!State.AssistanceEnabled || !Key(0x77)) continue;
                lock (gate) { assistance = false; engine.Reset(); message = "Assistance arrêtée avec F8."; Publish(); }
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
            assistance = false;
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
        contextKey = SettingsFingerprint();
        profilePath = Path.Combine(ObservationMode.DataDirectory, "local-profiles", contextKey + ".json");
        minimumConfidence = Math.Clamp(new ModelLearningCoordinator(ObservationMode.DataDirectory).TryGetRecommendedConfidence(modelPath)
            ?? (double)AimSettings.MinimumConfidence, .1, .99);
        engine = new AdaptiveAimEngine(new AdaptiveControlOptions { MinimumConfidence = minimumConfidence }, AdaptiveProfileStore.Load(profilePath));
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
            engine.Reset();
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
            assistance = enabled;
            engine.Reset();
            message = enabled ? "Assistance expérimentale active tant que la touche de visée est maintenue. F8 l’arrête. Recalibrez après changement de DPI, sensibilité ou zoom." : "Assistance arrêtée ; observation locale active.";
            Publish();
        }
    }

    public void PauseFrame(string reason)
    {
        lock (gate)
        {
            if (!active) return;
            engine.Reset(); calibration?.BreakInterval(); rawBaseline = false;
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
                if (SettingsFingerprint() != contextKey)
                {
                    SaveProfile(); assistance = false; calibration = null; LoadProfile(); rawBaseline = false;
                    message = "Réglages modifiés : profil correspondant chargé. Vérifiez la calibration avant de réactiver l’assistance.";
                    Publish(); return null;
                }
            }
            long dx = rawAvailable && rawBaseline ? rawX - previousRawX : 0;
            long dy = rawAvailable && rawBaseline ? rawY - previousRawY : 0;
            previousRawX = rawX; previousRawY = rawY;
            rawBaseline = rawAvailable;
            bool held = ActivationHeld;
            if (Key(0x77)) { assistance = false; message = "Assistance arrêtée avec F8."; }
            LocalCaptureService.Instance.PublishInput(new LocalInputSample(capturedUtc, rawAvailable ? dx : null,
                rawAvailable ? dy : null, Key(1), Key(2), assistance));
            LocalCaptureService.Instance.PublishDetections(frame, bounds, predictions.Select(p => new LocalDetectionBox(
                p.Rectangle.X, p.Rectangle.Y, p.Rectangle.Width, p.Rectangle.Height, p.Confidence, p.ClassId)).ToArray(),
                capturedUtc, Path.GetFileName(modelPath), foreground);
            var detections = predictions.Select((p, i) => new DetectionSample(i, p.ScreenCenterX, p.ScreenCenterY,
                p.Rectangle.Width, p.Rectangle.Height, p.Confidence, p.ClassId)).ToArray();
            var decision = engine.Update(new AdaptiveFrame(now, DisplayManager.ScreenLeft + DisplayManager.ScreenWidth / 2.0,
                DisplayManager.ScreenTop + DisplayManager.ScreenHeight / 2.0, DisplayManager.ScreenHeight, detections,
                held, assistance && calibration == null && rawAvailable, contextKey));
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
                        engine.SetCalibration(result);
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
                if (decision.HasCorrection && held && R6ForegroundGuard.StillMatches(foreground) && !Key(0x77))
                {
                    if (!LocalMouseOutput.TryMove(decision.CountsX, decision.CountsY, foreground, out string? error))
                    {
                        assistance = false;
                        message = "Assistance arrêtée : " + error;
                    }
                }
            }
            if (now - lastSave >= 15) { SaveProfile(); lastSave = now; }
            Publish(decision);
            return decision.TargetSourceIndex is int index && index >= 0 && index < predictions.Count ? predictions[index] : null;
        }
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
        Task previous = profileWrite;
        profileWrite = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { AdaptiveProfileStore.Save(path, snapshot); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { lock (gate) { message = "Profil non sauvegardé : " + error.Message; assistance = false; Publish(); } }
        });
    }
    private void Publish(AdaptiveDecision? decision = null)
    {
        var profile = engine.Snapshot(contextKey);
        Volatile.Write(ref state, new(active, calibration != null, assistance, profile.Calibration?.IsUsable == true,
            message, decision?.ContextKey ?? "En attente", calibration?.SamplesX ?? 0, calibration?.SamplesY ?? 0,
            profile.Profiles.Length, decision?.Gain ?? 0, profilePath));
    }
}
