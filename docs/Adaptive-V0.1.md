# Gameplay Analyzer V0.1

Cette version observe un aim trainer offline dont le curseur reste mobile. Elle ne contrôle pas la souris, ne collecte pas de captures et n'entraîne aucun modèle. Le profil produit est un résumé de session : V0.2 (historique permanent), V0.3 (optimisation), V0.4 (dataset) et V0.5 (entraînement) restent des étapes séparées.

## Dépôt inspecté et points d'intégration

Le dossier fourni était vide. La base récupérée est `https://github.com/Babyhamsta/Aimmy`, commit `6f7564b6d466989e15afd801d56d69a2eb6e4c88`. Les conditions du dépôt se trouvent dans `LICENSE` et `SourceAvailable.md`.

| Donnée | Code existant | Intégration V0.1 |
| --- | --- | --- |
| Frames | `Aimmy2/AILogic/CaptureManager.cs`, `ScreenGrab` | Capture existante réutilisée, aucune image sauvegardée |
| Détections | `Aimmy2/AILogic/PredictionFilter.cs`, `CreatePredictions` | Snapshot après filtrage ONNX, avant StickyAim |
| Coordonnées | `Prediction.ScreenCenterX/Y`, `Rectangle.Width/Height` | Pixels du bureau, origine du moniteur incluse ; pas de rescaling écran/image supplémentaire |
| Entrées utilisateur | `WinAPICaller.GetCursorPosition`; infrastructure de bindings séparée | Adaptateur dédié en lecture via `GetCursorPos` et état du bouton gauche |
| Sorties souris | `Aimmy2/InputLogic/MouseManager.cs` | Gardes sur mouvement, clic, maintien et relâchement |

La capture actuelle couvre la zone définie par Aimmy, et les détections restent filtrées par son FOV, sa confiance et sa classe. La première apparition détectée n'est donc pas nécessairement l'apparition réelle de la cible.

## Architecture exacte

```text
AIManager : frame -> ONNX -> PredictionFilter
                                  |
                         ObservationSession.UpdateTargets
                                  |
                         snapshot immuable / ID temporaire
                                  |
GetCursorPos + bouton gauche -> GameplayEvent (horloge monotone)
                                  |
                    GameplayRecorder.TryRecord (file bornée)
                                  |
                   worker unique : events.jsonl bufferisé
                                  |
                     EngagementSegmenter -> Engagement
                                  |
                         FeatureExtractor
                                  |
               engagements.jsonl + SessionAnalyzer
                                  |
                    PlayerStyleProfile de session
                                  |
                  analysis.json / analysis.txt / quality.json
```

Les sept classes demandées se trouvent dans `Aimmy2/Adaptive/`. `ObservationSession.cs` contient en plus l'adaptateur Windows, la configuration et le verrou de sortie. Le cœur d'analyse n'a aucune dépendance à Windows, ONNX ou une API de mouvement ; on peut l'alimenter ensuite avec la télémétrie d'un sandbox.

La file accepte plusieurs producteurs et un consommateur. Capacité par défaut : 8192 événements. Le producteur ne bloque jamais ; en saturation, le nouvel événement est refusé et compté. Les numéros de séquence permettent de couper les engagements traversant une perte. Le worker écrit les JSONL avec des buffers de 64 Kio. Il conserve seulement les métriques par engagement en mémoire ; les samples d'un engagement sont bornés par une durée de 10 s.

## Changements dans les fichiers existants

Seulement deux fichiers existants sont modifiés. Le patch exact est fourni dans [integration-v0.1.patch](integration-v0.1.patch).

`Aimmy2/AILogic/AIManager.cs` : ajout d'une session ; exécution de l'inférence pendant l'observation sans exiger Aim Assist ou un keybind ; transmission des détections avant StickyAim ; branche qui ignore AutoTrigger, CalculateCoordinates et HandleAim ; désactivation de SaveFrame ; arrêt de l'inférence à la fin de l'enregistrement ; drainage et export à la fermeture ou au changement de modèle.

`Aimmy2/InputLogic/MouseManager.cs` : retour immédiat des quatre méthodes de sortie lorsque le mode observation est activé. La configuration est lue une seule fois par processus. La fin des dix minutes et les changements des toggles Aim Assist/AutoTrigger ne réactivent pas les sorties. Les erreurs de lecture/configuration empêchent l'initialisation du mode plutôt que de retomber silencieusement sur une session assistée.

## Démarrer une session

1. Ouvrir un trainer offline avec un curseur mobile, sur le moniteur configuré dans Aimmy.
2. Depuis CMD à la racine, exécuter `Start-Analyzer.cmd`, ou depuis PowerShell `./scripts/Start-Analyzer.ps1`. Le lanceur utilise la version déjà compilée lorsqu'elle existe : seul le runtime .NET 8 Desktop est nécessaire. Il installe la configuration d'observation à côté de l'exécutable et démarre l'application. Après des changements du code C#, utiliser `Start-Analyzer.cmd -Rebuild` ; un SDK .NET 8 ou supérieur est alors nécessaire. `-BuildOnly` compile sans ouvrir Aimmy ; `-CheckOnly` vérifie les prérequis de lancement sans ouvrir Aimmy (et compile si le binaire manque). Le SDK installé pour la validation est dans `%LOCALAPPDATA%/AutoAimmyBuild/dotnet`; le lanceur ne modifie pas le PATH ni les variables persistantes.
3. Charger dans Aimmy un modèle ONNX adapté aux cibles de ce trainer. La session commence au chargement du modèle. Vérifier la notification **OBSERVING: mouse output blocked**. Laisser Aim Assist et AutoTrigger désactivés pour rendre l'état visible cohérent avec l'observation.
4. Jouer dix minutes. À l'échéance, une notification donne le dossier du rapport et rappelle que les sorties restent bloquées. Fermer l'application plus tôt draine aussi la file et produit le rapport partiel.
5. Lire `sessions/<date UTC>-<id>/analysis.txt`, situé à côté de l'exécutable dans `Aimmy2/bin/x64/Release/net8.0-windows`. `analysis.json` contient les distributions globales et par taille de cible ; `events.jsonl` contient les observations ; `engagements.jsonl` les features et motifs de fin ; `quality.json` les pertes et paramètres de télémétrie.

