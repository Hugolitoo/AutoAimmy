# AutoAimmy 0.2.0 — parcours local expérimental

Ce document décrit la version 0.2. Pour le parcours actuel et les nouveaux automatismes, voir le [guide 0.3](Local-Automation-0.3.md).

Le but est d'améliorer la détection et d'adapter l'assistance à chaque joueur sur son PC. Cette version fournit un parcours utilisable dans Aimmy. Elle n'établit pas que le modèle ou la visée sont déjà meilleurs dans R6.

## Démarrer

1. Mettre à jour, fermer puis relancer `AutoAimmy.cmd`. Ouvrir **AUTO** et charger le modèle ONNX habituel dans **Modèles**.
2. Cliquer **Démarrer la session locale**. R6 doit être au premier plan sur le moniteur sélectionné. L'enregistrement se met en pause quand une autre application passe devant.
3. Cliquer **Calibration guidée**. Après cinq secondes, rester devant une cible immobile, sans marcher ni tirer. Maintenir la touche de visée configurée dans Aimmy, conserver la même vue et déplacer doucement la souris gauche/droite, puis haut/bas, dans les deux sens. Une petite fenêtre affiche la progression. La calibration se termine quand les mesures sont cohérentes ; sinon elle échoue après 45 secondes d'images reçues.
4. Quand la calibration est enregistrée, cliquer **Activer l'assistance expérimentale**. Elle agit seulement avec la touche de visée maintenue et une cible confirmée dans R6 au premier plan. **F8** coupe l'assistance, même lorsque l'inférence est en pause. Recalibrer si DPI, souris, sensibilité ou zoom change : ces changements ne sont pas tous reconnaissables automatiquement.
5. Utiliser **Arrêter la session**, puis **Voir les enregistrements** ou **Vérifier les images**. Aucun rapport n'a besoin d'être envoyé au développeur.

## Ce qui est automatique

- Lecture des réglages R6 sauvegardés au lancement et après modification des fichiers. Plusieurs comptes gardent un choix local ; le dernier fichier modifié n'est pas présenté comme une preuve du compte actif.
- Capture locale du jeu à jusqu'à 5 images/s, largeur maximale 1280 px, et échantillons exacts du carré analysé par le détecteur à environ 1 image/s. Lecture animée dans `playback.html` ; pas d'audio ni de MP4.
- Sélection et suivi cohérent des cibles après suppression des boîtes qui se chevauchent. Aucune mémoire du jeu n'est lue.
- Neuf sous-profils : petite/moyenne/grande cible, chacune avec mouvement lent/modéré/rapide à l'écran. La caméra contribue à cette vitesse ; ce ne sont pas des distances en mètres ou des vitesses physiques.
- Corrections calibrées, lissées et bornées (24 comptes maximum par image et 600 comptes/s). Réduction prudente du gain lorsqu'il y a des changements de signe répétés dans l'erreur de visée. Cela ne prouve pas que l'assistance était la cause de ces oscillations.
- Sauvegarde des sous-profils par réglages, modèle, compte R6 local et méthode de souris. Les profils du PC d'un ami restent sur son PC.
- Sélection locale d'exemples confiants, incertains et sans détection, avec statut **non vérifié**. Une prédiction répétée ou confiante n'est jamais automatiquement déclarée correcte.
- À la fermeture de la revue, si les exemples sont suffisants, comparaison automatique des seuils du modèle actuel ; le meilleur seuil du jeu d'entraînement doit aussi améliorer le jeu de validation indépendant avant application à la prochaine session.

La calibration mesure des pixels de déplacement de la cible par compte de souris dans une vue donnée. Elle ne mesure ni les DPI physiques ni les degrés. Le registre de souris peut contenir des mouvements générés pendant l'assistance : les échantillons indiquent si cette assistance était activée. Le rapport passif historique est arrêté avant de démarrer ce mode.

## Améliorer le détecteur

Dans **Vérifier les images**, enlever les fausses boîtes par clic droit et dessiner les cibles oubliées par glisser gauche. Choisir la classe avant de dessiner. Valider explicitement toute l'image, ou confirmer qu'elle ne contient aucune cible. Une image douteuse peut être passée.

