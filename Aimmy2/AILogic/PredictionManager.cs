using Aimmy2.Class;

namespace AILogic
{
    internal class KalmanPrediction
    {
        public struct Detection
        {
            public int X;
            public int Y;
            public DateTime Timestamp;
        }

        // State: [x, y, vx, vy]
        private double _x, _y, _vx, _vy;

        // Covariance matrix (4x4 simplified to diagonal)
        private double _p00 = 1.0, _p11 = 1.0, _p22 = 1.0, _p33 = 1.0;

        // Tuning parameters
        private const double ProcessNoise = 0.1;
        private const double MeasurementNoise = 0.5;
        private const double MaxVelocity = 5000.0;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private bool _initialized = false;

        public void UpdateKalmanFilter(Detection detection)
        {
            var now = DateTime.UtcNow;

            if (!_initialized)
            {
                _x = detection.X;
                _y = detection.Y;
                _vx = 0;
                _vy = 0;
                _lastUpdateTime = now;
                _initialized = true;
                return;
            }

            // Calculate time step, clamped between 1ms and 100ms
            double dt = (now - _lastUpdateTime).TotalSeconds;
            dt = Math.Clamp(dt, 0.001, 0.1);

            // Prediction step
            double predictedX = _x + _vx * dt;
            double predictedY = _y + _vy * dt;

            // Update covariance (add process noise)
            _p00 += ProcessNoise;
            _p11 += ProcessNoise;
            _p22 += ProcessNoise * 10; // Higher noise for velocity
            _p33 += ProcessNoise * 10;

            // Innovation (measurement residual)
            double innovationX = detection.X - predictedX;
            double innovationY = detection.Y - predictedY;

            // Kalman gain (simplified for position measurements)
            double K = _p00 / (_p00 + MeasurementNoise);

            // Update state
            _x = predictedX + K * innovationX;
            _y = predictedY + K * innovationY;

            // Update velocity based on innovation
            _vx += K * innovationX / dt;
            _vy += K * innovationY / dt;

            // Clamp velocity to reasonable values
            _vx = Math.Clamp(_vx, -MaxVelocity, MaxVelocity);
            _vy = Math.Clamp(_vy, -MaxVelocity, MaxVelocity);

            // Update covariance
            _p00 *= (1 - K);
            _p11 *= (1 - K);

            _lastUpdateTime = now;
        }

        public Detection GetKalmanPosition(double mouseSpeed = 0)
        {
            var now = DateTime.UtcNow;
            double dt = (now - _lastUpdateTime).TotalSeconds;

            // Predict current position based on time since last update
            double currentX = _x + _vx * dt;
            double currentY = _y + _vy * dt;

            // Get base lead time from settings
            double leadTime = AimSettings.KalmanLeadTime;

            // Dynamically adjust based on mouse speed if available
            if (mouseSpeed > 0.0)
            {
                // Estimate animation completion time from mouse speed
                double estimatedCompletionTime = 100.0 / mouseSpeed;
                double dynamicLead = estimatedCompletionTime * 0.4; // Use 40% of completion time
                // Scale by user setting (setting acts as multiplier around the dynamic value)
                leadTime = dynamicLead * (leadTime / 0.10); // 0.10 is the default, so setting of 0.10 = 1x multiplier
                leadTime = Math.Clamp(leadTime, 0.02, 0.3); // Cap to reasonable range
            }

            // Predict where target will be after lead time
            double predictedX = currentX + _vx * leadTime;
            double predictedY = currentY + _vy * leadTime;

            return new Detection
            {
                X = (int)predictedX,
                Y = (int)predictedY,
                Timestamp = now
            };
        }

        public void Reset()
        {
            _x = _y = _vx = _vy = 0;
            _p00 = _p11 = _p22 = _p33 = 1.0;
            _initialized = false;
        }
    }

    internal class WiseTheFoxPrediction
    {
        /// <summary>
        /// Prediction using Exponential Moving Average with velocity tracking
        /// Originally by @wisethef0x, fixed to include actual prediction
        /// </summary>
        public struct WTFDetection
        {
            public int X;
            public int Y;
            public DateTime Timestamp;
        }

        private DateTime _lastUpdateTime;
        private const double Alpha = 0.5; // Smoothing factor

        private double _emaX, _emaY;
        private double _velocityX, _velocityY;
        private double _prevX, _prevY;
        private bool _initialized = false;

