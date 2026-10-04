using System.Drawing;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Aimmy2.AILogic
{
    internal static class PredictionFilter
    {
        internal static List<Prediction> CreatePredictions(
            Tensor<float> outputTensor,
            Rectangle detectionBox,
            int imageSize,
            int numDetections,
            int numClasses,
            IReadOnlyDictionary<int, string> modelClasses,
            float minConfidence,
            string selectedClass,
            float fovMinX,
            float fovMaxX,
            float fovMinY,
            float fovMaxY)
        {
            int selectedClassId = ResolveSelectedClassId(modelClasses, selectedClass);

            var predictions = new List<Prediction>(numDetections);

            for (int i = 0; i < numDetections; i++)
            {
                float xCenter = outputTensor[0, 0, i];
                float yCenter = outputTensor[0, 1, i];
                float width = outputTensor[0, 2, i];
                float height = outputTensor[0, 3, i];

                int bestClassId = 0;
                float bestConfidence = 0f;

                if (numClasses == 1)
                {
                    bestConfidence = outputTensor[0, 4, i];
                }
                else
                {
                    if (selectedClassId == -1)
                    {
                        for (int classId = 0; classId < numClasses; classId++)
                        {
                            float classConfidence = outputTensor[0, 4 + classId, i];
                            if (classConfidence > bestConfidence)
                            {
                                bestConfidence = classConfidence;
                                bestClassId = classId;
                            }
                        }
                    }
                    else
                    {
                        bestConfidence = outputTensor[0, 4 + selectedClassId, i];
                        bestClassId = selectedClassId;
                    }
                }

                if (bestConfidence < minConfidence) continue;

                float xMin = xCenter - width / 2;
                float yMin = yCenter - height / 2;
                float xMax = xCenter + width / 2;
                float yMax = yCenter + height / 2;

                if (xMin < fovMinX || xMax > fovMaxX || yMin < fovMinY || yMax > fovMaxY) continue;

                predictions.Add(new Prediction
                {
                    Rectangle = new RectangleF(xMin, yMin, width, height),
                    Confidence = bestConfidence,
                    ClassId = bestClassId,
                    ClassName = modelClasses.GetValueOrDefault(bestClassId, $"Class_{bestClassId}"),
                    CenterXTranslated = xCenter / imageSize,
                    CenterYTranslated = yCenter / imageSize,
                    ScreenCenterX = detectionBox.Left + xCenter,
                    ScreenCenterY = detectionBox.Top + yCenter
                });
            }

            return predictions;
        }

        internal static int ResolveSelectedClassId(IReadOnlyDictionary<int, string> modelClasses, string selectedClass)
        {
            if (selectedClass == "Best Confidence")
            {
                return -1;
            }

            foreach (var (classId, className) in modelClasses)
            {
                if (className == selectedClass)
                {
                    return classId;
                }
            }

            return -1;
        }
    }
}
