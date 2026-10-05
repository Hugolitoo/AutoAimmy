using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Aimmy2.ModelLearning;

public sealed record TrainingJobState(string Status, string Detail, string[] Blockers, string? CandidatePath = null);
public sealed record TrustedTrainingSource(string ModelSha256, string SourcePath, string SourceSha256);

/// <summary>Runs only the packaged offline training script, with explicit local source trust and cancellation.</summary>
public sealed class TrainingJobCoordinator
{
    private readonly string _dataDirectory;
    private readonly string _root;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private TrainingJobState _state = new("Idle", "Entraînement local : poids source et environnement requis.", []);
    public TrainingJobState State { get { lock (_gate) return _state; } }
    public TrainingJobCoordinator(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _root = Path.Combine(_dataDirectory, "learning");
    }

    // Called only after the user explicitly selects a source checkpoint they trust.
    // PyTorch checkpoints are executable pickle-based artifacts; presence alone never authorizes loading.
    public void RegisterTrustedSource(string activeModelPath, string selectedPtPath)
    {
        if (!File.Exists(activeModelPath) || !File.Exists(selectedPtPath) || !string.Equals(Path.GetExtension(selectedPtPath), ".pt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select an existing .pt source checkpoint and an active ONNX model.");
        if (new FileInfo(selectedPtPath).Length is <= 0 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid source checkpoint size.");
        lock (_gate)
        {
            if (_cancellation != null) throw new InvalidOperationException("A training job is already active.");
            var modelHash = ModelLearningCoordinator.HashFile(activeModelPath);
            var sourceHash = ModelLearningCoordinator.HashFile(selectedPtPath);
            var sources = Path.Combine(_root, "source");
            Directory.CreateDirectory(sources);
            var saved = Path.Combine(sources, sourceHash + ".pt");
            if (!File.Exists(saved)) File.Copy(selectedPtPath, saved);
            if (ModelLearningCoordinator.HashFile(saved) != sourceHash) throw new InvalidDataException("Source checkpoint copy failed hash verification.");
            Directory.CreateDirectory(Path.Combine(_root, "trusted-sources"));
            var destination = Path.Combine(_root, "trusted-sources", modelHash + ".json");
            var temporary = destination + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new TrustedTrainingSource(modelHash, saved, sourceHash)));
            File.Move(temporary, destination, true);
            _state = new("SourceRegistered", "Poids source de confiance enregistrés pour ce modèle. Rien n'a encore été entraîné.", []);
        }
    }

