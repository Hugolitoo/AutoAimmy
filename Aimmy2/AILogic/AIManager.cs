using Aimmy2.Adaptive;
using Aimmy2.LocalAutomation;
using Aimmy2.LocalCapture;
using AILogic;
using Aimmy2.Class;
using Class;
using InputLogic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;
using Other;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;
using Visuality;
using static AILogic.MathUtil;
using static Other.LogManager;

namespace Aimmy2.AILogic
{
    internal class AIManager : IDisposable
    {
        private static readonly TimeSpan BenchmarkWarmupMinimumDuration = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BenchmarkWarmupMaximumDuration = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan BenchmarkSampleDuration = TimeSpan.FromSeconds(10);
        private const int BenchmarkWarmupMinimumInferences = 30;
        private const int BenchmarkStabilityWindow = 8;

        #region Variables

        private int _currentImageSize;
        private readonly object _sizeLock = new object();
        private volatile bool _sizeChangePending = false;

        public void RequestSizeChange(int newSize)
        {
            lock (_sizeLock)
            {
                _sizeChangePending = true;
            }
        }

        // Dynamic properties instead of constants
        public int IMAGE_SIZE => _currentImageSize;
        private int NUM_DETECTIONS { get; set; } = 8400; // Will be set dynamically for dynamic models
        private bool IsDynamicModel { get; set; } = false;

        // Public static property to check if current loaded model is dynamic
        public static bool CurrentModelIsDynamic { get; private set; } = false;
        private int ModelFixedSize { get; set; } = 640; // Store the fixed size for non-dynamic models
        private int NUM_CLASSES { get; set; } = 1;
        private Dictionary<int, string> _modelClasses = new Dictionary<int, string>
        {
            { 0, "enemy" }
        };
        public Dictionary<int, string> ModelClasses => _modelClasses; // apparently this is better than making _modelClasses public
        public static event Action<Dictionary<int, string>>? ClassesUpdated;
        public static event Action<int>? ImageSizeUpdated;
        public static event Action<bool>? DynamicModelStatusChanged;

        private const int SAVE_FRAME_COOLDOWN_MS = 500;

        private DateTime lastSavedTime = DateTime.MinValue;
        private List<string>? _outputNames;
        private RectangleF LastDetectionBox;
        private KalmanPrediction kalmanPrediction;
        private WiseTheFoxPrediction wtfpredictionManager;

        private byte[]? _bitmapBuffer; // Reusable buffer for bitmap operations

        // Display-aware properties
        private int ScreenWidth => DisplayManager.ScreenWidth;
        private int ScreenHeight => DisplayManager.ScreenHeight;
        private int ScreenLeft => DisplayManager.ScreenLeft;
        private int ScreenTop => DisplayManager.ScreenTop;

        private readonly RunOptions? _modeloptions;
        private InferenceSession? _onnxModel;
        private readonly string _modelPath;
        private bool _usingDirectML;

        private Task? _aiLoopTask;
        private CancellationTokenSource? _aiLoopCancellation;
        private volatile bool _isAiLoopRunning;
        private volatile bool _benchmarkMode;
        private volatile bool _suppressOutputActions;
        private volatile bool _lastPredictionRanInference;
        private readonly SemaphoreSlim _inferenceGate = new(1, 1);
        private bool _disposed;
        private Action? _pendingOverlayUpdate;
        private int _overlayUpdateScheduled;
        private ObservationSession? _observation;
        internal ObservationSession? Observation => _observation;
        internal string ModelPath => _modelPath;

        // For Auto-Labelling Data System
        private bool PlayerFound = false;

        private double CenterXTranslated = 0;
        private double CenterYTranslated = 0;

        // Benchmarking
        private int iterationCount = 0;
        private long totalTime = 0;

        private int detectedX { get; set; }
        private int detectedY { get; set; }

        public double AIConf = 0;
        // Pre-calculated values - now dynamic
        private float _scaleX => ScreenWidth / (float)IMAGE_SIZE;
        private float _scaleY => ScreenHeight / (float)IMAGE_SIZE;

        // Tensor reuse (model inference)
        private DenseTensor<float>? _reusableTensor;
        private float[]? _reusableInputArray;
        private List<NamedOnnxValue>? _reusableInputs;

        // Benchmarking
        private readonly Dictionary<string, BenchmarkData> _benchmarks = new();
        private readonly object _benchmarkLock = new();


        private readonly CaptureManager _captureManager = new();
        private readonly StickyAimSelector _stickyAimSelector = new();
        public Task<bool> InitializationTask { get; }
        public bool IsLoaded => _onnxModel != null && _outputNames != null;
        #endregion Variables

        #region Benchmarking

        private class BenchmarkData
        {
            public long TotalTime { get; set; }
            public int CallCount { get; set; }
            public long MinTime { get; set; } = long.MaxValue;
            public long MaxTime { get; set; }
            public double AverageTime => CallCount > 0 ? (double)TotalTime / CallCount : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IDisposable Benchmark(string name)
        {
            return new BenchmarkScope(this, name);
        }

        private class BenchmarkScope : IDisposable
        {
            private readonly AIManager _manager;
            private readonly string _name;
            private readonly Stopwatch _sw;

            public BenchmarkScope(AIManager manager, string name)
            {
                _manager = manager;
                _name = name;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                _manager.RecordBenchmark(_name, _sw.ElapsedMilliseconds);
            }
        }

        private void RecordBenchmark(string name, long elapsedMs)
        {
            lock (_benchmarkLock)
            {
                if (!_benchmarks.TryGetValue(name, out var data))
                {
                    data = new BenchmarkData();
                    _benchmarks[name] = data;
                }

                data.TotalTime += elapsedMs;
                data.CallCount++;
                data.MinTime = Math.Min(data.MinTime, elapsedMs);
                data.MaxTime = Math.Max(data.MaxTime, elapsedMs);
            }
        }

        public void PrintBenchmarks()
        {
            lock (_benchmarkLock)
            {
                var lines = new List<string>
                {
                    "=== AIManager Performance Benchmarks ==="
                };

                foreach (var kvp in _benchmarks.OrderBy(x => x.Key))
                {
                    var data = kvp.Value;
                    lines.Add($"{kvp.Key}: Avg={data.AverageTime:F2}ms, Min={data.MinTime}ms, Max={data.MaxTime}ms, Count={data.CallCount}");
                }

                lines.Add($"Overall FPS: {(iterationCount > 0 ? 1000.0 / (totalTime / (double)iterationCount) : 0):F2}");

                //File.WriteAllLines("AIManager_Benchmarks.txt", lines);

                Log(LogLevel.Info, string.Join(Environment.NewLine, lines));
            }
        }

        #endregion Benchmarking

        public AIManager(string modelPath)
        {
            _modelPath = modelPath;

            // Initialize the cached image size
            _currentImageSize = AimSettings.ImageSize;

            // Initialize DXGI capture for current display
            if (AimSettings.ScreenCaptureMethod == "DirectX")
            {
                _captureManager.InitializeDxgiDuplication();
            }

            kalmanPrediction = new KalmanPrediction();
            wtfpredictionManager = new WiseTheFoxPrediction();

            _modeloptions = new RunOptions();

            // Attempt to load via DirectML (else fallback to CPU)
            InitializationTask = Task.Run(() => InitializeModel(modelPath));
        }