Il faut au moins 40 images vérifiées provenant du modèle actif, dans au moins deux sessions : 20 pour l'entraînement/configuration et 20 pour la validation, dont au moins 20 cibles dans cette dernière. Ce minimum permet une première comparaison, pas une preuve universelle de qualité. Les sessions sont séparées en entier pour éviter d'utiliser des images presque identiques des deux côtés. Les doublons exacts sont exclus.

Le score utilisé est F1 à IoU 0,5. Une amélioration d'au moins 0,01 est demandée, sans baisse de précision ou rappel supérieure à 0,01. Le changement de modèle vérifie aussi le temps moyen d'inférence local. Les classes doivent correspondre exactement. Les mesures de comparaison incluent le prétraitement et ne sont pas un benchmark FPS en jeu. Les configurations multi-classes exigent de garder une sélection de classe cohérente avec l'évaluation ; les modèles R6 à classe unique évitent cette ambiguïté.

**Comparer un modèle ONNX** évalue un candidat puis conserve une copie du modèle précédent. **Revenir au modèle précédent** restaure cette sélection. L'application vérifie le chargement et conserve les fichiers source.

**Entraîner et comparer localement** est un parcours optionnel. Il nécessite les poids source PyTorch `.pt` de confiance correspondant au modèle, un Python local avec PyTorch, Ultralytics, ONNX et Pillow. Importer les poids une fois avec le bouton dédié les lie au modèle courant. Le programme n'exécute jamais un `.pt` simplement parce qu'il existe. Le moteur cherche Python dans `data/learning/runtime/python.exe` puis dans PATH (hors alias Windows Store). Il n'installe pas ces dépendances et ne télécharge aucun poids.

Le runner empaqueté interdit le réseau, entraîne sur les seules données d'entraînement, exporte un candidat et le compare au modèle initial sur les sessions réservées. Une annulation ou un candidat refusé conserve l'ancien modèle. Sur le PC utilisé pour développer cette version, les poids `.pt` et le moteur d'entraînement manquent : **aucun réentraînement réel n'a été réalisé**. Le format natif d'entraînement et les exports sont décrits dans la [documentation Ultralytics](https://docs.ultralytics.com/modes/train/).

## Limites et données

Cette version ne reconnaît pas de façon fiable l'arme, le zoom équipé, l'état ADS réel, les impacts ou le recul isolé du mouvement du joueur. Une image ne donne pas les DPI physiques. La lecture de souris expose un déplacement relatif, comme décrit par [Microsoft RAWMOUSE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawmouse). La touche de visée est un état de touche, pas une preuve visuelle d'ADS. Le processus R6 ne prouve pas à lui seul le mode de jeu ; ce parcours est prévu pour les tests contre IA.

Le modèle ne peut pas corriger automatiquement toutes ses propres erreurs sans exemples fiables. La revue humaine reste nécessaire pour distinguer faux positifs, cibles manquées et bonnes détections. Les sous-profils et le seuil sont personnalisés ; la qualité réelle doit être mesurée sur les sessions du joueur.

Les enregistrements sont dans `data/local-capture`, les profils dans `data/local-profiles`, et les images vérifiées, comparaisons et éventuels modèles dans `data/learning`. La capture s'arrête à 512 Mio par session, 2 Gio cumulés, 30 minutes ou manque d'espace ; elle ne supprime pas les fichiers précédents. Les images de revue sont plafonnées séparément à 2 000 et 512 Mio. Les copies de jeux de données/modèles prennent de l'espace supplémentaire. Aucun de ces fichiers n'est publié avec les mises à jour.

Les calculs se font sur le PC. Seul le lanceur habituel consulte GitHub pour les mises à jour ; `AutoAimmy-hors-ligne.cmd` évite cette vérification réseau.

## Vérification de cette version

Tests locaux : 55 mesures historiques, 243 contrôles adaptatifs dont simulations de boucle fermée à 20–120 FPS, 113 vérifications de suppression des doublons, 22 vérifications d'enregistrement et quotas, 38 contrôles d'apprentissage incluant une vraie inférence R6 ONNX sur images synthétiques, 6 tests du runner Python, 17 contrôles de mise à jour et suites d'import/profils/lanceur. L'interface est rendue et inspectée avec des données de test. Le test de fermeture utilise un contexte UI pour détecter les blocages. Les tests ne génèrent aucun mouvement de souris et n'enregistrent pas de partie réelle.
