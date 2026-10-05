# Local adaptive controller

This folder is a pure .NET engine. It does not capture the screen, generate mouse input, train neural-network weights, or access the network. The Windows adapter owns those operations and must enforce activation, foreground and observation guards at the final input call as well as at `Update`.

## Integration

1. Pass all filtered enemy detections to `AdaptiveAimEngine.Update`. `DetectionSample.X/Y` are absolute **screen centers**, including the monitor offset; width and height are screen pixels. `SourceIndex` identifies the original detection list entry. The engine applies class-specific IoU suppression, associates visible boxes and returns the selected original index.
2. Use a monotonic capture timestamp in seconds. Raw deltas supplied to calibration must cover the interval between the corresponding two captured images, not intervals measured after variable inference work. Missing images must interrupt a calibration interval. Track identities are approximate visual associations.
3. A new `GuidedCalibration(viewKey, screenHeight)` pass requires a stationary target, no character movement, no shooting and no generated mouse output. The player pans gently in both horizontal directions, then in both vertical directions, while keeping the same view and activation binding. Call `Observe` with the engine's current track and raw counts. Do not infer that a target is physically stationary from its screen coordinates.
4. `CalibrationResult.IsUsable` requires bidirectional evidence on both axes, sufficient counts and samples, a good fit, and a consistent gain. The result measures signed **target displacement in pixels per raw count** in the current view. It does not measure DPI, degrees or a physical distance. `SetCalibration` rejects insufficient evidence.
5. `AdaptiveDecision.CountsX/Y` are already bounded **relative counts**. Do not pass them through the original pixel-coordinate `MoveCrosshair` conversion. Apply them only if the Windows adapter independently allows output and activation is still held. A positive target error and a negative measured camera response produce a positive relative correction.
6. Checkpoint `Snapshot` through `AdaptiveProfileStore.Save`; `Load(path, out error)` reports malformed/oversize files and falls back to an uncalibrated state. Include a stable local player/account identity in the profile file selection, and settings, resolution, view/activation state and output method in the calibration key.

## What adapts

The nine contexts combine three **apparent image sizes** and three **screen-motion speeds**. Screen motion includes camera movement. A 250 ms dwell and gradual parameter blending prevent rapid changes. Defaults are conservative heuristics. Measurements per context retain normalized error, screen motion and assisted error crossings. Repeated crossings can reduce gain and increase smoothing within fixed bounds; this is not proof that the player's aim improved or that assistance caused the crossings. No automatic gain amplification is performed.

Detection loss, invalid frames, long gaps, insufficient calibration, a changed calibration key, changed screen height or released activation produce no movement. Stale detections are never projected through an occlusion. The normal controller caps output at 24 counts per frame and a 600 counts/second rate before integer quantization.

Changing DPI, sensitivity, FOV or zoom can invalidate the camera response. The adapter must suspend after known changes and require a new calibration. The engine's `ObserveCameraGain` hook also suspends after repeated reliable, independently measured mismatches. **That hook cannot establish an actual zoom change from arbitrary moving enemies.** Holding the same input binding does not prove visual ADS or a particular equipped scope.

## Validation

`tests/AdaptiveControlChecks` covers duplicate suppression, identity continuity, target-switch hysteresis, calibration sign/fit/bidirectional evidence, rejection of mixed camera gains, output/context guards, profile adaptation and persistence, and synthetic closed loops at 20–120 FPS and variable frame intervals. These checks establish implementation behavior on synthetic inputs. Real-game detection quality, device output behavior and gameplay improvement still require measurement.
