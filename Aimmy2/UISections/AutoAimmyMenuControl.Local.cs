using Aimmy2.Adaptive;
using Aimmy2.Class;
using Aimmy2.LocalAutomation;
using Aimmy2.LocalCapture;
using Aimmy2.ModelLearning;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Aimmy2.Controls;

public partial class AutoAimmyMenuControl
{
    private readonly ModelLearningCoordinator learning = new(ObservationMode.DataDirectory);
    private readonly TrainingJobCoordinator training = new(ObservationMode.DataDirectory);
    private bool localBusy, learningBusy, inspecting;
    private CancellationTokenSource? learningCancellation;
    private DateTime lastLearningRefresh;
    private LearningState? learningState;

    private void RefreshLocalView()
    {
        var session = LocalAutomationSession.Instance.State;
        var capture = LocalCaptureService.Instance.State;
        LocalStatusText.Text = session.Message;
        LocalProfileText.Text = $"Calibration : {(session.Calibrated ? "enregistrée pour cette vue" : "nécessaire")} · {session.ProfileCount} sous-profils\n" +
            "Contexte : " + session.Context.Replace("Small", "petite cible").Replace("Medium", "cible moyenne").Replace("Large", "grande cible")
                .Replace("Slow", "mouvement lent").Replace("Moving", "mouvement modéré").Replace("Fast", "mouvement rapide") +
            (session.Gain > 0 ? $" · gain {session.Gain:0.00}" : "");
        LocalCaptureText.Text = $"Capture : {capture.FullFrames} images du jeu · {capture.CandidateFrames} exemples · {capture.BytesWritten / 1048576.0:0.0} Mo" +
            (capture.Error != null ? "\n" + capture.Error : capture.Active ? " · active lorsque R6 est au premier plan" : " · arrêtée");
        StartLocalButton.IsEnabled = !localBusy && !learningBusy && !session.Active && global::Other.FileManager.AIManager?.IsLoaded == true;
        CalibrateLocalButton.IsEnabled = !localBusy && !learningBusy && !session.Calibrating && global::Other.FileManager.AIManager?.IsLoaded == true;
        AssistLocalButton.IsEnabled = !localBusy && !learningBusy && session.Active && session.Calibrated && !session.Calibrating;
        AssistLocalButton.Content = session.AssistanceEnabled ? "Désactiver l’assistance" : "Activer l’assistance expérimentale";
        StopLocalButton.IsEnabled = !localBusy && session.Active;
        ReviewLearningButton.IsEnabled = !learningBusy && !session.Active;
        OptimizeLearningButton.IsEnabled = !learningBusy && !session.Active;
        CompareModelButton.IsEnabled = !learningBusy && !session.Active;
        RollbackModelButton.IsEnabled = !learningBusy && !session.Active;
        ImportTrainingButton.IsEnabled = !learningBusy && !session.Active;
        StartTrainingButton.IsEnabled = !learningBusy && !session.Active;
        CancelTrainingButton.IsEnabled = learningBusy;
        TrainingStatusText.Text = training.State.Detail;
        OutputModeText.Text = session.AssistanceEnabled ? "Assistance calibrée autorisée : touche de visée maintenue, R6 au premier plan. F8 pour arrêter." :
            "Observation / calibration : aucun déplacement de souris généré.";
        if (session.Active)
        {
            ObservationTitle.Text = session.Calibrating ? "Calibration locale" : session.AssistanceEnabled ? "Session locale avec assistance" : "Session locale en observation";
            ObservationDetail.Text = "Collecte et sous-profils sur ce PC. Le rapport de dix minutes reste disponible séparément.";
            NextStepText.Text = "Utilisez Arrêter la session, puis Vérifier les images. Les comparaisons se font dans l’application ; aucun ZIP à envoyer.";
        }
        if (learningState != null)
            LearningStatusText.Text = $"{learningState.ReviewedImages} images vérifiées · {learningState.PendingImages} à examiner\n" + learningState.Detail;
        if (!inspecting && DateTime.UtcNow - lastLearningRefresh > TimeSpan.FromSeconds(8)) _ = RefreshLearningAsync();
    }

    private async Task RefreshLearningAsync(bool import = false)
    {
        // A user-requested import must not disappear behind the periodic status refresh.
        // Yield the UI thread until that earlier inspection finishes, then do the import.
        if (inspecting && !import) return;
        while (inspecting) await Task.Delay(25);
        inspecting = true;
        lastLearningRefresh = DateTime.UtcNow;
        try
        {
            string path = LocalAutomationSession.Instance.ModelPath;
            learningState = await Task.Run(() => { if (import) learning.ImportRecordingCaptures(); return learning.Inspect(path); });
        }
        catch (Exception error) { LearningStatusText.Text = "Lecture locale impossible : " + error.Message; }
        finally { inspecting = false; }
    }

