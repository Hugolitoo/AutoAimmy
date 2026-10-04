# AutoAimmy

Experimental, observation-only gameplay analyzer for **offline aim trainers and sandboxes**. Noncommercial fork of [Aimmy](https://github.com/Babyhamsta/Aimmy).

Download the first installation from [Releases](https://github.com/Hugolitoo/AutoAimmy/releases): **AutoAimmy-win-x64.zip**. Extract the entire ZIP and open **AutoAimmy.cmd**. Windows x64 and the .NET desktop runtime are bundled; no developer SDK is needed.

Add your offline trainer's ONNX model to `data/bin/models`. Recording starts when a model is loaded and lasts ten minutes by default. The application blocks generated mouse movement and clicks in observation mode. V0.1 uses desktop cursor telemetry, so locked/recentered cursors require a future telemetry adapter.

Updates are offered before launch. Player settings, models and sessions remain in `data`, separate from application versions. Use **AutoAimmy-hors-ligne.cmd** to skip networking, **Retour-version-precedente.cmd** to roll back, and **Exporter-rapport.cmd** to export the latest report. Reports are never automatically uploaded.

This release adds deployment and updates to V0.1; it does not add adaptive assistance or model training.

See [distribution instructions](docs/Distribution.md) and [analyzer architecture](docs/Adaptive-V0.1.md). Contributions and redistribution must respect the upstream [PolyForm Noncommercial license](LICENSE) and [source-available notice](SourceAvailable.md).
