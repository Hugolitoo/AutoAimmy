# AutoAimmy 0.3.0 — guide du test local

Cette version automatise la collecte, la mesure de la caméra, les sous-profils et les calculs d'apprentissage sur votre PC. L'assistance reste expérimentale et prévue pour les tests R6 contre IA. Une comparaison sur des images vérifiées peut montrer un gain local ; elle ne garantit pas le meilleur réglage possible ni une amélioration dans toutes les parties.

## Ce que tu dois faire

1. **Mettre à jour et ouvrir l'application.** Fermer Aimmy, lancer `Mettre-a-jour.cmd`, puis `AutoAimmy.cmd`. Pour une première installation, extraire entièrement `AutoAimmy-win-x64.zip`. Aucun SDK, Python ou logiciel de souris supplémentaire n'est à installer manuellement.
2. **Charger le modèle habituel.** Le placer dans `data/bin/models` et le choisir dans **Modèles**. Ouvrir **AUTO** pour voir l'état réel de la session, de la calibration et de l'apprentissage. Les réglages R6 sauvegardés sont lus automatiquement. Avec plusieurs configurations, choisir une fois le bon ensemble ; il n'est pas nécessaire de retaper DPI, sensibilité ou lunette.
3. **Laisser la collecte automatique activée.** Avec un modèle chargé, elle démarre lorsque R6 est au premier plan sur le moniteur choisi. Le bouton **Démarrer la session locale** reste disponible. Le passage sur une autre fenêtre suspend les images et finit la session automatique après environ dix secondes : cela permet de lancer les calculs hors du jeu.
4. **Faire la calibration initiale.** Cliquer **Calibration guidée (45 s max)**, retourner dans R6 pendant le compte à rebours, conserver la même vue et maintenir la touche de visée définie dans Aimmy. Devant un décor visible et une cible immobile, déplacer doucement la souris gauche/droite puis haut/bas, dans les deux sens. Ne pas marcher ni tirer. Le but est de mesurer combien l'image se déplace pour un mouvement de ta souris.
5. **Activer l'assistance si tu veux la tester.** Attendre une calibration validée, puis cliquer **Activer l'assistance expérimentale**. Elle nécessite R6 au premier plan, la touche de visée maintenue et une cible confirmée. **F8** coupe l'assistance. Après un arrêt F8, elle ne se réactive pas automatiquement. Une reprise après une pause attend une nouvelle mesure cohérente de la vue.
6. **Faire deux sessions différentes, puis vérifier des images.** Quelques minutes par session suffisent pour commencer à collecter ; le nombre d'images utilisables compte plus que la durée. Revenir dans AUTO, attendre la fin de collecte ou cliquer **Arrêter la session**, puis **Vérifier les images**. Le minimum est 40 images vérifiées, avec au moins 20 images de chaque côté de la comparaison et au moins 20 cibles annotées dans la validation. Deux sessions distinctes évitent de comparer le modèle sur les mêmes scènes presque identiques.
7. **Laisser l'application ouverte pendant une pause.** Laisser **Comparer et apprendre automatiquement lorsque R6 est en pause** activé. Ici, « pause » signifie que **R6 n'est plus la fenêtre au premier plan** ; rester dans le menu du jeu ne suffit pas. L'application importe les exemples, compare le seuil de détection, prépare le moteur local si nécessaire, entraîne un candidat puis mesure son résultat. Revenir dans R6 interrompt le calcul ; il pourra être relancé à la prochaine pause.

Il n'y a aucun rapport à envoyer pour ces étapes. Les compteurs et messages dans AUTO indiquent ce qui manque et le résultat du dernier essai.

## Vérifier les images : la petite étape encore nécessaire

Dans **Vérifier les images**, retirer une fausse boîte par clic droit et dessiner une cible oubliée en faisant glisser le bouton gauche. Choisir la bonne classe avant de dessiner. Valider toute l'image, ou confirmer explicitement une image sans cible. Passer les images ambiguës.

Les détections confiantes et les cas difficiles sont sauvegardés comme **propositions non vérifiées**. Des cadres stables, du sang ou un marqueur de touche ne prouvent pas à eux seuls que toutes les cibles d'une image sont correctement annotées. Cette version garde donc une validation humaine avant de décider qu'un détecteur est meilleur. Cela évite d'entraîner et d'accepter un modèle qui reproduit simplement ses propres erreurs.

Les sessions sont séparées en entier entre entraînement et validation ; les doublons exacts sont exclus. Les images de validation ne sont pas fournies au programme d'entraînement, même pour choisir son meilleur point de sauvegarde. Les annotations peuvent être réutilisées dans la descendance d'un modèle accepté si les classes correspondent exactement.