Pour changer la durée ou le dossier, copier `adaptive.example.json` en `adaptive.json` à la racine et modifier les champs. Relancer le script après avoir fermé Aimmy. Le fichier effectif est lu depuis le dossier de l'exécutable. Sans fichier, Aimmy garde son fonctionnement upstream ; utiliser le script pour tester l'analyse. Aucun mécanisme ne vérifie techniquement qu'un trainer est offline : `OfflineTrainerConfirmed` formalise le choix de l'utilisateur.

Une nouvelle session est créée à chaque chargement de modèle ; ne pas changer de modèle pendant les dix minutes.

## Sens des métriques et limites

- **ReactionTimeMs** : première détection échantillonnée → déplacement net du curseur d'au moins 2 px. C'est une latence d'apparition détectée à mouvement, pas une mesure de réaction cognitive. Les engagements répétés sur une cible persistante ne constituent pas de nouvelles apparitions réelles.
- **AcquisitionTimeMs** : première détection → première entrée dans la boîte après début du mouvement. **ClickDelayMs** : entrée → front de clic ; un clic antérieur à l'entrée donne une valeur indisponible.
- Vitesse en px/s, accélération en px/s² et jerk en px/s³ : différences finies utilisant les timestamps réels. Accélération et jerk portent sur la vitesse scalaire et sont sensibles au sampling.
- **PathEfficiency** : déplacement direct début/fin divisé par longueur parcourue, borné entre 0 et 1 ; indisponible sans déplacement. Courbure : somme des angles de changement de direction divisée par la longueur.
- Overshoot : dépassement du bord éloigné de la boîte sur l'axe d'approche initial. Corrections : éloignement puis retour avec seuil de 3 px. Ces heuristiques sont surtout utiles pour des cibles fixes ; les mouvements de cible et changements d'identité peuvent les biaiser.
- Tracking : erreur moyenne pondérée par le temps, en pixels et en rayons de cible. **TrackingErrorVelocityRmsPxPerSecond** mesure les variations de cette erreur ; une valeur basse correspond à une erreur plus stable, sans prouver un meilleur tracking.
- **OvershootRate.Mean** représente la fréquence d'overshoot (multiplier par 100 pour un pourcentage). Les distributions p10/médiane/p90 utilisent une interpolation linéaire et donnent leur effectif. `CorrectionAmplitudePx` est la moyenne des amplitudes par engagement ; sa médiane agrégée n'est pas la médiane de toutes les corrections individuelles.
- Les groupes SmallTarget/MediumTarget/LargeTarget reposent sur le plus petit côté de la boîte (<30, <100, >=100 px), sans prétendre estimer la distance physique. Aucun pourcentage de confiance de profil n'est affiché.
- Clic, changement de cible, perte de cible >150 ms ou timeout terminent un engagement. Un bouton maintenu ne crée pas plusieurs engagements jusqu'à son relâchement. Les engagements traversant un trou de sampling >100 ms, une perte de file, un timeout ou une fin de session sont marqués tronqués et exclus des distributions ; ils restent exportés.
- Les IDs sont un suivi approximatif de la cible sélectionnée par proximité et classe, avec tolérance de déplacement et expiration de 150 ms. Des cibles qui se croisent peuvent échanger leur identité. Ce n'est pas encore un tracker multi-objet complet.
- Polling nominal toutes les 8 ms, cadence effective dépendante de Windows et de la charge. Les clics très courts peuvent être manqués. Les différences de coordonnées représentent le curseur du bureau, pas des deltas HID bruts. Un curseur verrouillé ou recentré rend l'analyse du mouvement inadaptée.
- Pas de ground truth de hits/misses ni de précision de détection ; `TargetSwitchTimeMs` reste indisponible. Les timestamps ne corrigent pas la latence de capture/inférence. Vérifier visuellement le modèle et les boîtes dans le trainer avant d'interpréter le rapport.

## Vérification

Compilation complète : `dotnet build Aimmy2/Aimmy2.csproj -c Release -p:Platform=x64`.

Tests du cœur sans GPU ni NuGet externe : `dotnet run --project tests/AdaptiveChecks/AdaptiveChecks.csproj -c Release`.

Les tests portent sur des trajectoires connues, métriques manquantes, quantiles, clic maintenu, changement/perte de cible, pertes de séquence, exclusion des engagements tronqués et export drainé. Ils ne remplacent pas une vraie session de trainer ni une mesure de surcharge en conditions réelles. Le code n'ajoute aucun bypass, injection, spoofing, dissimulation ou entraînement.