        #region Models

        private async Task<bool> InitializeModel(string modelPath)
        {
            using (Benchmark("ModelInitialization"))
            {
                try
                {
                    if (!await LoadModelAsync(modelPath, useDirectML: true))
                    {
                        return false;
                    }

                    _usingDirectML = true;
                    return true;
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"Error starting the model via DirectML: {ex.Message}\n\nFalling back to CPU, performance may be poor.", true);

                    try
                    {
                        bool loaded = await LoadModelAsync(modelPath, useDirectML: false);
                        _usingDirectML = loaded ? false : _usingDirectML;
                        return loaded;
                    }
                    catch (Exception e)
                    {
                        Log(LogLevel.Error, $"Error starting the model via CPU: {e.Message}, you won't be able to aim assist at all.", true);
                        return false;
                    }
                }
            }
        }

        private Task<bool> LoadModelAsync(string modelPath, bool useDirectML)
        {
            try
            {
                OnnxModelLoadResult loadedModel = OnnxModelSessionFactory.Load(modelPath, useDirectML);
                _onnxModel = loadedModel.Session;
                _outputNames = loadedModel.OutputNames;

                // Validate the onnx model output shape (ensure model is OnnxV8)
                if (!ValidateOnnxShape())
                {
                    DisposeLoadedModel();
                    return Task.FromResult(false);
                }

                // Pre-allocate bitmap buffer
                _bitmapBuffer = new byte[3 * IMAGE_SIZE * IMAGE_SIZE];
            }
            catch
            {
                DisposeLoadedModel();
                throw;
            }

            LocalAutomationSession.Instance.SetModel(modelPath);
            _isAiLoopRunning = true;
            _aiLoopCancellation?.Dispose();
            _aiLoopCancellation = new CancellationTokenSource();
            CancellationToken loopCancellation = _aiLoopCancellation.Token;
            _aiLoopTask = Task.Factory.StartNew(
                () => AiLoopAsync(loopCancellation),
                loopCancellation,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            return Task.FromResult(true);
        }

        private void DisposeLoadedModel()
        {
            _onnxModel?.Dispose();
            _onnxModel = null;
            _outputNames = null;
        }

        private bool ValidateOnnxShape()
        {
            if (_onnxModel != null)
            {
                var inputMetadata = _onnxModel.InputMetadata;
                var outputMetadata = _onnxModel.OutputMetadata;

                Log(LogLevel.Info, "=== Model Metadata ===");
                Log(LogLevel.Info, "Input Metadata:");

                bool isDynamic = false;
                int fixedInputSize = 0;

                foreach (var kvp in inputMetadata)
                {
                    string dimensionsStr = string.Join("x", kvp.Value.Dimensions);
                    Log(LogLevel.Info, $"  Name: {kvp.Key}, Dimensions: {dimensionsStr}");

                    // Check if model is dynamic (dimensions are -1)
                    if (kvp.Value.Dimensions.Any(d => d == -1))
                    {
                        isDynamic = true;
                    }
                    else if (kvp.Value.Dimensions.Length == 4)
                    {
                        // For fixed models, check if it's the expected format (1x3xHxW)
                        fixedInputSize = kvp.Value.Dimensions[2]; // Height should equal Width for square models
                    }
                }

                Log(LogLevel.Info, "Output Metadata:");
                foreach (var kvp in outputMetadata)
                {
                    string dimensionsStr = string.Join("x", kvp.Value.Dimensions);
                    Log(LogLevel.Info, $"  Name: {kvp.Key}, Dimensions: {dimensionsStr}");
                }

                IsDynamicModel = isDynamic;
                CurrentModelIsDynamic = isDynamic;

                if (IsDynamicModel)
                {
                    // For dynamic models, calculate NUM_DETECTIONS based on selected image size
                    NUM_DETECTIONS = CalculateNumDetections(IMAGE_SIZE);
                    LoadClasses();
                    ImageSizeUpdated?.Invoke(IMAGE_SIZE);
                    Log(LogLevel.Info, $"Loaded dynamic model - using selected image size {IMAGE_SIZE}x{IMAGE_SIZE} with {NUM_DETECTIONS} detections", true, 3000);
                }
                else
                {
                    // For fixed models, auto-adjust image size if needed
                    ModelFixedSize = fixedInputSize;

                    // List of supported sizes
                    var supportedSizes = new[] { "640", "512", "416", "320", "256", "160" };
                    var fixedSizeStr = fixedInputSize.ToString();

                    if (!supportedSizes.Contains(fixedSizeStr))
                    {
                        Log(LogLevel.Error,
                            $"Model requires unsupported size {fixedInputSize}x{fixedInputSize}. Supported sizes are: {string.Join(", ", supportedSizes)}",
                            true, 10000);
                        return false;
                    }

                    // Always calculate NUM_DETECTIONS based on the model's fixed size
                    NUM_DETECTIONS = CalculateNumDetections(fixedInputSize);
                    _currentImageSize = fixedInputSize;

                    if (fixedInputSize != AimSettings.ImageSize)
                    {
                        // Auto-adjust the image size to match the model
                        Log(LogLevel.Warning,
                            $"Fixed-size model expects {fixedInputSize}x{fixedInputSize}. Automatically adjusting Image Size setting.",
                            true, 3000);

                        AimSettings.ImageSize = fixedInputSize;

                        // Update the UI dropdown if it exists
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                // Find the MainWindow and update the dropdown
                                var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                                if (mainWindow?.SettingsMenuControlInstance != null)
                                {
                                    mainWindow.SettingsMenuControlInstance.UpdateImageSizeDropdown(fixedSizeStr);
                                }
                            }
                            catch { }
                        });
                    }

                    ImageSizeUpdated?.Invoke(fixedInputSize);
                    LoadClasses();

                    // For static models, validate the expected shape
                    var expectedShape = new int[] { 1, 4 + NUM_CLASSES, NUM_DETECTIONS };
                    if (!outputMetadata.Values.All(metadata => metadata.Dimensions.SequenceEqual(expectedShape)))
                    {
                        Log(LogLevel.Error,
                            $"Output shape does not match the expected shape of {string.Join("x", expectedShape)}.\nThis model will not work with Aimmy, please use an YOLOv8 model converted to ONNXv8.",
                            true, 10000);
                        return false;
                    }

                    Log(LogLevel.Info, $"Loaded fixed-size model: {fixedInputSize}x{fixedInputSize}", true, 2000);
                }

                // Notify UI about dynamic model status
                DynamicModelStatusChanged?.Invoke(IsDynamicModel);

                return true;
            }

