# Observation correction in 0.1.2

`AimReference` in adaptive.json is `ScreenCenter` (default) or `Cursor`. ScreenCenter requires a full-screen trainer on the monitor selected in Aimmy. Windowed viewports and offset reticles are not calibrated by this version.

Tracking error and box-entry acquisition use the selected reference. In ScreenCenter mode, the capture region is also centered on the selected monitor regardless of a saved mouse-centered capture setting. An initial overlap has unavailable acquisition latency. Clicks do not establish hits. Cursor trajectory, overshoot and correction metrics are unavailable in ScreenCenter mode.

A passive standard Windows Raw Input receiver samples relative mouse counts separately from desktop coordinates. Counts are summed between 8 ms samples; opposing movements within one interval can cancel, so this is not a high-frequency physical trajectory. It neither reads a game process nor generates input. Counts are not converted to pixels, degrees, weapon recoil or sensitivity settings. Raw input registration failures are recorded in quality.json; missing telemetry remains unavailable.

Cognitive ReactionTimeMs is unavailable. MovementOnsetLatencyMs requires at least 200 ms of input history and 150 ms without movement before the first target sample. Continuous motion at first detection does not yield an onset latency. Detection and target association remain model-dependent approximations; validate detection boxes visually before recording. No player rating or automatic setting adjustment is derived from these metrics.

Existing installations retain data. On the next launch, a configuration with no AimReference asks for the reference (Enter = ScreenCenter). New installations include Configurer-viseur.cmd; older installations can edit AimReference in data/adaptive.json to change the choice later. Old reports cannot be corrected because shared report ZIPs exclude raw observations.

Validation: core synthetic tests cover a distant stationary desktop cursor with a fixed reticle, box acquisition, initial overlap, missing raw data and pre-existing movement. Windows smoke tests exercise passive registration, reads and repeated disposal. Physical movement accuracy, model detection accuracy and trainer calibration still require a new controlled session.
