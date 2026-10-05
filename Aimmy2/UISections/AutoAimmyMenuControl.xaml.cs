using Aimmy2.Adaptive;
using Other;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Aimmy2.Controls;

public partial class AutoAimmyMenuControl : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PlayerSessionContext imported = PlayerSessionContext.Load(ObservationMode.DataDirectory);
    private string? latestReport;
    private bool refreshing;
    private sealed record SettingsChoice(string Key, string Label);
    public ScrollViewer DashboardScrollViewer => DashboardScroll;

    public AutoAimmyMenuControl()
    {
        InitializeComponent();
        timer.Tick += (_, _) => RefreshView();
        Loaded += (_, _) => { imported = PlayerSessionContext.Load(ObservationMode.DataDirectory); LoadAccountChoices(); RefreshView(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "inconnu";
    private void RefreshView()
    {
        VersionText.Text = "v" + (Environment.GetEnvironmentVariable("AUTOAIMMY_VERSION") ?? typeof(AutoAimmyMenuControl).Assembly.GetName().Version?.ToString());
        var observation = FileManager.AIManager?.Observation;
        bool recording = observation?.IsRecording == true;
        ObservationTitle.Text = !ObservationMode.BlocksOutput ? "Observation désactivée" : observation == null ? "Prête — charger un modèle" :
            observation.HasFailed ? "Observation interrompue" : recording ? "Observation en cours" : "Observation terminée";
        ObservationDetail.Text = observation == null ? "Ouvrez l’onglet Modèles et chargez votre fichier ONNX pour démarrer." :
            observation.HasFailed ? observation.Failure : recording ?
            $"{TimeSpan.FromSeconds(observation.ElapsedSeconds):mm\\:ss} / {TimeSpan.FromSeconds(ObservationMode.Options.DurationSeconds):mm\\:ss} · rapport créé automatiquement à la fin." :
            observation.ReportPath != null ? "Rapport prêt. Vous pouvez fermer l’application normalement." :
            $"Export automatique indisponible : {observation.ExportFailure ?? "aucun ZIP"}. Les fichiers de session sont conservés.";
        ModelText.Text = "Modèle : " + (Class.Dictionary.lastLoadedModel == "N/A" ? "aucun" : Class.Dictionary.lastLoadedModel) +
            " · Viseur : " + (ObservationMode.Options.AimReference == AimReference.ScreenCenter ? "centre de l’écran" : "curseur") +
            " · Moniteur : " + Aimmy2.Class.DisplayManager.ScreenWidth + " × " + Aimmy2.Class.DisplayManager.ScreenHeight;
        // During observation display the immutable context used by its measurements.
        var context = recording ? observation!.Context : imported;
        var settings = context.Settings;
        string status = settings?.ImportStatus ?? context.Status;
        ImportTitle.Text = context.SettingsSource == "SettingsFile" && status == "Imported" ? "Réglages détectés · Imported" :
            status == "ImportedCandidate" ? "Réglages proposés — dernier fichier sauvegardé, à vérifier" :
            status == "AmbiguousAccounts" ? "Plusieurs comptes R6 — réglages inconnus" :
            status == "NotFound" ? "Configuration R6 introuvable" : context.SettingsSource == "UserDeclared" && settings != null ?
            "Ancien profil déclaré — relire les réglages" : "Réglages indisponibles · " + status;
        string sourceDate = DateTime.TryParse(settings?.SettingsFileLastWriteUtc, out var stamp) ? stamp.ToLocalTime().ToString("g") : "inconnue";
        ImportDetail.Text = context.SettingsSource == "SettingsFile" ?
            $"Source : fichier R6 sauvegardé · modification : {sourceDate}. Les changements non sauvegardés ne sont pas visibles." :
            "Aucune lecture automatique valide disponible. Relire les réglages ne demande aucune saisie.";
        SettingsText.Text = $"Sensibilité H / V : {Number(settings?.HorizontalSensitivity)} / {Number(settings?.VerticalSensitivity)}     FOV : {Number(settings?.Fov)}\n" +
            $"Résolution du jeu : {settings?.Resolution ?? "inconnue"}     Ratio : {settings?.AspectRatio ?? "inconnu"}";
        AdsText.Text = settings?.AdsSensitivityByScope?.Count > 0 ? "ADS sauvegardés : " + string.Join(" · ", settings.AdsSensitivityByScope.Select(p =>
            p.Key.Replace("ADSMouseSensitivity", "").Replace("xHalf", ".5x") + " = " + Number(p.Value))) + "\nCes valeurs ne révèlent pas la lunette équipée." : "Sensibilités ADS : inconnues";
        UnknownText.Text = "Pas encore détectés : DPI de la souris, arme équipée, lunette équipée et état de visée.";
        RefreshSettingsButton.IsEnabled = !recording && !refreshing;
        if (recording) ActionMessage.Text = "Réglages figés pour cette session. La relecture sera disponible à la fin.";
        SelectAccountButton.IsEnabled = !recording && !refreshing;
        AccountChoices.IsEnabled = !recording && !refreshing;
        var hud = Aimmy2.VisualAnalysis.LiveHudObserver.Instance.Latest;
        LiveStatusText.Text = hud.Status == "ReadingHUD" ? "Lecture active : HUD de R6, sur ce PC" : hud.Status == "WaitingForGame" ? "En attente / pause : remettre R6 au premier plan" : "Lecture indisponible / démarrage";
        LiveValuesText.Text = $"Texte arme reconnu : {hud.WeaponText ?? "inconnu"} · texte lunette : {hud.ScopeText ?? "inconnu"} · ADS réel : inconnu" +
            (hud.FrameUtc.HasValue ? $"\nDernière image lue : {hud.FrameUtc.Value.ToLocalTime():HH:mm:ss}" : "");
        LiveDetailText.Text = hud.Detail ?? "Lecture locale du HUD, sans enregistrer d’images ni envoyer le jeu à un serveur.";
        try
        {
            latestReport = Directory.Exists(ObservationMode.ExportDirectory) ? new DirectoryInfo(ObservationMode.ExportDirectory)
                .GetFiles("AutoAimmy-report-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName : null;
            ReportText.Text = latestReport == null ? "Aucun rapport ZIP disponible pour le moment." : "Dernier ZIP : " + Path.GetFileName(latestReport);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { latestReport = null; ReportText.Text = "Dossier des rapports inaccessible."; }
        LatestReportButton.IsEnabled = latestReport != null;
        NextStepText.Text = recording ? "Jouez votre session de test. Fermer normalement l’application termine l’observation et crée le ZIP." :
            observation != null ? "Envoyez le ZIP et une courte vidéo montrant le HUD et les passages avec / sans visée pour préparer l’analyse visuelle." :
            "Chargez le modèle, vérifiez les cadres sur les bonnes cibles et enregistrez une courte vidéo de votre test contre les IA.";
    }

    private void LoadAccountChoices()
    {
        try
        {
            string path = Path.Combine(ObservationMode.DataDirectory, "settings-candidates.json");
            var choices = File.Exists(path) && new FileInfo(path).Length < 65536 ?
                JsonSerializer.Deserialize<SettingsChoice[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive=true }) : null;
            AccountChoices.ItemsSource = choices;
            AccountChoicePanel.Visibility = choices?.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (choices?.Length > 0)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ObservationMode.DataDirectory,"active-profile.json")));
                string? key = document.RootElement.TryGetProperty("SettingsFileKey",out var value) ? value.GetString() : null;
                int selected = Array.FindIndex(choices,c=>c.Key==key);
                AccountChoices.SelectedIndex = selected >= 0 ? selected : 0;
            }
        }
        catch { AccountChoicePanel.Visibility = Visibility.Collapsed; }
    }
    private async void RefreshSettings_Click(object sender, RoutedEventArgs e) => await RefreshSettingsAsync(null);
    private async void SelectAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountChoices.SelectedItem is SettingsChoice choice) await RefreshSettingsAsync(choice.Key);
    }
    private async Task RefreshSettingsAsync(string? selectedKey)
    {
        if (refreshing || FileManager.AIManager?.Observation?.IsRecording == true) return;
        refreshing = true;
        RefreshSettingsButton.IsEnabled = false;
        try
        {
            string module = Path.Combine(AppContext.BaseDirectory, "AutomaticSettings.psm1");
            if (!File.Exists(module)) throw new FileNotFoundException("Module d’import absent. Lancez la version distribuée avec AutoAimmy.cmd.");
            if (Environment.GetEnvironmentVariable("AUTOAIMMY_DATA_DIR") == null)
                throw new InvalidOperationException("Ouvrez AutoAimmy.cmd pour relire les réglages dans votre dossier de données.");
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            string root = Path.GetFullPath(Path.Combine(ObservationMode.DataDirectory, ".."));
            string script = "$ErrorActionPreference='Stop'; Import-Module " + Quote(module) + " -Force; $null=Sync-AutomaticPlayerProfile -Root " + Quote(root);
            if (selectedKey != null)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(selectedKey, "^[a-f0-9]{64}$")) throw new InvalidDataException("Configuration invalide.");
                script += " -SelectedFileKey " + Quote(selectedKey);
            }
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Impossible de démarrer la lecture des réglages.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await output;
            string message = await error;
            if (process.ExitCode != 0) throw new IOException("Relecture échouée : " + message);
            imported = PlayerSessionContext.Load(ObservationMode.DataDirectory);
            LoadAccountChoices();
            ActionMessage.Text = "Configuration relue. Ces réglages seront utilisés pour la prochaine observation.";
        }
        catch (Exception error) { ActionMessage.Text = error.Message; }
        finally { refreshing = false; RefreshView(); }
    }

    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { ActionMessage.Text = "Ouverture impossible : " + error.Message; }
    }
    private void OpenModels_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(ObservationMode.DataDirectory, "bin", "models"));
    private void OpenReports_Click(object sender, RoutedEventArgs e) => OpenFolder(ObservationMode.ExportDirectory);
    private void ShowLatestReport_Click(object sender, RoutedEventArgs e)
    {
        if (latestReport == null) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select," + latestReport);
            Process.Start(start);
        }
        catch (Exception error) { ActionMessage.Text = "Ouverture impossible : " + error.Message; }
    }
}