## Ce que le système mesure automatiquement

| Élément | Mesure et comportement |
| --- | --- |
| Caméra et cible | Le décor donne une estimation de translation et de variation d'échelle. Quand elle est fiable, son déplacement est retiré de la vitesse de la cible et sert à une courte anticipation bornée. Un décor uniforme, de fortes rotations ou des effets peuvent rendre cette mesure indisponible. |
| Vue, sensibilité et zoom | Les mouvements de souris sans tir, déplacement du personnage ou sortie générée servent à remesurer la réponse de caméra. Deux mesures cohérentes peuvent sélectionner un profil de vue. La mesure décrit une réponse en pixels par compte de souris, pas des DPI ou le nom d'une lunette. |
| Sous-profils | Neuf catégories initiales de taille apparente et de mouvement peuvent être complétées par des situations observées, jusqu'à 36 profils. La taille d'une boîte n'est pas une distance en mètres. |
| Gain et lissage | De petites variantes sont comparées sur des fenêtres de suivi d'une même cible dans un contexte cohérent. Un ajustement est conservé seulement après une amélioration suffisante du score mesuré. Ce score décrit l'erreur de suivi et les oscillations ; ce n'est pas un pourcentage de touches. |
| Tirs et recul | La lecture cohérente d'une baisse de munitions donne une estimation des tirs. Des fenêtres sans mouvement du personnage ni assistance peuvent associer cette baisse au déplacement résiduel de caméra pour estimer le recul de la vue. Une lecture ambiguë du HUD ne suffit pas pour une correction spécifique. |
| Sang et marqueur central | Des changements de rouge sur la cible et un bref marqueur lumineux autour du viseur servent d'indices probables. La forme réelle du marqueur R6 reste à valider sur vos images ; une absence de détection ne signifie pas une balle manquée. Ces indices ne constituent ni un compteur garanti d'impacts ni des annotations automatiquement fiables. |
| Qualité du détecteur | Le seuil puis le candidat ONNX sont comparés aux réglages et au modèle précédents sur les images vérifiées de validation. Un refus conserve la sélection validée. |
| Régression du modèle | Après une promotion, de nouvelles images vérifiées peuvent comparer le modèle actif au précédent. Une baisse suffisante du score entraîne un retour au précédent. Sans nouvelles annotations fiables, cette surveillance ne peut pas conclure. |

La calibration guidée reste disponible si le décor ne permet pas la remesure automatique. Un changement de DPI physique n'est pas toujours visible immédiatement. En cas de réponse incohérente, arrêter avec F8 et refaire la calibration dans la vue utilisée.

## Entraînement local du modèle existant

Le premier entraînement télécharge automatiquement **AutoAimmy-learning-win-x64.zip**, environ **284 Mio**, depuis la release AutoAimmy. Son contenu fournit Python, PyTorch CPU, Ultralytics, ONNX et les dépendances. Aucun compte payant, abonnement ou service d'analyse distant n'est utilisé. Au moins 2 Gio d'espace libre sont demandés pour l'installation ; les données et modèles d'entraînement prennent de la place supplémentaire. Le téléchargement peut reprendre après interruption.

Le modèle ONNX habituel reste utilisable pour détecter et comparer les seuils. Pour le réentraîner, le programme tente de reconstruire les poids des exports **YOLOv8 de détection classiques compatibles**, avec entrée 640 × 640 et poids inclus dans le fichier. Il vérifie l'architecture et compare réellement les sorties de la reconstruction à celles de l'ONNX. Un format incompatible est refusé avec une explication ; il n'est pas remplacé par un modèle différent. Dans ce cas, **Alternative : importer un .pt** permet de fournir les poids source correspondants, s'ils sont de confiance.

La reconstruction ne modifie pas le modèle et ne l'améliore pas en elle-même. Elle permet d'entraîner une copie. L'entraînement courant est limité à dix époques sur CPU ; sa durée dépend du PC et du nombre d'images. Quand le jeu revient au premier plan, le travail est annulé et peut être recommencé ensuite ; ce n'est pas une reprise garantie au milieu d'une époque. Les détections du jeu continuent d'utiliser le modèle chargé jusqu'à l'acceptation et au chargement d'un candidat à la fin du calcul, hors session active.

L'application demande un gain F1 d'au moins 0,01 à IoU 0,5, sans baisse de précision ou rappel supérieure à 0,01 ; la comparaison des modèles vérifie aussi le temps d'évaluation local. Ce temps inclut le chargement d'image et le prétraitement, ce n'est pas un chiffre de FPS dans R6. Les noms et l'ordre des classes doivent rester identiques. **Revenir au modèle précédent** reste disponible dans AUTO.

