# AutoAimmy

Version 0.1.8 adds experimental local Windows OCR of explicit text in R6's lower-right HUD (up to one read/second). Recognition requires two consecutive matching labels; icon-only weapons, equipped scopes without text and actual ADS remain unrecognized. No frames or OCR transcripts are saved or transmitted. A visual-events.jsonl timeline joins new reports. Multiple saved R6 configurations no longer hide every value: the newest is shown as a provisional candidate, and a one-click choice is remembered without retyping settings. Windows 10 version 2004 or newer is required for this build.

Version 0.1.7 adds an AutoAimmy page inside the app and opens it by default in observation mode. It shows the import result (including Imported), saved settings, unknown context, current model, observation progress and latest report. Settings can be reread without a questionnaire when no recording is in progress; model and report folders can be opened directly.

Version 0.1.6 removes the required profile questionnaire. Saved R6 settings are read automatically from GameSettings.ini on every launch: horizontal/vertical sensitivity, ADS values by magnification, multipliers, FOV, resolution and the aspect-ratio enum. Profil-joueur.cmd refreshes and displays the import without asking for settings. Hardware DPI, equipped weapon/scope and actual ADS state remain unknown; missing or ambiguous account settings are never guessed. Each report records SettingsFile provenance and the configuration file timestamp without account paths or identifiers. See [automatic settings](docs/Player-profiles.md).

Experimental, observation-only gameplay analyzer for **offline aim trainers and sandboxes**. Noncommercial fork of [Aimmy](https://github.com/Babyhamsta/Aimmy).

Download the first installation from [Releases](https://github.com/Hugolitoo/AutoAimmy/releases): **AutoAimmy-win-x64.zip**. Extract the entire ZIP and open **AutoAimmy.cmd**. Windows x64 and the .NET desktop runtime are bundled; no developer SDK is needed.

Add your offline trainer's ONNX model to `data/bin/models`. Recording starts when a model is loaded and lasts ten minutes by default. The application blocks generated mouse movement and clicks in observation mode. Version 0.1.2 supports a fixed screen-center reticle and separately records passive Windows raw mouse counts. Select the trainer monitor, use full screen and visually validate detection boxes. Existing installations are asked to choose their reference after updating.

Updates are installed automatically before launch. Player settings, models and sessions remain in `data`, separate from application versions. Use **AutoAimmy-hors-ligne.cmd** to skip networking, **Retour-version-precedente.cmd** to roll back, and **Exporter-rapport.cmd** to retry a manual export if needed. Report ZIPs appear automatically in `exports` after observation finishes or normal application closure. Reports are never automatically uploaded.

For an existing 0.1.1 or 0.1.2 installation, version 0.1.3 fixes discovery when GitHub returns multiple releases. Download **Reparer-mise-a-jour.cmd** from the release assets, put it beside your existing AutoAimmy.cmd, close the app and run the repair. It backs up the old updater, changes only the faulty release-array assignment, then offers the normal update. No reinstallation or player-data transfer is needed.

The report leaves cognitive reaction time and uncalibrated angular metrics unavailable. See [measurement corrections](docs/Reticle-V0.1.2.md). Adaptive assistance and model training are future work.

See [distribution instructions](docs/Distribution.md) and [analyzer architecture](docs/Adaptive-V0.1.md). Contributions and redistribution must respect the upstream [PolyForm Noncommercial license](LICENSE) and [source-available notice](SourceAvailable.md).
