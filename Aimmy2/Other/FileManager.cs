using Aimmy2.AILogic;
using Aimmy2.Class;
using Aimmy2.Other;
using Class;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Other
{
    internal class FileManager
    {
        public FileSystemWatcher? ModelFileWatcher;
        public FileSystemWatcher? ConfigFileWatcher;

        private ListBox ModelListBox;
        internal static ListBox? ModelListBoxForAutomation { get; private set; }
        private Label SelectedModelNotifier;

        private ListBox ConfigListBox;
        private Label SelectedConfigNotifier;

        public bool InQuittingState = false;

        //private DetectedPlayerWindow DetectedPlayerOverlay;
        //private FOV FOVWindow;

        public static AIManager? AIManager;
        public static event Action<AIManager>? ModelLoaded;
        internal static readonly SemaphoreSlim ModelOperationLock = new(1, 1);

        public FileManager(ListBox modelListBox, Label selectedModelNotifier, ListBox configListBox, Label selectedConfigNotifier)
        {
            ModelListBox = modelListBox;
            ModelListBoxForAutomation = modelListBox;
            SelectedModelNotifier = selectedModelNotifier;

            ConfigListBox = configListBox;
            SelectedConfigNotifier = selectedConfigNotifier;

            ModelListBox.SelectionChanged += ModelListBox_SelectionChanged;
            ConfigListBox.SelectionChanged += ConfigListBox_SelectionChanged;

            CheckForRequiredFolders();
            InitializeFileWatchers();
            LoadModelsIntoListBox(null, null);
            LoadConfigsIntoListBox(null, null);
        }

        private void CheckForRequiredFolders()
        {
            // The portable launcher uses a persistent data directory as the working directory.
            string baseDir = Directory.GetCurrentDirectory();
            string[] dirs = ["bin\\models", "bin\\images", "bin\\labels", "bin\\configs"];

            try
            {
                foreach (string dir in dirs)
                {
                    string fullPath = Path.Combine(baseDir, dir);
                    if (!Directory.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating a required directory: {ex}");
                Application.Current.Shutdown();
            }
        }

        public static bool CurrentlyLoadingModel = false;

        private async void ModelListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModelListBox.SelectedItem == null) return;

            string selectedModel = ModelListBox.SelectedItem.ToString()!;
            string modelPath = Path.Combine("bin/models", selectedModel);

            if (Dictionary.lastLoadedModel == selectedModel || !ModelOperationLock.Wait(0))
                return;

            CurrentlyLoadingModel = true;
            string previousModel = Dictionary.lastLoadedModel;
            AIManager? previousManager = AIManager;
            AIManager? manager = null;
            AIManager? loadedManager = null;
            bool notifyModelLoaded = false;

            var toggleKeys = new[] { "Aim Assist", "Constant AI Tracking", "Auto Trigger", "Show Detected Player", "Show AI Confidence", "Show Tracers" };
            var originalToggleStates = new Dictionary<string, dynamic>();

            try
            {
                foreach (var key in toggleKeys)
                {
                    if (Dictionary.toggleState.TryGetValue(key, out dynamic? originalValue))
                    {
                        originalToggleStates[key] = originalValue;
                    }

                    Dictionary.toggleState[key] = false;
                }

                // Let the AI finish up
                await Task.Delay(150);

                DisposeManager(previousManager, "Previous model session did not stop cleanly");
                AIManager = null;

                manager = await CreateLoadedManagerAsync(modelPath);
                if (manager == null)
                {
                    bool restored = await RestorePreviousModelAsync(previousModel);
                    if (!restored)
                    {
                        MarkNoModelLoaded();
                    }

                    LogManager.Log(LogManager.LogLevel.Error, $"Failed to load model: {selectedModel}", true, 5000);
                    return;
                }

                AIManager = manager;
                loadedManager = manager;
                manager = null;
                Dictionary.lastLoadedModel = selectedModel;

                string content = "Loaded Model: " + selectedModel;
                SelectedModelNotifier.Content = content;
                LogManager.Log(LogManager.LogLevel.Info, content, true, 2000);
                notifyModelLoaded = true;
            }
            catch (Exception ex)
            {
                DisposeManager(manager, "Failed model session did not stop cleanly");
                bool restored = await RestorePreviousModelAsync(previousModel);
                if (!restored)
                {
                    MarkNoModelLoaded();
                }

                LogManager.Log(LogManager.LogLevel.Error, $"Failed to load model: {selectedModel}. {ex.Message}", true, 5000);
            }
            finally
            {
                // Restore original values
                foreach (var keyValuePair in originalToggleStates)
                {
                    Dictionary.toggleState[keyValuePair.Key] = keyValuePair.Value;
                }

                CurrentlyLoadingModel = false;
                ModelOperationLock.Release();
            }

            if (notifyModelLoaded && loadedManager != null)
            {
                try
                {
                    ModelLoaded?.Invoke(loadedManager);
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogManager.LogLevel.Warning, $"Model loaded, but a load notification failed: {ex.Message}", true, 5000);
                }
            }
        }

        private static async Task<AIManager?> CreateLoadedManagerAsync(string modelPath)
        {
            var manager = new AIManager(modelPath);
            if (await manager.InitializationTask)
            {
                return manager;
            }

            manager.Dispose();
            return null;
        }

        private async Task<bool> RestorePreviousModelAsync(string previousModel)
        {
            if (previousModel == "N/A")
                return false;

            string previousModelPath = Path.Combine("bin/models", previousModel);
            if (!File.Exists(previousModelPath))
                return false;

            AIManager? restoredManager = await CreateLoadedManagerAsync(previousModelPath);
            if (restoredManager == null)
                return false;

            AIManager = restoredManager;
            Dictionary.lastLoadedModel = previousModel;
            RestoreModelSelection(previousModel);
            SelectedModelNotifier.Content = $"Loaded Model: {previousModel}";
            return true;
        }

        private void MarkNoModelLoaded()
        {
            AIManager = null;
            Dictionary.lastLoadedModel = "N/A";
            RestoreModelSelection("N/A");
            SelectedModelNotifier.Content = "Loaded Model: N/A";
        }

        private static void DisposeManager(AIManager? manager, string warningPrefix)
        {
            try
            {
                manager?.Dispose();
            }
            catch (Exception ex)
            {
                LogManager.Log(LogManager.LogLevel.Warning, $"{warningPrefix}: {ex.Message}");
            }
        }

        private void RestoreModelSelection(string previousModel)
        {
            if (previousModel != "N/A" && ModelListBox.Items.Contains(previousModel))
            {
                ModelListBox.SelectedItem = previousModel;
                return;
            }

            ModelListBox.SelectedItem = null;
        }

        private void ConfigListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigListBox.SelectedItem == null) return;
            string selectedConfig = ConfigListBox.SelectedItem.ToString()!;

            string configPath = Path.Combine("bin/configs", selectedConfig);
            Dictionary.lastLoadedConfig = selectedConfig;

            Aimmy2.MainWindow.ApplyConfigLoadDefaults(Dictionary.sliderSettings);
            SaveDictionary.LoadJSON(Dictionary.sliderSettings, configPath);
            PropertyChanger.PostNewConfig(configPath, true);

            SelectedConfigNotifier.Content = "Loaded Config: " + selectedConfig;
        }

        public void InitializeFileWatchers()
        {
            ModelFileWatcher = new FileSystemWatcher();
            ConfigFileWatcher = new FileSystemWatcher();

            InitializeWatcher(ref ModelFileWatcher, "bin/models", "*.onnx");
            InitializeWatcher(ref ConfigFileWatcher, "bin/configs", "*.cfg");
        }

        private void InitializeWatcher(ref FileSystemWatcher watcher, string path, string filter)
        {
            watcher.Path = path;
            watcher.Filter = filter;
            watcher.EnableRaisingEvents = true;

            if (filter == "*.onnx")
            {
                watcher.Changed += LoadModelsIntoListBox;
                watcher.Created += LoadModelsIntoListBox;
                watcher.Deleted += LoadModelsIntoListBox;
                watcher.Renamed += LoadModelsIntoListBox;
            }
            else if (filter == "*.cfg")
            {
                watcher.Changed += LoadConfigsIntoListBox;
                watcher.Created += LoadConfigsIntoListBox;
                watcher.Deleted += LoadConfigsIntoListBox;
                watcher.Renamed += LoadConfigsIntoListBox;
            }
        }

        public void LoadModelsIntoListBox(object? sender, FileSystemEventArgs? e)
        {
            if (!InQuittingState)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    string[] onnxFiles = Directory.GetFiles("bin/models", "*.onnx");
                    ModelListBox.Items.Clear();

                    foreach (string filePath in onnxFiles)
                    {
                        ModelListBox.Items.Add(Path.GetFileName(filePath));
                    }

                    if (ModelListBox.Items.Count > 0)
                    {
                        string? lastLoadedModel = Dictionary.lastLoadedModel;
                        if (lastLoadedModel != "N/A" && ModelListBox.Items.Contains(lastLoadedModel)) { ModelListBox.SelectedItem = lastLoadedModel; }
                        SelectedModelNotifier.Content = $"Loaded Model: {lastLoadedModel}";
                    }
                });
            }
        }

        public void LoadConfigsIntoListBox(object? sender, FileSystemEventArgs? e)
        {
            if (!InQuittingState)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    string[] configFiles = Directory.GetFiles("bin/configs", "*.cfg");
                    ConfigListBox.Items.Clear();

                    foreach (string filePath in configFiles)
                    {
                        ConfigListBox.Items.Add(Path.GetFileName(filePath));
                    }

                    if (ConfigListBox.Items.Count > 0)
                    {
                        string? lastLoadedConfig = Dictionary.lastLoadedConfig;
                        if (lastLoadedConfig != "N/A" && ConfigListBox.Items.Contains(lastLoadedConfig)) { ConfigListBox.SelectedItem = lastLoadedConfig; }

                        SelectedConfigNotifier.Content = "Loaded Config: " + lastLoadedConfig;
                    }
                });
            }
        }

        public static async Task<HashSet<string>> RetrieveAndAddFiles(string repoLink, string localPath, HashSet<string> allFiles)
        {
            try
            {
                GithubManager githubManager = new();

                var files = await githubManager.FetchGithubFilesAsync(repoLink);

                foreach (var file in files)
                {
                    if (file == null) continue;

                    if (!allFiles.Contains(file) && !File.Exists(Path.Combine(localPath, file)))
                    {
                        allFiles.Add(file);
                    }
                }

                githubManager.Dispose();

                return allFiles;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.ToString());
            }
        }
    }
}