Les poids issus d'un candidat accepté sont enregistrés comme source du cycle suivant. Un nouvel essai refusé conserve le dernier seuil et le modèle acceptés. Pour la surveillance ultérieure, toutes les images de validation utilisées doivent être postérieures à la promotion, avec au moins 40 cibles annotées ; une baisse F1 de plus de 0,03 par rapport au précédent déclenche le retour arrière.

## Comprendre les états courants

| Message ou situation | Action utile et raison |
| --- | --- |
| En attente d'un modèle | Charger l'ONNX dans Modèles pour démarrer la détection. |
| Calibration nécessaire ou décor insuffisant | Faire la calibration guidée dans la vue utilisée. L'application manque de mesures cohérentes pour convertir les corrections en mouvements de souris. |
| En attente : …/40 images vérifiées | Vérifier des images de deux sessions. Le volume enregistré seul ne fournit pas d'annotations fiables. |
| Calculs en pause pendant le jeu | Revenir dans AUTO ou une autre fenêtre pour libérer le CPU et autoriser l'entraînement. |
| Derniers exemples déjà comparés | Jouer une nouvelle session puis vérifier de nouvelles images. Refaire indéfiniment le même essai ne fournit pas une nouvelle preuve. |
| Candidat refusé | Continuer avec le modèle conservé. Un entraînement terminé n'est pas nécessairement meilleur. |
| Source non reconstructible | Utiliser les poids source `.pt` de confiance correspondant au modèle, ou continuer la détection et l'optimisation de seuil sans réentraînement. |
| Quota de capture atteint | Examiner les enregistrements et archiver manuellement ceux que tu souhaites conserver avant de libérer de la place. L'application ne supprime pas les sessions précédentes. |

## Capture DirectX, stockage et réseau

Le détecteur, le HUD et l'enregistreur utilisent maintenant un même service de capture DirectX synchronisé. Cela corrige le cas de plusieurs duplications de la même sortie à l'origine de l'erreur `E_INVALIDARG / 0x80070057`. Une erreur de pilote ou un mode d'affichage incompatible reste possible : vérifier le moniteur choisi, fermer puis relancer après un changement d'écran et essayer la méthode de capture GDI proposée dans Aimmy si DirectX échoue encore.

L'enregistrement est une séquence d'images JPEG, jusqu'à 5 images/s, largeur maximale 1280 px, accompagnée d'une lecture animée locale. Les exemples du détecteur conservent son carré d'entrée. Il n'y a ni audio ni MP4 haute fréquence. La collecte s'arrête à 512 Mio par session, 2 Gio cumulés, 30 minutes ou manque d'espace. Les images de revue sont limitées séparément à 2 000 images et 512 Mio. Les copies de jeux de données et de modèles nécessitent de l'espace supplémentaire.

Les captures sont dans `data/local-capture`, les profils dans `data/local-profiles`, et les images de revue, le moteur, les comparaisons et les modèles dérivés dans `data/learning`. Les mises à jour préservent `data`. Aucun enregistrement, profil ou modèle du joueur n'est inclus dans les publications GitHub.

Le lanceur habituel utilise GitHub pour les mises à jour. Le premier téléchargement du moteur utilise aussi GitHub. Ensuite, l'entraînement et l'analyse se font sur le PC et le programme d'entraînement bloque le réseau. **AutoAimmy-hors-ligne.cmd** désactive seulement la vérification du lanceur : pour une première utilisation sans aucune connexion, désactiver l'apprentissage automatique tant que le moteur n'est pas installé. On peut aussi extraire le ZIP du moteur directement dans `data/learning/runtime` : `python.exe` doit se trouver à la racine de ce dossier.

## Ce qui a été vérifié

Les contrôles logiciels couvrent la capture partagée et sa durée de vie, le suivi des cibles à différents débits, la calibration, les mouvements synthétiques du décor, les profils, les comparaisons et le maintien du dernier résultat accepté. Le modèle R6 local utilisé pendant le développement a été reconstruit avec comparaison numérique de ses sorties. Un entraînement réel et un export ONNX ont ensuite été exécutés sur un jeu d'images synthétiques de test ; la comparaison indépendante a refusé ce candidat faute de gain. Les données et ce modèle de test n'ont pas été distribués.

Ces contrôles valident le parcours logiciel. Ils ne remplacent pas une mesure sur vos séquences R6 et ne prouvent pas une amélioration réelle de la visée, des impacts ou de la détection dans le jeu.