    public async Task StartAsync(string activeModelPath, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_cancellation != null) throw new InvalidOperationException("A training job is already active.");
            _cancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _state = new("Checking", "Vérification des prérequis locaux…", []);
        }
        try
        {
            if (!File.Exists(activeModelPath)) { Block("ActiveModelMissing", "Chargez un modèle ONNX."); return; }
            var modelHash = await Task.Run(() => ModelLearningCoordinator.HashFile(activeModelPath), cancellation.Token);
            var trustPath = Path.Combine(_root, "trusted-sources", modelHash + ".json");
            if (!File.Exists(trustPath)) { Block("SourceWeightsTrustRequired", "Sélectionnez les poids source .pt de confiance correspondant au modèle ONNX."); return; }
            var source = JsonSerializer.Deserialize<TrustedTrainingSource>(await File.ReadAllTextAsync(trustPath, cancellation.Token));
            if (source == null || source.ModelSha256 != modelHash || !File.Exists(source.SourcePath) ||
                source.SourcePath != Path.Combine(_root, "source", source.SourceSha256 + ".pt") || ModelLearningCoordinator.HashFile(source.SourcePath) != source.SourceSha256)
            { Block("SourceWeightsChanged", "Les poids source ont changé ; sélectionnez à nouveau le fichier de confiance."); return; }
            var python = DiscoverPython();
            if (python == null) { Block("PythonRuntimeMissing", "Python local est absent. L'entraînement nécessite aussi PyTorch, Ultralytics et ONNX ; aucun téléchargement automatique n'est effectué."); return; }
            var runner = Path.Combine(AppContext.BaseDirectory, "local-learning", "runner.py");
            if (!File.Exists(runner)) { Block("PackagedRunnerMissing", "Le module d'entraînement local n'est pas présent dans cette installation."); return; }
            var inspection = await RunPython(python, runner, ["--mode", "inspect", "--weights", source.SourcePath], cancellation.Token);
            var inspectJson = LastJsonLine(inspection.Output);
            if (inspection.ExitCode != 0 || inspectJson == null)
            { Block("TrainingEnvironmentProbeFailed", "L'environnement Python local n'a pas pu être vérifié."); return; }
            using (inspectJson)
            {
                if (inspectJson.RootElement.GetProperty("Status").GetString() != "PrerequisitesPresent")
                {
                    var blockers = inspectJson.RootElement.GetProperty("Blockers").EnumerateArray().Select(x => x.GetString() ?? "MissingDependency").ToArray();
                    Set(new("Blocked", "Dépendances locales manquantes : " + string.Join(", ", blockers), blockers));
                    return;
                }
            }
            var learning = new ModelLearningCoordinator(_dataDirectory);
            var dataset = await Task.Run(() =>
            {
                using var model = new OnnxLearningDetector(activeModelPath);
                return learning.CreateDataset(model.ClassNames, modelHash);
            }, cancellation.Token);
            var output = Path.Combine(_root, "training-jobs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
            Set(new("Training", "Entraînement local sur les images vérifiées. La validation indépendante reste réservée.", []));
            var trained = await RunPython(python, runner, ["--mode", "train", "--weights", source.SourcePath, "--dataset", dataset.Directory,
                "--output", output, "--trusted-local-weights", "--epochs", "10", "--device", "cpu"], cancellation.Token);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "process.log"), trained.Output, cancellation.Token);
            using var result = LastJsonLine(trained.Output);
            if (trained.ExitCode != 0 || result == null || result.RootElement.GetProperty("Status").GetString() != "CandidateAwaitingIndependentEvaluation")
            {
                Set(new("Failed", "L'entraînement local n'a pas produit de candidat validable. Le journal reste sur ce PC.", ["TrainingFailed"]));
                return;
            }
            var candidate = result.RootElement.GetProperty("CandidatePath").GetString()!;
            if (!Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                ModelLearningCoordinator.HashFile(candidate) != result.RootElement.GetProperty("CandidateSha256").GetString())
                throw new InvalidDataException("Invalid candidate output path or hash.");
            Set(new("Evaluating", "Comparaison du candidat et du modèle de départ sur une session indépendante…", [], candidate));
            try
            {
                var promoted = await Task.Run(() => learning.EvaluateAndPromote(dataset, activeModelPath, candidate, p => new OnnxLearningDetector(p), cancellation.Token), cancellation.Token);
                Set(new("Accepted", "Candidat amélioré sur la validation locale, enregistré avec retour arrière. Il pourra être chargé à la prochaine session.", [], promoted.ActivePath));
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("CandidateRejected:", StringComparison.Ordinal))
            { Set(new("Rejected", "Le candidat n'améliore pas suffisamment la validation locale. Le modèle précédent est conservé.", [exception.Message], candidate)); }
        }
        catch (OperationCanceledException) { Set(new("Cancelled", "Entraînement arrêté. Le modèle actif est conservé.", [])); }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Set(new("Blocked", exception.Message, [exception.GetType().Name])); }
        catch (Exception exception)
        { Set(new("Failed", "Échec de l'entraînement local : " + exception.Message, [exception.GetType().Name])); }
        finally
        {
            lock (_gate) { _cancellation?.Dispose(); _cancellation = null; }
        }
    }

    public void Cancel() { lock (_gate) _cancellation?.Cancel(); }
    private void Block(string blocker, string detail) => Set(new("Blocked", detail, [blocker]));
    private void Set(TrainingJobState state) { lock (_gate) _state = state; }

    private string? DiscoverPython()
    {
        var bundled = Path.Combine(_root, "runtime", "python.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try { var candidate = Path.Combine(directory.Trim('"'), "python.exe"); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunPython(string python, string runner, string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-I");
        info.ArgumentList.Add(runner);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Python did not start.");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // Bounded capture still drains both streams to avoid pipe deadlocks on training logs.
        async Task<string> Drain(StreamReader reader)
        {
            var lines = new Queue<string>();
            int characters = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                lines.Enqueue(line);
                characters += line.Length;
                while (characters > 512 * 1024 && lines.Count > 1) characters -= lines.Dequeue().Length;
            }
            return string.Join(Environment.NewLine, lines);
        }
        var outputTask = Drain(process.StandardOutput);
        var errorTask = Drain(process.StandardError);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        return (process.ExitCode, error + Environment.NewLine + output);
    }
    private static JsonDocument? LastJsonLine(string output)
    {
        foreach (var line in output.Split('\n').Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try { return JsonDocument.Parse(line); } catch (JsonException) { }
        }
        return null;
    }
}