            return false;
        }

        private void LoadClasses()
        {
            if (_onnxModel == null) return;
            _modelClasses.Clear();

            try
            {
                var metadata = _onnxModel.ModelMetadata;

                if (metadata != null &&
                    metadata.CustomMetadataMap.TryGetValue("names", out string? value) &&
                    !string.IsNullOrEmpty(value))
                {
                    JObject data = JObject.Parse(value);
                    if (data != null && data.Type == JTokenType.Object)
                    {
                        //int maxClassId = -1;
                        foreach (var item in data)
                        {
                            if (int.TryParse(item.Key, out int classId) && item.Value.Type == JTokenType.String)
                            {
                                _modelClasses[classId] = item.Value.ToString();
                            }
                        }
                        NUM_CLASSES = _modelClasses.Count > 0 ? _modelClasses.Keys.Max() + 1 : 1;
                        Log(LogLevel.Info, $"Loaded {_modelClasses.Count} class(es) from model metadata: {data.ToString(Newtonsoft.Json.Formatting.None)}", false);
                    }
                    else
                    {
                        Log(LogLevel.Error, "Model metadata 'names' field is not a valid JSON object.", true);
                    }
                }
                else
                {
                    Log(LogLevel.Error, "Model metadata does not contain 'names' field for classes.", true);
                }
                ClassesUpdated?.Invoke(new Dictionary<int, string>(_modelClasses));
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error loading classes: {ex.Message}", true);
            }
        }

        #endregion Models

        #region AI

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldPredict() =>
            AimProcessingDecisions.ShouldRunPrediction(
                AimSettings.ShowDetectedPlayer,
                AimSettings.ConstantAiTracking,
                InputBindingManager.IsHoldingBinding("Aim Keybind"),
                InputBindingManager.IsHoldingBinding("Second Aim Keybind"));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldProcess() =>
            AimProcessingDecisions.ShouldProcessFrame(
                AimSettings.AimAssist,
                AimSettings.ShowDetectedPlayer,
                AimSettings.AutoTrigger);

        private async Task AiLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch
            {
            }

            if (ObservationMode.BlocksOutput)
            {
                _observation = new ObservationSession();
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    Log(LogLevel.Info, "Observation active : aucune correction automatique par défaut. Le mode local et son assistance se contrôlent dans AUTO.", true, 5000)));
            }
            Stopwatch stopwatch = new();
            DetectedPlayerWindow? DetectedPlayerOverlay = Dictionary.DetectedPlayerOverlay;

            try
            {
                while (_isAiLoopRunning && !cancellationToken.IsCancellationRequested)
                {
                    bool sizeChangePending;
                    lock (_sizeLock)
                    {
                        sizeChangePending = _sizeChangePending;
                    }

                    if (_benchmarkMode)
                    {
                        await Task.Delay(10, cancellationToken);
                        continue;
                    }

                    stopwatch.Restart();

                    try
                    {
                        if (sizeChangePending)
                        {
                            await Task.Delay(1, cancellationToken);
                            continue;
                        }

                        _captureManager.HandlePendingDisplayChanges();

                        if (ObservationMode.BlocksOutput && _observation?.IsRecording == false && !LocalAutomationSession.Instance.Active)
                        {
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        using (Benchmark("AILoopIteration"))
                        {
                            UpdateFOV();

                            if (ObservationMode.BlocksOutput || LocalAutomationSession.Instance.Active || ShouldProcess())
                            {
                                if (ObservationMode.BlocksOutput || LocalAutomationSession.Instance.Active || ShouldPredict())
                                {
                                    Prediction? closestPrediction;
                                    using (Benchmark("GetClosestPrediction"))
                                    {
                                        await _inferenceGate.WaitAsync(cancellationToken);
                                        try
                                        {
                                            closestPrediction = await GetClosestPrediction();
                                        }
                                        finally
                                        {
                                            _inferenceGate.Release();
                                        }
                                    }

                                    if (ObservationMode.BlocksOutput || LocalAutomationSession.Instance.Active)
                                    {
                                        UpdateObservationOverlay(closestPrediction);
                                        continue;
                                    }

                                    if (closestPrediction == null)
                                    {
                                        DisableOverlay(DetectedPlayerOverlay!);
                                        continue;
                                    }

                                    using (Benchmark("AutoTrigger"))
                                    {
                                        await AutoTrigger();
                                    }

                                    using (Benchmark("CalculateCoordinates"))
                                    {
                                        CalculateCoordinates(DetectedPlayerOverlay, closestPrediction, _scaleX, _scaleY);
                                    }

                                    using (Benchmark("HandleAim"))
                                    {
                                        HandleAim(closestPrediction);
                                    }

                                    totalTime += stopwatch.ElapsedMilliseconds;
                                    iterationCount++;
                                }
                                else
                                {
                                    await Task.Delay(1, cancellationToken);
                                }
                            }
                            else
                            {
                                await Task.Delay(1, cancellationToken);
                            }
                        }
                    }
                    finally
                    {
                        stopwatch.Stop();
                        await ApplyFpsLimitAsync(stopwatch, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"AI loop stopped: {ex.Message}", true, 5000);
            }
        }

        private static async Task ApplyFpsLimitAsync(Stopwatch iterationStopwatch, CancellationToken cancellationToken)
        {
            int fpsLimit = AimSettings.AiFpsLimit;
            if (fpsLimit <= 0)
                return;

            double targetMilliseconds = 1000.0 / fpsLimit;
            double remainingMilliseconds = targetMilliseconds - iterationStopwatch.Elapsed.TotalMilliseconds;
            if (remainingMilliseconds > 1)
            {
                await Task.Delay((int)Math.Floor(remainingMilliseconds), cancellationToken);
            }
        }

        #region AI Loop Functions

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task AutoTrigger()
        {
            if (_suppressOutputActions)
                return;

            // if auto trigger is disabled,
            // or if the aim keybinds are not held,
            // or if constant AI tracking is enabled,
            // we check for spray release and return
            if (!AimProcessingDecisions.ShouldAttemptAutoTrigger(
                AimSettings.AutoTrigger,
                InputBindingManager.IsHoldingBinding("Aim Keybind"),
                InputBindingManager.IsHoldingBinding("Second Aim Keybind"),
                AimSettings.ConstantAiTracking))
            {
                CheckSprayRelease();
                return;
            }


            if (AimSettings.SprayMode)
            {
                await MouseManager.DoTriggerClick(LastDetectionBox);
                return;
            }


            if (AimSettings.CursorCheck)
            {
                var mousePos = WinAPICaller.GetCursorPosition();

                if (!DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePos.X, mousePos.Y)))
                {
                    return;
                }

                if (LastDetectionBox.Contains(mousePos.X, mousePos.Y))
                {
                    await MouseManager.DoTriggerClick(LastDetectionBox);
                }
            }
            else
            {
                await MouseManager.DoTriggerClick();
            }

            if (!AimSettings.AimAssist || !AimSettings.ShowDetectedPlayer) return;

        }
        private void CheckSprayRelease()
        {
            if (!AimSettings.SprayMode) return;

            // if auto trigger is disabled, we reset the spray state
            // if the aim keybinds are not held, we reset the spray state
            if (!AimProcessingDecisions.ShouldKeepSprayActive(
                AimSettings.AutoTrigger,
                InputBindingManager.IsHoldingBinding("Aim Keybind"),
                InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                MouseManager.ResetSprayState();
            }
        }

        private async void UpdateFOV()
        {
            if (AimSettings.DetectionAreaType == "Closest to Mouse" && AimSettings.ShowFov)
            {
                var mousePosition = WinAPICaller.GetCursorPosition();

                // Check if mouse is on the current display
                if (!DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePosition.X, mousePosition.Y)))
                {
                    // Mouse is on a different display - don't update FOV position
                    return;
                }

                // Translate mouse position relative to current display
                var displayRelativeX = mousePosition.X - DisplayManager.ScreenLeft;
                var displayRelativeY = mousePosition.Y - DisplayManager.ScreenTop;

                await Application.Current.Dispatcher.BeginInvoke(() =>
                    Dictionary.FOVWindow.FOVStrictEnclosure.Margin = new Thickness(
                        Convert.ToInt16(displayRelativeX / WinAPICaller.scalingFactorX) - 320, // this is based off the window size, not the size of the model -whip
                        Convert.ToInt16(displayRelativeY / WinAPICaller.scalingFactorY) - 320, 0, 0));
            }
        }

        // Keep at most one queued dispatcher callback and replace its payload with the latest frame.
        // A synchronous dispatcher wait here can deadlock UI-thread model disposal or app shutdown.
        private void QueueOverlayUpdate(DetectedPlayerWindow overlay, Action update)
        {
            if (Volatile.Read(ref _disposed)) return;
            Interlocked.Exchange(ref _pendingOverlayUpdate, () =>
            {
                if (!Volatile.Read(ref _disposed) && overlay.IsLoaded &&
                    ReferenceEquals(Dictionary.DetectedPlayerOverlay, overlay)) update();
            });
            ScheduleOverlayUpdate();
        }

        private void ScheduleOverlayUpdate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (Volatile.Read(ref _disposed) || dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            { Interlocked.Exchange(ref _pendingOverlayUpdate, null); return; }
            if (Interlocked.CompareExchange(ref _overlayUpdateScheduled, 1, 0) != 0) return;
            try
            {
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    try { Interlocked.Exchange(ref _pendingOverlayUpdate, null)?.Invoke(); }
                    finally
                    {
                        Volatile.Write(ref _overlayUpdateScheduled, 0);
                        if (Volatile.Read(ref _pendingOverlayUpdate) != null) ScheduleOverlayUpdate();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _pendingOverlayUpdate, null);
                Volatile.Write(ref _overlayUpdateScheduled, 0);
            }
        }

        private void DisableOverlay(DetectedPlayerWindow DetectedPlayerOverlay)
        {
            if (AimSettings.ShowDetectedPlayer && Dictionary.DetectedPlayerOverlay != null)
            {
                QueueOverlayUpdate(DetectedPlayerOverlay, () =>
                {
                    DetectedPlayerOverlay!.DetectedPlayerConfidence.Opacity = 0;
                    DetectedPlayerOverlay!.DetectedTracers.Opacity = 0;
                    DetectedPlayerOverlay!.DetectedPlayerFocus.Opacity = 0;
                });
            }
        }

        private void UpdateOverlay(DetectedPlayerWindow DetectedPlayerOverlay, Prediction closestPrediction)
        {
            var scalingFactorX = WinAPICaller.scalingFactorX;
            var scalingFactorY = WinAPICaller.scalingFactorY;
            var detectionBox = LastDetectionBox;
            var confidence = closestPrediction.Confidence;
            var className = closestPrediction.ClassName;
            var screenHorizontalCenter = DisplayManager.ScreenWidth / (2.0 * scalingFactorX);

            // Convert screen coordinates to display-relative coordinates
            var displayRelativeX = detectionBox.X - DisplayManager.ScreenLeft;
            var displayRelativeY = detectionBox.Y - DisplayManager.ScreenTop;

            // Calculate center position in display-relative coordinates
            var centerX = Convert.ToInt16(displayRelativeX / scalingFactorX) + (detectionBox.Width / 2.0);
            var centerY = Convert.ToInt16(displayRelativeY / scalingFactorY);

            QueueOverlayUpdate(DetectedPlayerOverlay, () =>
            {
                if (!AimSettings.ShowDetectedPlayer) { DetectedPlayerOverlay.DetectedPlayerFocus.Opacity = 0; return; }
                if (AimSettings.ShowAiConfidence)
                {
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 1;
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Content = $"{className}: {Math.Round((confidence * 100), 2)}%";

                    var labelEstimatedHalfWidth = DetectedPlayerOverlay.DetectedPlayerConfidence.ActualWidth / 2.0;
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Margin = new Thickness(
                        centerX - labelEstimatedHalfWidth,
                        centerY - DetectedPlayerOverlay.DetectedPlayerConfidence.ActualHeight - 2, 0, 0);
                }
                var showTracers = AimSettings.ShowTracers;
                DetectedPlayerOverlay.DetectedTracers.Opacity = showTracers ? 1 : 0;
                if (showTracers)
                {
                    var tracerPosition = AimSettings.TracerPosition;

                    var boxTop = centerY;
                    var boxBottom = centerY + detectionBox.Height;
                    var boxHorizontalCenter = centerX;
                    var boxVerticalCenter = centerY + (detectionBox.Height / 2.0);
                    var boxLeft = centerX - (detectionBox.Width / 2.0);
                    var boxRight = centerX + (detectionBox.Width / 2.0);

                    switch (tracerPosition)
                    {
                        case "Top":
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxTop;
                            break;

                        case "Bottom":
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxBottom;
                            break;

                        case "Middle":
                            if (boxHorizontalCenter < screenHorizontalCenter)
                            {
                                // if the box is on the left half of the screen, aim for the right-middle of the box
                                DetectedPlayerOverlay.DetectedTracers.X2 = boxRight;
                                DetectedPlayerOverlay.DetectedTracers.Y2 = boxVerticalCenter;
                            }
                            else
                            {
                                // if the box is on the right half, aim for the left-middle
                                DetectedPlayerOverlay.DetectedTracers.X2 = boxLeft;
                                DetectedPlayerOverlay.DetectedTracers.Y2 = boxVerticalCenter;
                            }
                            break;

                        default:
                            // default to the bottom-center if the setting is unrecognized
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxBottom;
                            break;
                    }
                }

                DetectedPlayerOverlay.Opacity = AimSettings.OverlayOpacity;

                DetectedPlayerOverlay.DetectedPlayerFocus.Opacity = 1;
                DetectedPlayerOverlay.DetectedPlayerFocus.Margin = new Thickness(
                    centerX - (detectionBox.Width / 2.0), centerY, 0, 0);
                DetectedPlayerOverlay.DetectedPlayerFocus.Width = detectionBox.Width;
                DetectedPlayerOverlay.DetectedPlayerFocus.Height = detectionBox.Height;
            });
        }

        // Visual validation only; never calls aim coordinates or mouse output.
        private void UpdateObservationOverlay(Prediction? prediction)
        {
            var overlay = Dictionary.DetectedPlayerOverlay;
            if (overlay == null || !AimSettings.ShowDetectedPlayer) return;
            bool hasPrediction = prediction != null;
            double sx = WinAPICaller.scalingFactorX, sy = WinAPICaller.scalingFactorY;
            double width = prediction?.Rectangle.Width ?? 0, height = prediction?.Rectangle.Height ?? 0;
            double left = ((prediction?.ScreenCenterX ?? 0) - width / 2 - DisplayManager.ScreenLeft) / sx;
            double top = ((prediction?.ScreenCenterY ?? 0) - height / 2 - DisplayManager.ScreenTop) / sy;
            QueueOverlayUpdate(overlay, () =>
            {
                overlay.DetectedTracers.Opacity = 0;
                overlay.DetectedPlayerConfidence.Opacity = 0;
                overlay.DetectedPlayerFocus.Opacity = !hasPrediction || !AimSettings.ShowDetectedPlayer ? 0 : 1;
                if (!hasPrediction || !AimSettings.ShowDetectedPlayer) return;
                overlay.Opacity = AimSettings.OverlayOpacity;
                overlay.DetectedPlayerFocus.Margin = new Thickness(left, top, 0, 0);
                overlay.DetectedPlayerFocus.Width = width / sx;
                overlay.DetectedPlayerFocus.Height = height / sy;
            });
        }

        private void CalculateCoordinates(DetectedPlayerWindow DetectedPlayerOverlay, Prediction closestPrediction, float scaleX, float scaleY)
        {
            AIConf = closestPrediction.Confidence;

            if (AimSettings.ShowDetectedPlayer && Dictionary.DetectedPlayerOverlay != null)
            {
                using (Benchmark("UpdateOverlay"))
                {
                    UpdateOverlay(DetectedPlayerOverlay!, closestPrediction);
                }
                if (!AimSettings.AimAssist) return;
            }

            double YOffset = AimSettings.YOffset;
            double XOffset = AimSettings.XOffset;

            double YOffsetPercentage = AimSettings.YOffsetPercent;
            double XOffsetPercentage = AimSettings.XOffsetPercent;

            var rect = closestPrediction.Rectangle;

            if (AimSettings.UseXAxisPercentageAdjustment)
            {
                detectedX = (int)((rect.X + (rect.Width * (XOffsetPercentage / 100))) * scaleX);
            }
            else
            {
                detectedX = (int)((rect.X + rect.Width / 2) * scaleX + XOffset);
            }

            if (AimSettings.UseYAxisPercentageAdjustment)
            {
                detectedY = (int)((rect.Y + rect.Height - (rect.Height * (YOffsetPercentage / 100))) * scaleY + YOffset);
            }
            else
            {
                detectedY = CalculateDetectedY(scaleY, YOffset, closestPrediction);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CalculateDetectedY(float scaleY, double YOffset, Prediction closestPrediction)
        {
            var rect = closestPrediction.Rectangle;
            float yBase = rect.Y;
            float yAdjustment = 0;

            switch (AimSettings.AimingBoundariesAlignment)
            {
                case "Center":
                    yAdjustment = rect.Height / 2;
                    break;

                case "Top":
                    // yBase is already at the top
                    break;

                case "Bottom":
                    yAdjustment = rect.Height;
                    break;
            }

            return (int)((yBase + yAdjustment) * scaleY + YOffset);
        }

        private void HandleAim(Prediction closestPrediction)
        {
            if (_suppressOutputActions)
                return;

            if (AimSettings.AimAssist &&
                (AimSettings.ConstantAiTracking ||
                 AimSettings.AimAssist && InputBindingManager.IsHoldingBinding("Aim Keybind") ||
                 AimSettings.AimAssist && InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                if (AimSettings.Predictions)
                {
                    HandlePredictions(kalmanPrediction, closestPrediction, detectedX, detectedY);
                }
                else
                {
                    MouseManager.MoveCrosshair(detectedX, detectedY);
                }
            }
        }

        private void HandlePredictions(KalmanPrediction kalmanPrediction, Prediction closestPrediction, int detectedX, int detectedY)
        {
            var predictionMethod = AimSettings.PredictionMethod;
            switch (predictionMethod)
            {
                case "Kalman Filter":
                    KalmanPrediction.Detection detection = new()
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = DateTime.UtcNow
                    };

                    kalmanPrediction.UpdateKalmanFilter(detection);
                    var predictedPosition = kalmanPrediction.GetKalmanPosition();

                    MouseManager.MoveCrosshair(predictedPosition.X, predictedPosition.Y);
                    break;

                case "Shall0e's Prediction":
                    // Update position (calculates velocity internally)
                    ShalloePredictionV2.UpdatePosition(detectedX, detectedY);

                    // Get predicted position
                    MouseManager.MoveCrosshair(ShalloePredictionV2.GetSPX(), ShalloePredictionV2.GetSPY());
                    break;

                case "wisethef0x's EMA Prediction":
                    WiseTheFoxPrediction.WTFDetection wtfdetection = new()
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = DateTime.UtcNow
                    };

                    wtfpredictionManager.UpdateDetection(wtfdetection);
                    var wtfpredictedPosition = wtfpredictionManager.GetEstimatedPosition();

                    // Use both predicted X and Y
                    MouseManager.MoveCrosshair(wtfpredictedPosition.X, wtfpredictedPosition.Y);
                    break;
            }
        }

        private async Task<Prediction?> GetClosestPrediction(bool useMousePosition = true)
        {
            _lastPredictionRanInference = false;
            bool localActive = !_benchmarkMode && LocalAutomationSession.Instance.Active;
            R6ForegroundSnapshot? foreground = null;
            if (localActive)
            {
                if (!R6ForegroundGuard.TryGet(out var game))
                {
                    LocalAutomationSession.Instance.PauseFrame("En pause : remettez R6 au premier plan.");
                    _observation?.UpdateTargets(Array.Empty<Prediction>());
                    return null;
                }
                foreground = game;
            }
            Rectangle detectionBox = CreateDetectionBox(useMousePosition);
            if (localActive && foreground != null && !foreground.ClientBounds.Contains(detectionBox))
            {
                LocalAutomationSession.Instance.PauseFrame("Capture hors de la fenêtre de R6 : sélectionnez son moniteur et agrandissez le jeu.");
                _observation?.UpdateTargets(Array.Empty<Prediction>());
                return null;
            }

            Bitmap? frame;

            using (Benchmark("ScreenGrab"))
            {
                frame = _captureManager.ScreenGrab(detectionBox, allowStaleCache: _benchmarkMode, requireFresh: localActive);
            }

            if (frame == null) return null;
            DateTime capturedUtc = DateTime.UtcNow;
            double capturedSeconds = LocalAutomationSession.Instance.Timestamp;
            long rawX = 0, rawY = 0;
            bool rawAvailable = localActive && RawMouseObserver.Shared.TryRead(out rawX, out rawY, out _);

            IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
            Tensor<float>? outputTensor = null;

            try
            {
                float[] inputArray;
                using (Benchmark("BitmapToFloatArray"))
                {
                    if (_reusableInputArray == null || _reusableInputArray.Length != 3 * IMAGE_SIZE * IMAGE_SIZE)
                    {
                        _reusableInputArray = new float[3 * IMAGE_SIZE * IMAGE_SIZE];
                    }
                    inputArray = _reusableInputArray;

                    // Fill the reusable array
                    BitmapToFloatArrayInPlace(frame, inputArray, IMAGE_SIZE);
                }

                // Reuse tensor and inputs - recreate if size changed
                /// this needs to be revised !!!!! - taylor
                if (_reusableTensor == null || _reusableTensor.Dimensions[2] != IMAGE_SIZE)
                {
                    _reusableTensor = new DenseTensor<float>(inputArray, new int[] { 1, 3, IMAGE_SIZE, IMAGE_SIZE });
                    _reusableInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", _reusableTensor) };
                }
                else
                {
                    // Directly copy into existing DenseTensor buffer
                    inputArray.AsSpan().CopyTo(_reusableTensor.Buffer.Span);
                }

                if (_onnxModel == null) return null;
                using (Benchmark("ModelInference"))
                {
                    results = _onnxModel.Run(_reusableInputs, _outputNames, _modeloptions);
                    _lastPredictionRanInference = true;
                    outputTensor = results[0].AsTensor<float>();
                }

                if (outputTensor == null)
                {
                    Log(LogLevel.Error, "Model inference returned null output tensor.", true, 2000);
                    if (!localActive) SaveFrame(frame);
                    return null;
                }

                // Calculate the FOV boundaries
                float FovSize = localActive ? IMAGE_SIZE : (float)AimSettings.FovSize;
                float fovMinX = (IMAGE_SIZE - FovSize) / 2.0f;
                float fovMaxX = (IMAGE_SIZE + FovSize) / 2.0f;
                float fovMinY = (IMAGE_SIZE - FovSize) / 2.0f;
                float fovMaxY = (IMAGE_SIZE + FovSize) / 2.0f;

                //List<double[]> KDpoints;
                List<Prediction> KDPredictions;
                using (Benchmark("PrepareKDTreeData"))
                {
                    float minConfidence = localActive ? .10f : AimSettings.MinimumConfidence;
                    string selectedClass = AimSettings.TargetClass;

                    KDPredictions = PredictionFilter.CreatePredictions(
                        outputTensor,
                        detectionBox,
                        IMAGE_SIZE,
                        NUM_DETECTIONS,
                        NUM_CLASSES,
                        _modelClasses,
                        minConfidence,
                        selectedClass,
                        fovMinX,
                        fovMaxX,
                        fovMinY,
                        fovMaxY);
                    if (localActive) KDPredictions = DetectionPostProcessor.SuppressDuplicates(KDPredictions);
                }

                if (localActive && foreground != null)
                {
                    if (!R6ForegroundGuard.StillMatches(foreground))
                    {
                        LocalAutomationSession.Instance.PauseFrame("Capture interrompue : R6 a quitté le premier plan.");
                        _observation?.UpdateTargets(Array.Empty<Prediction>());
                        return null;
                    }
                    _observation?.UpdateTargets(KDPredictions);
                    return LocalAutomationSession.Instance.ProcessFrame(frame, detectionBox, KDPredictions,
                        capturedUtc, capturedSeconds, foreground, rawX, rawY, rawAvailable);
                }
                if (ObservationMode.BlocksOutput && !_benchmarkMode)
                {
                    return _observation?.UpdateTargets(KDPredictions);
                }

                if (KDPredictions.Count == 0)
                {
                    SaveFrame(frame);
                    return null;
                }

                //kdtree was replaced with linear search
                Prediction? bestCandidate = null;
                double bestDistSq = double.MaxValue;
                double center = IMAGE_SIZE / 2.0;

                // TODO: Optimize this linear search further if needed
                // TODO: Consider updating KD-Tree and adding options to switch from linear to kd.
                // we can honestly replacing linear search by letting sticky aim handle the search
                using (Benchmark("LinearSearch"))
                {
                    foreach (var p in KDPredictions)
                    {
                        var dx = p.CenterXTranslated * IMAGE_SIZE - center;
                        var dy = p.CenterYTranslated * IMAGE_SIZE - center;
                        double d2 = dx * dx + dy * dy; // dx^2 + dy^2

                        if (d2 < bestDistSq) { bestDistSq = d2; bestCandidate = p; }
                    }
                }

                if (_benchmarkMode)
                {
                    return bestCandidate;
                }

                Prediction? finalTarget = _stickyAimSelector.SelectTarget(
                    AimSettings.StickyAim,
                    (float)AimSettings.StickyAimThreshold,
                    IMAGE_SIZE,
                    bestCandidate,
                    KDPredictions);
                if (finalTarget != null)
                {
                    UpdateDetectionBox(finalTarget, detectionBox);
                    SaveFrame(frame, finalTarget);
                    return finalTarget;
                }

                return null;
            }
            finally
            {
                // Always dispose the cloned frame to prevent memory leaks
                frame.Dispose();
                results?.Dispose();
            }
        }

        private Rectangle CreateDetectionBox(bool useMousePosition = true)
        {
            string detectionAreaType = LocalAutomationSession.Instance.Active || (ObservationMode.BlocksOutput && ObservationMode.Options.AimReference == AimReference.ScreenCenter)
                ? "Closest to Center Screen" : AimSettings.DetectionAreaType;
            System.Drawing.Point mousePosition = default;
            bool mouseOnCurrentDisplay = false;

            if (useMousePosition && detectionAreaType == "Closest to Mouse")
            {
                mousePosition = WinAPICaller.GetCursorPosition();
                mouseOnCurrentDisplay = DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePosition.X, mousePosition.Y));
            }

            var displayBounds = new Rectangle(
                DisplayManager.ScreenLeft,
                DisplayManager.ScreenTop,
                DisplayManager.ScreenWidth,
                DisplayManager.ScreenHeight);

            return CaptureTargetSelector.SelectDetectionBox(
                detectionAreaType,
                IMAGE_SIZE,
                displayBounds,
                mousePosition,
                mouseOnCurrentDisplay);
        }

        private void UpdateDetectionBox(Prediction target, Rectangle detectionBox)
        {
            float translatedXMin = target.Rectangle.X + detectionBox.Left;
            float translatedYMin = target.Rectangle.Y + detectionBox.Top;
            LastDetectionBox = new(translatedXMin, translatedYMin,
                target.Rectangle.Width, target.Rectangle.Height);

            CenterXTranslated = target.CenterXTranslated;
            CenterYTranslated = target.CenterYTranslated;
        }

        public async Task<PerformanceBenchmarkReport> RunPerformanceBenchmarkAsync(
            IProgress<PerformanceBenchmarkProgress>? progress = null,
            CancellationToken cancellationToken = default,
            PerformanceGoal goal = PerformanceGoal.Balanced)
        {
            if (!IsLoaded)
                throw new InvalidOperationException("Load a model before running the performance helper.");

            int originalImageSize = IMAGE_SIZE;
            int originalDetections = NUM_DETECTIONS;
            InferenceSession? originalSession = _onnxModel;
            List<string>? originalOutputNames = _outputNames;
            bool originalUsingDirectML = _usingDirectML;
            bool originalSizeChangePending;

            lock (_sizeLock)
            {
                originalSizeChangePending = _sizeChangePending;
                _sizeChangePending = false;
            }

            int[] supportedSizes = [640, 512, 416, 320, 256, 160];
            int[] sizes = IsDynamicModel
                ? supportedSizes
                    .OrderBy(size => size == originalImageSize ? 0 : 1)
                    .ThenByDescending(size => size)
                    .ToArray()
                : [ModelFixedSize];

            var results = new List<PerformanceBenchmarkSizeResult>(sizes.Length);
            _benchmarkMode = true;
            _suppressOutputActions = true;
            bool gateEntered = false;
            Exception? benchmarkException = null;
            Exception? restoreException = null;

            try
            {
                await Task.Delay(80, cancellationToken);
                await _inferenceGate.WaitAsync(cancellationToken);
                gateEntered = true;
                _stickyAimSelector.Reset();

                for (int i = 0; i < sizes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int size = sizes[i];

                    progress?.Report(new PerformanceBenchmarkProgress(
                        i + 1,
                        sizes.Length,
                        size,
                        $"Preparing {size}px model"));

                    ConfigureBenchmarkImageSize(size);
                    if (i > 0 || size != originalImageSize)
                    {
                        ReloadBenchmarkModelSession(originalSession);
                    }

                    progress?.Report(new PerformanceBenchmarkProgress(
                        i + 1,
                        sizes.Length,
                        size,
                        $"Warming up {size}px"));

                    await RunBenchmarkWarmupAsync(
                        cancellationToken,
                        progress,
                        i + 1,
                        sizes.Length,
                        size);

                    progress?.Report(new PerformanceBenchmarkProgress(
                        i + 1,
                        sizes.Length,
                        size,
                        $"Testing {size}px"));

                    PerformanceBenchmarkSizeResult result = await RunBenchmarkSampleAsync(
                        BenchmarkSampleDuration,
                        true,
                        cancellationToken,
                        progress,
                        i + 1,
                        sizes.Length,
                        size,
                        "Testing");

                    results.Add(result);
                }
            }
            catch (Exception ex)
            {
                benchmarkException = ex;
            }
            finally
            {
                try
                {
                    if (gateEntered)
                    {
                        RestoreBenchmarkState(
                            originalImageSize,
                            originalDetections,
                            originalSession,
                            originalOutputNames,
                            originalUsingDirectML);
                    }
                }
                catch (Exception ex)
                {
                    restoreException = ex;
                    Log(LogLevel.Error, $"Performance helper could not restore the model session: {ex.Message}", true, 5000);
                }
                finally
                {
                    _stickyAimSelector.Reset();
                    _suppressOutputActions = false;
                    _benchmarkMode = false;

                    if (gateEntered)
                    {
                        _inferenceGate.Release();
                    }

                    lock (_sizeLock)
                    {
                        _sizeChangePending = _sizeChangePending || originalSizeChangePending;
                    }
                }
            }

            ThrowIfBenchmarkFailed(benchmarkException, restoreException);

            var recommendations = PerformanceRecommendationBuilder.BuildChoices(
                results,
                originalImageSize,
                !IsDynamicModel,
                goal);
            var recommendation = recommendations.Primary;

            progress?.Report(new PerformanceBenchmarkProgress(
                sizes.Length,
                sizes.Length,
                recommendation.SuggestedImageSize,
                "Complete"));

            return new PerformanceBenchmarkReport(results, recommendation, !IsDynamicModel, recommendations, goal);
        }

        private static void ThrowIfBenchmarkFailed(Exception? benchmarkException, Exception? restoreException)
        {
            if (restoreException != null)
            {
                throw new InvalidOperationException(
                    "Performance helper could not restore the model session. Reload the model before applying settings.",
                    restoreException);
            }

            if (benchmarkException != null)
            {
                ExceptionDispatchInfo.Capture(benchmarkException).Throw();
            }
        }

        private async Task RunBenchmarkWarmupAsync(
            CancellationToken cancellationToken,
            IProgress<PerformanceBenchmarkProgress>? progress,
            int stepIndex,
            int totalSteps,
            int imageSize)
        {
            var warmupStopwatch = Stopwatch.StartNew();
            var progressStopwatch = Stopwatch.StartNew();
            var recentFrameTimes = new Queue<double>(BenchmarkStabilityWindow);
            int inferenceCount = 0;

            while (warmupStopwatch.Elapsed < BenchmarkWarmupMaximumDuration)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frameStopwatch = Stopwatch.StartNew();
                await GetClosestPrediction(useMousePosition: false);
                frameStopwatch.Stop();

                if (_lastPredictionRanInference)
                {
                    inferenceCount++;
                    recentFrameTimes.Enqueue(frameStopwatch.Elapsed.TotalMilliseconds);
                    if (recentFrameTimes.Count > BenchmarkStabilityWindow)
                    {
                        recentFrameTimes.Dequeue();
                    }
                }

                if (progress != null && progressStopwatch.ElapsedMilliseconds >= 250)
                {
                    double requiredProgress = Math.Min(
                        warmupStopwatch.Elapsed.TotalMilliseconds / BenchmarkWarmupMinimumDuration.TotalMilliseconds,
                        inferenceCount / (double)BenchmarkWarmupMinimumInferences);
                    double maxProgress = warmupStopwatch.Elapsed.TotalMilliseconds / BenchmarkWarmupMaximumDuration.TotalMilliseconds;
                    int secondsLeft = Math.Max(0, (int)Math.Ceiling((BenchmarkWarmupMinimumDuration - warmupStopwatch.Elapsed).TotalSeconds));

                    progress.Report(new PerformanceBenchmarkProgress(
                        stepIndex,
                        totalSteps,
                        imageSize,
                        $"Warming up {imageSize}px ({secondsLeft}s minimum, {inferenceCount} frames)",
                        Math.Clamp(Math.Max(requiredProgress, maxProgress), 0, 1)));
                    progressStopwatch.Restart();
                }

                if (warmupStopwatch.Elapsed >= BenchmarkWarmupMinimumDuration &&
                    inferenceCount >= BenchmarkWarmupMinimumInferences &&
                    IsBenchmarkWarmupStable(recentFrameTimes))
                {
                    break;
                }
            }
        }

        private static bool IsBenchmarkWarmupStable(IReadOnlyCollection<double> recentFrameTimes)
        {
            if (recentFrameTimes.Count < BenchmarkStabilityWindow)
                return false;

            double average = recentFrameTimes.Average();
            double max = recentFrameTimes.Max();
            return max <= Math.Max(100, average * 2.5);
        }

        private void ConfigureBenchmarkImageSize(int imageSize)
        {
            _currentImageSize = imageSize;
            NUM_DETECTIONS = CalculateNumDetections(imageSize);
            _bitmapBuffer = new byte[3 * imageSize * imageSize];
            _reusableInputArray = null;
            _reusableTensor = null;
            _reusableInputs = null;
        }

        private void RestoreBenchmarkState(
            int originalImageSize,
            int originalDetections,
            InferenceSession? originalSession,
            List<string>? originalOutputNames,
            bool originalUsingDirectML)
        {
            InferenceSession? benchmarkSession = _onnxModel;
            ConfigureBenchmarkImageSize(originalImageSize);
            _onnxModel = originalSession;
            _outputNames = originalOutputNames;
            _usingDirectML = originalUsingDirectML;
            NUM_DETECTIONS = originalDetections;

            if (!ReferenceEquals(benchmarkSession, originalSession))
            {
                benchmarkSession?.Dispose();
            }

            if (_onnxModel == null || _outputNames == null)
            {
                throw new InvalidOperationException("Original model session was unavailable after benchmark.");
            }
        }

        private void ReloadBenchmarkModelSession(InferenceSession? preservedSession)
        {
            InferenceSession? previousSession = _onnxModel;
            OnnxModelLoadResult loadedModel = OnnxModelSessionFactory.Load(_modelPath, _usingDirectML);
            _onnxModel = loadedModel.Session;
            _outputNames = loadedModel.OutputNames;

            if (!ReferenceEquals(previousSession, preservedSession))
            {
                previousSession?.Dispose();
            }
        }

        private async Task<PerformanceBenchmarkSizeResult> RunBenchmarkSampleAsync(
            TimeSpan duration,
            bool collectMetrics,
            CancellationToken cancellationToken,
            IProgress<PerformanceBenchmarkProgress>? progress = null,
            int stepIndex = 0,
            int totalSteps = 0,
            int imageSize = 0,
            string phase = "")
        {
            using var sampler = new ResourceUsageSampler();
            var sampleStopwatch = Stopwatch.StartNew();
            var peakWindowStopwatch = Stopwatch.StartNew();
            var progressStopwatch = Stopwatch.StartNew();
            int frameCount = 0;
            int peakWindowFrameCount = 0;
            double maxFps = 0;

            if (collectMetrics)
            {
                sampler.Start();
            }

            while (sampleStopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameStopwatch = Stopwatch.StartNew();
                await GetClosestPrediction(useMousePosition: false);
                frameStopwatch.Stop();
                bool ranInference = _lastPredictionRanInference;

                if (progress != null &&
                    progressStopwatch.ElapsedMilliseconds >= 250 &&
                    totalSteps > 0)
                {
                    double stepProgress = Math.Clamp(sampleStopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                    int secondsLeft = Math.Max(0, (int)Math.Ceiling((duration - sampleStopwatch.Elapsed).TotalSeconds));
                    progress.Report(new PerformanceBenchmarkProgress(
                        stepIndex,
                        totalSteps,
                        imageSize,
                        $"{phase} {imageSize}px ({secondsLeft}s left)",
                        stepProgress));
                    progressStopwatch.Restart();
                }

                if (!collectMetrics)
                    continue;

                if (!ranInference)
                    continue;

                frameCount++;
                peakWindowFrameCount++;

                if (peakWindowStopwatch.ElapsedMilliseconds >= 500)
                {
                    maxFps = Math.Max(maxFps, peakWindowFrameCount / peakWindowStopwatch.Elapsed.TotalSeconds);
                    peakWindowFrameCount = 0;
                    peakWindowStopwatch.Restart();
                }
            }

            if (!collectMetrics)
            {
                return new PerformanceBenchmarkSizeResult(
                    IMAGE_SIZE,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    0);
            }

            TimeSpan sampleElapsed = sampleStopwatch.Elapsed;
            TimeSpan peakWindowElapsed = peakWindowStopwatch.Elapsed;
            sampleStopwatch.Stop();
            peakWindowStopwatch.Stop();

            ResourceUsageSummary resourceUsage = await sampler.FinishAsync();
            double averageFps = CalculateBenchmarkAverageFps(frameCount, sampleElapsed);
            if (peakWindowFrameCount > 0 && peakWindowElapsed.TotalSeconds > 0)
            {
                maxFps = Math.Max(maxFps, peakWindowFrameCount / peakWindowElapsed.TotalSeconds);
            }

            return new PerformanceBenchmarkSizeResult(
                IMAGE_SIZE,
                averageFps,
                Math.Max(maxFps, averageFps),
                resourceUsage.AverageCpuPercent,
                resourceUsage.PeakCpuPercent,
                resourceUsage.AverageGpuPercent,
                resourceUsage.PeakGpuPercent,
                resourceUsage.GpuAvailable,
                frameCount);
        }

        internal static double CalculateBenchmarkAverageFps(int frameCount, TimeSpan sampleElapsed)
        {
            return sampleElapsed.TotalSeconds > 0
                ? frameCount / sampleElapsed.TotalSeconds
                : 0;
        }

        #endregion AI Loop Functions

        #endregion AI

        #region Screen Capture

        private void SaveFrame(Bitmap frame, Prediction? DoLabel = null)
        {
            if (ObservationMode.BlocksOutput) return;
            if (_suppressOutputActions)
                return;

            // Only save frames if "Collect Data While Playing" is enabled
            if (!AimSettings.CollectDataWhilePlaying) return;

            // Skip if we're in constant tracking mode (unless auto-labeling is enabled)
            if (AimSettings.ConstantAiTracking && !AimSettings.AutoLabelData) return;

            // Cooldown check
            if ((DateTime.Now - lastSavedTime).TotalMilliseconds < SAVE_FRAME_COOLDOWN_MS) return;

            try
            {
                // Validate bitmap is still usable
                if (frame == null) return;

                // Accessing Width/Height will throw if bitmap is disposed
                int width = frame.Width;
                int height = frame.Height;
                if (width <= 0 || height <= 0) return;

                lastSavedTime = DateTime.Now;
                string uuid = Guid.NewGuid().ToString();
                string imagePath = Path.Combine("bin", "images", $"{uuid}.jpg");

                // Save synchronously to avoid "Object is currently in use elsewhere" error
                frame.Save(imagePath, ImageFormat.Jpeg);

                if (AimSettings.AutoLabelData && DoLabel != null)
                {
                    var labelPath = Path.Combine("bin", "labels", $"{uuid}.txt");

                    float x = (DoLabel!.Rectangle.X + DoLabel.Rectangle.Width / 2) / width;
                    float y = (DoLabel!.Rectangle.Y + DoLabel.Rectangle.Height / 2) / height;
                    float labelWidth = DoLabel.Rectangle.Width / width;
                    float labelHeight = DoLabel.Rectangle.Height / height;

                    File.WriteAllText(labelPath, $"{DoLabel.ClassId} {x} {y} {labelWidth} {labelHeight}");
                }
            }
            catch (ArgumentException)
            {
                // Bitmap was disposed or invalid - silently ignore
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"SaveFrame failed: {ex.Message}");
            }
        }



        #endregion Screen Capture

        private void StopAiLoop()
        {
            _isAiLoopRunning = false;
            _aiLoopCancellation?.Cancel();

            try
            {
                Task? loopTask = _aiLoopTask;
                if (loopTask != null && !loopTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    Log(LogLevel.Warning, "AI loop is still stopping; waiting before disposing model resources.");
                    loopTask.Wait();
                }
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
            {
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"AI loop did not stop cleanly: {ex.Message}");
            }
            finally
            {
                _aiLoopCancellation?.Dispose();
                _aiLoopCancellation = null;
                _aiLoopTask = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // Signal that we're shutting down
            lock (_sizeLock)
            {
                _sizeChangePending = true;
            }

            StopAiLoop();
            try { _observation?.Dispose(); }
            catch (Exception ex) { Log(LogLevel.Error, $"Observation export failed: {ex.Message}"); }

            // Print final benchmarks
            PrintBenchmarks();

            // Dispose DXGI objects
            _captureManager.Dispose();

            // Clean up other resources
            _reusableInputArray = null;
            _reusableInputs = null;
            _onnxModel?.Dispose();
            _modeloptions?.Dispose();
            _inferenceGate.Dispose();
            _bitmapBuffer = null;
        }
    }
}