    private async void StartLocal_Click(object sender, RoutedEventArgs e)
    {
        localBusy = true; RefreshView();
        try { await Task.Run(() => LocalAutomationSession.Instance.Start()); }
        catch (Exception error) { ActionMessage.Text = error.Message; }
        finally { localBusy = false; RefreshView(); }
    }
    private async void CalibrateLocal_Click(object sender, RoutedEventArgs e)
    {
        localBusy = true; RefreshView();
        try
        {
            await Task.Run(() => LocalAutomationSession.Instance.BeginCalibration());
            new CalibrationHintWindow().Show();
        }
        catch (Exception error) { ActionMessage.Text = error.Message; }
        finally { localBusy = false; RefreshView(); }
    }
    private void AssistLocal_Click(object sender, RoutedEventArgs e)
    {
        try { LocalAutomationSession.Instance.EnableAssistance(!LocalAutomationSession.Instance.State.AssistanceEnabled); }
        catch (Exception error) { ActionMessage.Text = error.Message; }
        RefreshView();
    }
    private async void StopLocal_Click(object sender, RoutedEventArgs e)
    {
        localBusy = true; RefreshView();
        try { await LocalAutomationSession.Instance.StopAsync(); await RefreshLearningAsync(import: true); }
        catch (Exception error) { ActionMessage.Text = error.Message; }
        finally { localBusy = false; RefreshView(); }
    }
    private void OpenRecordings_Click(object sender, RoutedEventArgs e)
    {
        string? playback = LocalCaptureService.Instance.State.PlaybackPath;
        if (playback != null && File.Exists(playback))
        {
            try { Process.Start(new ProcessStartInfo(playback) { UseShellExecute = true }); }
            catch (Exception error) { ActionMessage.Text = error.Message; }
        }
        else OpenFolder(Path.Combine(ObservationMode.DataDirectory, "local-capture"));
    }
    private async void ReviewLearning_Click(object sender, RoutedEventArgs e)
    {
        learningBusy = true; RefreshView();
        try
        {
            await RefreshLearningAsync(import: true);
            string[] classes = global::Other.FileManager.AIManager?.ModelClasses.OrderBy(p => p.Key).Select(p => p.Value).ToArray() ?? new[] { "enemy" };
            var review = new LearningReviewWindow(learning, classes) { Owner = Window.GetWindow(this) };
            review.ShowDialog();
            await RefreshLearningAsync(import: true);
        }
        catch (Exception error) { LearningActionText.Text = error.Message; }
        finally { learningBusy = false; RefreshView(); }
        // Once verified examples are sufficient, measure a new configuration without another questionnaire.
        if (learningState?.ReviewedImages >= 40) await OptimizeAsync();
    }
    private async void OptimizeLearning_Click(object sender, RoutedEventArgs e) => await OptimizeAsync();
    private async Task OptimizeAsync()
    {
        if (learningBusy || LocalAutomationSession.Instance.Active) return;
        learningBusy = true; RefreshView();
        learningCancellation = new();
        LearningActionText.Text = "Comparaison locale des seuils en cours. Les sessions de validation restent séparées.";
        try
        {
            string model = LocalAutomationSession.Instance.ModelPath;
            double confidence = LocalAutomationSession.Instance.MinimumConfidence;
            var result = await Task.Run(() =>
            {
                learning.ImportRecordingCaptures();
                using var detector = new OnnxLearningDetector(model);
                var dataset = learning.CreateDataset(detector.ClassNames, ModelLearningCoordinator.HashFile(model));
                return learning.OptimizeConfidence(dataset, model, confidence, p => new OnnxLearningDetector(p), learningCancellation.Token);
            });
            if (result.Accepted) LocalAutomationSession.Instance.ReloadConfiguration();
            LearningActionText.Text = result.Accepted ?
                $"Seuil validé et enregistré : {result.ProposedConfidence:P0}. F1 sur validation : {result.BaselineValidation.F1:P1} → {result.ProposedValidation.F1:P1}. Il sera utilisé à la prochaine session." :
                $"Réglage conservé : le candidat n’a pas démontré de progrès suffisant. {string.Join(", ", result.Reasons)}";
        }
        catch (OperationCanceledException) { LearningActionText.Text = "Calcul local annulé ; réglage conservé."; }
        catch (Exception error) { LearningActionText.Text = FriendlyLearningError(error.Message); }
        finally { learningCancellation?.Dispose(); learningCancellation = null; learningBusy = false; RefreshView(); }
    }
    private async void CompareModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Modèle ONNX|*.onnx", Title = "Choisir le candidat à comparer sur vos images vérifiées" };
        if (dialog.ShowDialog() != true) return;
        learningBusy = true; RefreshView();
        learningCancellation = new();
        LearningActionText.Text = "Comparaison des deux modèles sur les mêmes images locales de validation…";
        try
        {
            string baseline = LocalAutomationSession.Instance.ModelPath;
            var selection = await Task.Run(() =>
            {
                using var detector = new OnnxLearningDetector(baseline);
                return learning.EvaluateAndPromote(learning.CreateDataset(detector.ClassNames, ModelLearningCoordinator.HashFile(baseline)), baseline, dialog.FileName, p => new OnnxLearningDetector(p), learningCancellation.Token);
            });
            ActivateLocalModel(selection.ActivePath);
            LearningActionText.Text = "Candidat validé et sélectionné. L’ancien modèle reste disponible avec Revenir au modèle précédent.";
        }
        catch (OperationCanceledException) { LearningActionText.Text = "Comparaison annulée ; modèle conservé."; }
        catch (Exception error) { LearningActionText.Text = FriendlyLearningError(error.Message); }
        finally { learningCancellation?.Dispose(); learningCancellation = null; learningBusy = false; RefreshView(); }
    }
    private void RollbackModel_Click(object sender, RoutedEventArgs e)
    {
        try { ActivateLocalModel(learning.Rollback()); LearningActionText.Text = "Modèle précédent sélectionné."; }
        catch (Exception error) { LearningActionText.Text = FriendlyLearningError(error.Message); }
    }
    private async void ImportTraining_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Poids PyTorch|*.pt", Title = "Sélectionnez les poids source de confiance correspondant à votre modèle ONNX" };
        if (dialog.ShowDialog() != true) return;
        learningBusy = true; RefreshView();
        try
        {
            string model = LocalAutomationSession.Instance.ModelPath;
            await Task.Run(() => training.RegisterTrustedSource(model, dialog.FileName));
            LearningActionText.Text = "Poids source liés au modèle actif. Le moteur local vérifiera les prérequis avant de commencer.";
            await RefreshLearningAsync();
        }
        catch (Exception error) { LearningActionText.Text = error.Message; }
        finally { learningBusy = false; RefreshView(); }
    }
    private async void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        if (learningBusy || LocalAutomationSession.Instance.Active) return;
        learningBusy = true; RefreshView();
        try
        {
            await training.StartAsync(LocalAutomationSession.Instance.ModelPath);
            string? selected = learning.ResolveSelectedModel(null);
            if (training.State.Status == "Accepted" && selected != null) ActivateLocalModel(selected);
            LearningActionText.Text = training.State.Detail;
        }
        catch (Exception error) { LearningActionText.Text = FriendlyLearningError(error.Message); }
        finally { learningBusy = false; await RefreshLearningAsync(); RefreshView(); }
    }
    private void CancelTraining_Click(object sender, RoutedEventArgs e) => StopBackgroundWork();
    public void StopBackgroundWork() { training.Cancel(); learningCancellation?.Cancel(); }
    private static void ActivateLocalModel(string path)
    {
        // FileManager's existing model picker performs load verification and restores on failure.
        string destination = Path.Combine(ObservationMode.DataDirectory, "bin", "models", "local-" + ModelLearningCoordinator.HashFile(path)[..12] + ".onnx");
        if (!File.Exists(destination)) File.Copy(path, destination);
        var modelList = global::Other.FileManager.ModelListBoxForAutomation;
        if (modelList == null) throw new InvalidOperationException("Ouvrez l’onglet Modèles puis sélectionnez " + Path.GetFileName(destination));
        string name = Path.GetFileName(destination);
        if (!modelList.Items.Contains(name)) modelList.Items.Add(name);
        modelList.SelectedItem = name;
    }
    private static string FriendlyLearningError(string text)
    {
        if (text.Contains("ReviewedDatasetMissing")) return "Il faut au moins 40 images vérifiées dans AUTO avant de comparer les réglages.";
        if (text.Contains("IndependentValidationSessionMissing")) return "Enregistrez une deuxième session : elle servira à vérifier le résultat sur d’autres images.";
        if (text.Contains("InsufficientSplit")) return "Vérifiez au moins 20 images de chaque session, dont 20 cibles dans la session de validation.";
        if (text.Contains("NoImprovement")) return "Le candidat n’améliore pas suffisamment la détection sur les images de validation. Modèle conservé.";
        return text;
    }
}
