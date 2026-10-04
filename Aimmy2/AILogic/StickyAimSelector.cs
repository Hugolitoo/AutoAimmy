namespace Aimmy2.AILogic
{
    internal sealed class StickyAimSelector
    {
        private const int MaxFramesWithoutTarget = 3;
        private const float LockScoreDecay = 0.85f;
        private const float LockScoreGain = 15f;
        private const float MaxLockScore = 100f;
        private const float ReferenceTargetSize = 10000f;

        private Prediction? _currentTarget;
        private int _consecutiveFramesWithoutTarget;
        private float _lastTargetVelocityX;
        private float _lastTargetVelocityY;
        private float _targetLockScore;
        private int _framesWithoutMatch;

        internal Prediction? SelectTarget(
            bool stickyAimEnabled,
            float stickyThreshold,
            int imageSize,
            Prediction? bestCandidate,
            IReadOnlyList<Prediction> predictions)
        {
            if (!stickyAimEnabled)
            {
                Reset();
                return bestCandidate;
            }

            if (bestCandidate == null || predictions.Count == 0)
            {
                return HandleNoDetections();
            }

            _consecutiveFramesWithoutTarget = 0;

            float screenCenterX = imageSize / 2f;
            float screenCenterY = imageSize / 2f;
            Prediction? aimTarget = null;
            float nearestToCrosshairDistSq = float.MaxValue;

            foreach (var candidate in predictions)
            {
                float distSq = GetDistanceSq(candidate.ScreenCenterX, candidate.ScreenCenterY, screenCenterX, screenCenterY);
                if (distSq < nearestToCrosshairDistSq)
                {
                    nearestToCrosshairDistSq = distSq;
                    aimTarget = candidate;
                }
            }

            if (aimTarget == null)
            {
                return HandleNoDetections();
            }

            if (_currentTarget == null)
            {
                return AcquireNewTarget(aimTarget);
            }

            float lastX = _currentTarget.ScreenCenterX;
            float lastY = _currentTarget.ScreenCenterY;
            float targetArea = _currentTarget.Rectangle.Width * _currentTarget.Rectangle.Height;
            float targetSize = MathF.Sqrt(targetArea);
            float sizeFactor = GetSizeFactor(targetArea);
            float aimToCurrentDistSq = GetDistanceSq(aimTarget.ScreenCenterX, aimTarget.ScreenCenterY, lastX, lastY);
            float trackingRadius = targetSize * 3f;
            float trackingRadiusSq = trackingRadius * trackingRadius;
            float aimTargetArea = aimTarget.Rectangle.Width * aimTarget.Rectangle.Height;
            float sizeRatio = MathF.Min(targetArea, aimTargetArea) / MathF.Max(targetArea, aimTargetArea);
            bool isSameTarget = aimToCurrentDistSq < trackingRadiusSq && sizeRatio > 0.5f;

            if (isSameTarget)
            {
                _framesWithoutMatch = 0;
                UpdateVelocity(aimTarget, sizeFactor);
                _targetLockScore = Math.Min(MaxLockScore, _targetLockScore + LockScoreGain);
                _currentTarget = aimTarget;
                return aimTarget;
            }

            _framesWithoutMatch++;

            bool aimTargetVeryCentered = nearestToCrosshairDistSq < stickyThreshold * stickyThreshold * 0.25f;
            if (aimTargetVeryCentered || _framesWithoutMatch >= 3)
            {
                return AcquireNewTarget(aimTarget);
            }

            return null;
        }

        private static float GetDistanceSq(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        private static float GetSizeFactor(float targetArea)
        {
            float ratio = ReferenceTargetSize / Math.Max(targetArea, 100f);
            return Math.Clamp(ratio, 1.0f, 3.0f);
        }

        private Prediction? HandleNoDetections()
        {
            if (_currentTarget != null && ++_consecutiveFramesWithoutTarget <= MaxFramesWithoutTarget)
            {
                _targetLockScore *= LockScoreDecay;

                return new Prediction
                {
                    ScreenCenterX = _currentTarget.ScreenCenterX + _lastTargetVelocityX * _consecutiveFramesWithoutTarget,
                    ScreenCenterY = _currentTarget.ScreenCenterY + _lastTargetVelocityY * _consecutiveFramesWithoutTarget,
                    Rectangle = _currentTarget.Rectangle,
                    Confidence = _currentTarget.Confidence * (1f - _consecutiveFramesWithoutTarget * 0.2f),
                    ClassId = _currentTarget.ClassId,
                    ClassName = _currentTarget.ClassName,
                    CenterXTranslated = _currentTarget.CenterXTranslated,
                    CenterYTranslated = _currentTarget.CenterYTranslated
                };
            }

            Reset();
            return null;
        }

        private Prediction AcquireNewTarget(Prediction target)
        {
            _lastTargetVelocityX = 0f;
            _lastTargetVelocityY = 0f;
            _targetLockScore = LockScoreGain;
            _framesWithoutMatch = 0;
            _currentTarget = target;
            return target;
        }

        private void UpdateVelocity(Prediction newTarget, float sizeFactor)
        {
            if (_currentTarget == null)
            {
                return;
            }

            float smoothing = Math.Clamp(0.6f + sizeFactor * 0.1f, 0.7f, 0.9f);
            float newWeight = 1f - smoothing;

            float newVelX = newTarget.ScreenCenterX - _currentTarget.ScreenCenterX;
            float newVelY = newTarget.ScreenCenterY - _currentTarget.ScreenCenterY;
            _lastTargetVelocityX = _lastTargetVelocityX * smoothing + newVelX * newWeight;
            _lastTargetVelocityY = _lastTargetVelocityY * smoothing + newVelY * newWeight;
        }

        internal void Reset()
        {
            _currentTarget = null;
            _consecutiveFramesWithoutTarget = 0;
            _framesWithoutMatch = 0;
            _lastTargetVelocityX = 0f;
            _lastTargetVelocityY = 0f;
            _targetLockScore = 0f;
        }
    }
}
