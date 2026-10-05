# AutoAimmy

Version 0.1.4 adds saved player profiles: enter your settings once, reuse the active profile at launch, and include an immutable context snapshot with each report. Use Profil-joueur.cmd to switch profiles. These values are user-declared; visual recognition of weapon, scope and ADS is not implemented yet. See [player profiles](docs/Player-profiles.md).

Experimental, observation-only gameplay analyzer for **offline aim trainers and sandboxes**. Noncommercial fork of [Aimmy](https://github.com/Babyhamsta/Aimmy).

Download the first installation from [Releases](https://github.com/Hugolitoo/AutoAimmy/releases): **AutoAimmy-win-x64.zip**. Extract the entire ZIP and open **AutoAimmy.cmd**. Windows x64 and the .NET desktop runtime are bundled; no developer SDK is needed.

Add your offline trainer's ONNX model to `data/bin/models`. Recording starts when a model is loaded and lasts ten minutes by default. The application blocks generated mouse movement and clicks in observation mode. Version 0.1.2 supports a fixed screen-center reticle and separately records passive Windows raw mouse counts. Select the trainer monitor, use full screen and visually validate detection boxes. Existing installations are asked to choose their reference after updating.

Updates are offered before launch. Player settings, models and sessions remain in `data`, separate from application versions. Use **AutoAimmy-hors-ligne.cmd** to skip networking, **Retour-version-precedente.cmd** to roll back, and **Exporter-rapport.cmd** to export the latest report. Reports are never automatically uploaded.

For an existing 0.1.1 or 0.1.2 installation, version 0.1.3 fixes discovery when GitHub returns multiple releases. Download **Reparer-mise-a-jour.cmd** from the release assets, put it beside your existing AutoAimmy.cmd, close the app and run the repair. It backs up the old updater, changes only the faulty release-array assignment, then offers the normal update. No reinstallation or player-data transfer is needed.

The report leaves cognitive reaction time and uncalibrated angular metrics unavailable. See [measurement corrections](docs/Reticle-V0.1.2.md). Adaptive assistance and model training are future work.

See [distribution instructions](docs/Distribution.md) and [analyzer architecture](docs/Adaptive-V0.1.md). Contributions and redistribution must respect the upstream [PolyForm Noncommercial license](LICENSE) and [source-available notice](SourceAvailable.md).
