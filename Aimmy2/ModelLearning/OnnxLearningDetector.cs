using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Aimmy2.ModelLearning;

/// <summary>CPU evaluator for Aimmy's existing YOLOv8 RGB, square-image, raw-output inference contract.</summary>
public sealed class OnnxLearningDetector : ILocalDetector
{
    private readonly InferenceSession _session;
    private readonly int _imageSize;
    public int ClassCount { get; }
    public string[] ClassNames { get; }

    public OnnxLearningDetector(string modelPath, int dynamicImageSize = 640)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.AppendExecutionProvider_CPU();
        _session = new InferenceSession(modelPath, options);
        try
        {
            if (_session.InputMetadata.Count != 1 || !_session.InputMetadata.TryGetValue("images", out var input) || input.ElementType != typeof(float))
                throw new InvalidDataException("Only Aimmy YOLOv8 float input 'images' is supported.");
            var dimensions = input.Dimensions;
            if (dimensions.Length != 4 || dimensions[1] != 3 || dimensions[0] is not (1 or -1))
                throw new InvalidDataException("Expected input [1,3,H,W].");
            _imageSize = dimensions[2] < 0 ? dynamicImageSize : dimensions[2];
            if (!new[] { 160, 256, 320, 416, 512, 640 }.Contains(_imageSize) || dimensions[3] > 0 && dimensions[3] != _imageSize)
                throw new InvalidDataException("Unsupported model image size.");
            if (_session.OutputMetadata.Count != 1) throw new InvalidDataException("Expected a single raw YOLOv8 output.");
            var output = _session.OutputMetadata.Values.Single();
            if (output.ElementType != typeof(float) || output.Dimensions.Length != 3 || output.Dimensions[0] is not (1 or -1) || output.Dimensions[1] is < 5 or > 1003)
                throw new InvalidDataException("Expected output [1,4+classes,detections].");
            var expectedDetections = (_imageSize / 8) * (_imageSize / 8) + (_imageSize / 16) * (_imageSize / 16) + (_imageSize / 32) * (_imageSize / 32);
            if (output.Dimensions[2] > 0 && output.Dimensions[2] != expectedDetections)
                throw new InvalidDataException("Expected raw YOLOv8 detection count for the selected image size.");
            ClassCount = output.Dimensions[1] - 4;
            ClassNames = Enumerable.Range(0, ClassCount).Select(i => $"Class_{i}").ToArray();
            if (_session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names))
            {
                // Standard Ultralytics metadata uses a Python dictionary. Newtonsoft handles quoted string keys
                // after the same numeric-key conversion used by the application's model loader.
                var converted = System.Text.RegularExpressions.Regex.Replace(names.Replace('\'', '"'), @"(?<=[{,])\s*(\d+)\s*:", "\"$1\":");
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(converted);
                    if (parsed != null) for (var i = 0; i < ClassCount; i++)
                        if (parsed.TryGetValue(i.ToString(System.Globalization.CultureInfo.InvariantCulture), out var name)) ClassNames[i] = name;
                }
                catch (JsonException) { }
            }
        }
        catch { _session.Dispose(); throw; }
    }

    public IReadOnlyList<LearningBox> Detect(string imagePath, double minimumConfidence)
    {
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        using var source = new Bitmap(imagePath);
        if (source.Width != source.Height) throw new InvalidDataException("Evaluation requires a square detector crop; full-screen images are not detector input.");
        using var bitmap = new Bitmap(_imageSize, _imageSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImage(source, 0, 0, _imageSize, _imageSize);
        var data = new float[3 * _imageSize * _imageSize];
        var bitmapData = bitmap.LockBits(new Rectangle(0, 0, _imageSize, _imageSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[_imageSize * 4];
            var plane = _imageSize * _imageSize;
            for (var y = 0; y < _imageSize; y++)
            {
                Marshal.Copy(bitmapData.Scan0 + y * bitmapData.Stride, row, 0, row.Length);
                for (var x = 0; x < _imageSize; x++)
                {
                    var pixel = y * _imageSize + x;
                    data[pixel] = row[x * 4 + 2] / 255f;
                    data[plane + pixel] = row[x * 4 + 1] / 255f;
                    data[2 * plane + pixel] = row[x * 4] / 255f;
                }
            }
        }
        finally { bitmap.UnlockBits(bitmapData); }
        var tensor = new DenseTensor<float>(data, [1, 3, _imageSize, _imageSize]);
        using var result = _session.Run([NamedOnnxValue.CreateFromTensor("images", tensor)]);
        var output = result.First().AsTensor<float>();
        if (output.Dimensions.Length != 3 || output.Dimensions[1] != 4 + ClassCount || output.Dimensions[2] > 100000)
            throw new InvalidDataException("Unsupported detector output.");
        var boxes = new List<LearningBox>();
        for (var i = 0; i < output.Dimensions[2]; i++)
        {
            var bestClass = 0;
            var confidence = output[0, 4, i];
            for (var c = 1; c < ClassCount; c++) if (output[0, 4 + c, i] > confidence) { confidence = output[0, 4 + c, i]; bestClass = c; }
            if (confidence < minimumConfidence) continue;
            var width = output[0, 2, i] / _imageSize;
            var height = output[0, 3, i] / _imageSize;
            var left = output[0, 0, i] / _imageSize - width / 2;
            var top = output[0, 1, i] / _imageSize - height / 2;
            var box = new LearningBox(bestClass, left, top, width, height, confidence);
            // Full-square detector region, before the same class-wise suppression as the local runtime.
            if (box.IsValid && width * _imageSize > 1 && height * _imageSize > 1) boxes.Add(box);
        }
        var kept = new List<LearningBox>();
        foreach (var candidate in boxes.OrderByDescending(x => x.Confidence).Take(1000))
        {
            if (kept.Any(x => x.ClassId == candidate.ClassId && MetricAccumulator.IntersectionOverUnion(x, candidate) > (double).45f)) continue;
            kept.Add(candidate);
            if (kept.Count == 100) break;
        }
        return kept;
    }

    public void Dispose() => _session.Dispose();
}