        public WiseTheFoxPrediction()
        {
            _lastUpdateTime = DateTime.UtcNow;
        }

        public void UpdateDetection(WTFDetection detection)
        {
            var now = DateTime.UtcNow;

            if (!_initialized)
            {
                _emaX = detection.X;
                _emaY = detection.Y;
                _prevX = detection.X;
                _prevY = detection.Y;
                _velocityX = 0;
                _velocityY = 0;
                _lastUpdateTime = now;
                _initialized = true;
                return;
            }

            // Calculate time delta, clamped
            double dt = (now - _lastUpdateTime).TotalSeconds;
            dt = Math.Clamp(dt, 0.001, 0.1);

            // Apply EMA to position
            _emaX = Alpha * detection.X + (1.0 - Alpha) * _emaX;
            _emaY = Alpha * detection.Y + (1.0 - Alpha) * _emaY;

            // Calculate velocity (pixels per second)
            double newVelocityX = (_emaX - _prevX) / dt;
            double newVelocityY = (_emaY - _prevY) / dt;

            // Apply EMA to velocity for smoothing
            _velocityX = Alpha * newVelocityX + (1.0 - Alpha) * _velocityX;
            _velocityY = Alpha * newVelocityY + (1.0 - Alpha) * _velocityY;

            // Store for next frame
            _prevX = _emaX;
            _prevY = _emaY;
            _lastUpdateTime = now;
        }

        public WTFDetection GetEstimatedPosition()
        {
            // Get lead time from settings
            double leadTime = AimSettings.WiseTheFoxLeadTime;

            // Predict where target will be after lead time
            double predictedX = _emaX + _velocityX * leadTime;
            double predictedY = _emaY + _velocityY * leadTime;

            return new WTFDetection
            {
                X = (int)predictedX,
                Y = (int)predictedY,
                Timestamp = DateTime.UtcNow
            };
        }

        public void Reset()
        {
            _emaX = _emaY = 0;
            _velocityX = _velocityY = 0;
            _prevX = _prevY = 0;
            _initialized = false;
        }
    }

    internal class ShalloePredictionV2
    {
        /// <summary>
        /// Velocity-based prediction using historical velocity averaging
        /// Fixed to use proper velocity calculation instead of broken position averaging
        /// </summary>
        private static List<int> _velocityXHistory = new(5);
        private static List<int> _velocityYHistory = new(5);

        private static int _prevX = 0;
        private static int _prevY = 0;
        private static bool _initialized = false;

        // Max velocity samples to keep
        private const int MaxHistorySize = 5;

        public static void UpdatePosition(int targetX, int targetY)
        {
            if (!_initialized)
            {
                _prevX = targetX;
                _prevY = targetY;
                _initialized = true;
                return;
            }

            // Calculate velocity (pixels per frame)
            int velocityX = targetX - _prevX;
            int velocityY = targetY - _prevY;

            // Add to history, removing oldest if full
            if (_velocityXHistory.Count >= MaxHistorySize)
            {
                _velocityXHistory.RemoveAt(0);
                _velocityYHistory.RemoveAt(0);
            }

            _velocityXHistory.Add(velocityX);
            _velocityYHistory.Add(velocityY);

            // Store current position for next frame
            _prevX = targetX;
            _prevY = targetY;
        }

        public static int GetSPX()
        {
            if (!_initialized || _velocityXHistory.Count == 0)
            {
                return _prevX;
            }

            // Get lead multiplier from settings
            double leadMultiplier = AimSettings.ShalloeLeadMultiplier;

            // Calculate average velocity
            double avgVelocity = _velocityXHistory.Average();

            // Predict future position: current + (average velocity * lead multiplier)
            return (int)(_prevX + avgVelocity * leadMultiplier);
        }

        public static int GetSPY()
        {
            if (!_initialized || _velocityYHistory.Count == 0)
            {
                return _prevY;
            }

            // Get lead multiplier from settings
            double leadMultiplier = AimSettings.ShalloeLeadMultiplier;

            // Calculate average velocity
            double avgVelocity = _velocityYHistory.Average();

            // Predict future position: current + (average velocity * lead multiplier)
            return (int)(_prevY + avgVelocity * leadMultiplier);
        }

        public static void Reset()
        {
            _velocityXHistory.Clear();
            _velocityYHistory.Clear();
            _prevX = _prevY = 0;
            _initialized = false;
        }
    }
}
