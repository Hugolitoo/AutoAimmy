# AutoAimmy

**AutoAimmy 0.3.0 — expérimental, pour les tests R6 contre IA.** L'application analyse les images du jeu sur votre PC, enregistre des exemples, mesure la réponse de la caméra et adapte une assistance calibrée. Les comparaisons et l'entraînement du détecteur peuvent se lancer automatiquement quand R6 quitte le premier plan. Il reste nécessaire de vérifier des images pour disposer d'une mesure fiable de la détection. Cette version ne prouve pas que votre visée ou votre modèle sont meilleurs dans une partie réelle.

## Installer ou mettre à jour

Télécharger **AutoAimmy-win-x64.zip** dans les [Releases](https://github.com/Hugolitoo/AutoAimmy/releases), extraire **tout** le ZIP, puis ouvrir **AutoAimmy.cmd**. Le runtime .NET Windows x64 est inclus ; aucun SDK de développement n'est nécessaire. Dans une installation existante, fermer l'application, lancer **Mettre-a-jour.cmd**, puis rouvrir **AutoAimmy.cmd**. Conserver le dossier `data` : il contient les modèles, réglages et observations de ce PC.

Mettre votre modèle ONNX habituel dans `data/bin/models`, puis le charger dans **Modèles**. Aucun modèle personnel n'est distribué avec l'application. Le lanceur habituel vérifie les mises à jour sur GitHub avant l'ouverture. **Retour-version-precedente.cmd** restaure la version précédente de l'application.

## Premier essai

1. Ouvrir **AUTO**. Laisser **Enregistrer automatiquement mes sessions R6** activé, puis revenir dans R6 sur le moniteur sélectionné. La collecte démarre lorsqu'un modèle est chargé et que le jeu est au premier plan.
2. Faire la **Calibration guidée** dans la vue utilisée : maintenir la touche de visée configurée dans Aimmy, faire de petits mouvements horizontaux puis verticaux dans les deux sens, sans marcher ni tirer. Le décor doit être visible et une cible immobile aide la mesure.
3. Après validation, cliquer **Activer l'assistance expérimentale**. Elle agit avec la touche de visée maintenue et une cible confirmée. **F8** l'arrête. L'activation initiale reste un choix explicite.
4. Revenir dans AUTO après le test. La collecte automatique se termine après environ dix secondes hors de la fenêtre R6. **Vérifier les images** permet de corriger les cadres et de confirmer les images sans cible.
5. Répéter sur une deuxième session et vérifier au moins **20 images par session**. Avec 40 images vérifiées au total et assez de cibles dans la validation, l'application peut comparer les seuils, préparer le moteur local, entraîner un candidat et le comparer pendant les pauses.

Le [guide 0.3](docs/Local-Automation-0.3.md) explique les états de l'interface, les conditions des mesures et la marche à suivre. Aucun ZIP d'observation ne doit être envoyé à un développeur pour utiliser ce parcours.

## Ce qui change en 0.3

- La capture DirectX est partagée entre le détecteur, le lecteur du HUD et l'enregistreur. Cela corrige la duplication concurrente de la même sortie qui pouvait provoquer `0x80070057` pendant la calibration.
- Le déplacement du décor sert à estimer le mouvement de caméra, puis à mieux distinguer le mouvement propre des cibles. Des situations observées peuvent compléter les neuf catégories initiales, jusqu'à 36 sous-profils. Le gain et le lissage sont comparés sur de petites fenêtres de suivi avant de conserver un ajustement.
- La réponse de caméra peut être remesurée pendant les mouvements sans tir ni assistance. Des vues aux réponses différentes gardent leurs profils. Cette mesure peut s'adapter à un changement de zoom ou de sensibilité sans connaître le nom de la lunette ou les DPI physiques ; les scènes ambiguës demandent encore une calibration guidée.
- Les sessions, l'import des exemples, les comparaisons et l'entraînement peuvent s'enchaîner automatiquement. Les calculs s'interrompent lorsque R6 revient au premier plan.
- La lecture des munitions sert à estimer les tirs et le recul dans des fenêtres de mesure cohérentes. Le sang et les changements brefs autour du viseur fournissent des indices visuels probables, dont la fiabilité reste à vérifier sur les images du jeu.
- Le moteur d'entraînement portable est téléchargé une fois, environ **284 Mio**, puis utilisé localement. Les exports YOLOv8 classiques compatibles peuvent fournir les poids d'entraînement par reconstruction depuis l'ONNX, seulement si les sorties reconstruites correspondent à l'original. Un export incompatible exige encore une source `.pt` de confiance.
- Un essai de seuil refusé conserve le dernier seuil accepté. Les poids d'un modèle réentraîné accepté sont associés à ce modèle pour permettre les cycles suivants. De nouvelles images vérifiées peuvent déclencher un retour au modèle précédent si une régression est mesurée.

## Données et limites

L'enregistrement est une séquence d'images à **5 images/s maximum**, avec lecture animée locale, sans audio. Ce n'est pas un MP4 haute fréquence. La collecte s'arrête aux quotas affichés : 512 Mio par session, 2 Gio cumulés ou 30 minutes ; les images destinées à la revue ont leur propre quota. Les données se trouvent dans `data/local-capture`, `data/local-profiles` et `data/learning`. Rien n'est téléversé automatiquement.

Les réglages R6 sauvegardés sont importés. Si plusieurs comptes existent, un choix de configuration peut être demandé sans retaper les valeurs. Les DPI physiques ne se lisent pas dans l'image du jeu. Le modèle ne peut pas vérifier à lui seul que ses cadres sont corrects : **les images de validation doivent être revues par une personne**. Une reconstruction équivalente permet de réentraîner le modèle ; elle ne l'améliore pas à elle seule.

Les tests logiciels incluent des simulations, de vraies inférences ONNX et un cycle de reconstruction, entraînement et export sur des images synthétiques. Le candidat synthétique a été refusé faute de gain mesuré. La qualité des détections, des profils et des indices visuels reste à mesurer sur vos parties contre IA.

**AutoAimmy-hors-ligne.cmd** évite la vérification des mises à jour. Le premier téléchargement du moteur d'apprentissage requiert une connexion ; une fois le moteur installé, l'analyse et l'entraînement restent hors ligne. Voir le guide pour préparer une installation entièrement hors ligne.

Fork non commercial de [Aimmy](https://github.com/Babyhamsta/Aimmy). Contributions et redistribution doivent respecter la [licence PolyForm Noncommercial](LICENSE) et la [notice source disponible](SourceAvailable.md). Voir aussi la [distribution](docs/Distribution.md), les [réglages importés](docs/Player-profiles.md) et l'[architecture de l'analyseur passif](docs/Adaptive-V0.1.md).
