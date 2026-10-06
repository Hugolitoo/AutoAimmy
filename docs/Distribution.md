# Distribution et mises à jour AutoAimmy

Le dépôt de distribution est https://github.com/Hugolitoo/AutoAimmy. Les releases publiques ne contiennent aucune session, configuration personnelle, clé ou modèle du joueur.

## Pour ton ami

Télécharger `AutoAimmy-win-x64.zip` depuis la release, extraire entièrement le ZIP, puis lancer `AutoAimmy.cmd`. Le runtime .NET 8 Windows x64 est inclus. Il n'a pas besoin de compiler ni d'installer le SDK.

Le launcher vérifie les releases GitHub à chaque démarrage et propose une mise à jour disponible. Le canal `test` accepte les préversions. Le canal `stable` ignore les préversions. Modifier `updater.json` pour choisir le canal.

Les données sont dans `data/` : `bin/models`, `bin/configs`, réglages Aimmy, `adaptive.json`, `sessions` et `profiles`. Les versions de l'application se trouvent dans `versions/<version>`. Les mises à jour n'écrivent jamais dans `data`.

- `AutoAimmy-hors-ligne.cmd` ouvre la version installée sans contacter GitHub.
- `Mettre-a-jour.cmd` vérifie explicitement les nouveautés.
- `Retour-version-precedente.cmd` restaure le pointeur de version après fermeture d'AutoAimmy. La version remplacée reste disponible. Utiliser ensuite le lanceur hors ligne si on veut éviter de réinstaller immédiatement la version rejetée.
- `Exporter-rapport.cmd` exporte le dernier rapport terminé dans `exports`. Il inclut analyse, qualité et features d'engagement ; il exclut les événements bruts du curseur et toute configuration/clé. L'envoi du ZIP reste manuel.

Chaque rapport indique la version du programme. Le compteur de session commence au chargement d'un modèle. Placer le modèle du trainer dans `data/bin/models` avant de le charger. Aucun modèle gaming générique n'est inclus dans le paquet.

## Publier les prochaines versions

La compilation locale se fait avec `Build-Release.cmd -Version 0.3.0`. Le script lance les suites d'analyse, de contrôle adaptatif, d'automatisation locale, de capture et d'apprentissage, puis publie une application self-contained. La sortie est dans `artifacts/0.3.0` :

- `AutoAimmy-win-x64.zip` : première installation complète ;
- `AutoAimmy-update-win-x64.zip` : payload de mise à jour, sans données joueur ;
- `manifest.json` : version, plateforme, taille et SHA-256 du payload.
- `Reparer-mise-a-jour.cmd` : réparation du lanceur des premières installations ;
- `AutoAimmy-learning-win-x64.zip` : moteur Python/PyTorch CPU séparé, si le paquet local a été préparé. Le payload de l'application contient seulement son manifest de téléchargement et les scripts d'apprentissage.

Le moteur d'apprentissage de la 0.3 est publié séparément, environ 284 Mio. Le fichier `distribution/learning-runtime.json` référence sa release, son hash et sa taille. Les versions suivantes peuvent réutiliser ce paquet sans le télécharger à chaque mise à jour. Pour créer un nouveau moteur, utiliser `scripts/Build-LearningRuntime.ps1` avec l'environnement de construction préparé, mettre à jour le manifest et publier l'archive référencée. Le moteur est installé dans `data/learning/runtime`, distinct du runtime .NET inclus dans chaque version.

Le script refuse d'écraser un dossier de release existant et vérifie les fichiers du runtime embarqué. Après toute modification, utiliser un nouveau numéro de version.

Le workflow GitHub `AutoAimmy release` automatise les vérifications, la compilation et la publication. Depuis Actions, choisir ce workflow, `Run workflow`, un numéro numérique inédit et le canal. La préversion est publiée avec le tag `v<version>`. Les assets conservent des noms fixes ; le launcher sélectionne la version numérique compatible la plus récente parmi les 30 dernières releases.

La version initiale est publiée depuis le paquet validé localement. Les versions futures peuvent être produites par Actions dès que leurs changements de source ont été envoyés au dépôt. Une modification locale seule ne déclenche pas une mise à jour chez les testeurs : seule une release publiée devient installable.

## Vérifications et garanties

Le launcher utilise un verrou exclusif pour empêcher les lancements concurrents. L'updater refuse une installation ou un rollback si une instance de cette installation est ouverte. Il télécharge depuis les assets GitHub via HTTPS, vérifie le hash annoncé, contrôle la version et la structure du ZIP, refuse les traversées de répertoire et limite la taille extraite. Le payload est d'abord extrait dans un dossier unique ; le pointeur `current.json` est remplacé après validation, avec une sauvegarde de l'ancien état. Une panne avant promotion laisse la version précédente active.

Le SHA-256 garantit l'intégrité par rapport au manifest publié ; ce n'est pas une signature indépendante du compte GitHub. La confiance repose sur le dépôt et HTTPS. Les exécutables ne sont pas signés avec un certificat Windows ; aucun mécanisme ne contourne les protections de Windows.

La restauration porte sur le programme. Les futures migrations de format des profils devront conserver une compatibilité ou créer une sauvegarde ; l'updater ne restaure pas des données modifiées par une version ultérieure.

Tests : `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/UpdaterChecks.ps1` et `dotnet run --project tests/AdaptiveChecks/AdaptiveChecks.csproj -c Release`.

Le bouton upstream de mise à jour Aimmy a été neutralisé et renvoie vers le launcher AutoAimmy. Les conditions et notices originales sont livrées dans chaque payload.
