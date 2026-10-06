AUTOAIMMY 0.3 - GUIDE DU TEST LOCAL

1. Extraire tout AutoAimmy-win-x64.zip, puis ouvrir AutoAimmy.cmd.
   Installation existante : fermer Aimmy, lancer Mettre-a-jour.cmd,
   puis rouvrir AutoAimmy.cmd. Ne pas supprimer data.
   Aucun SDK ni Python a installer manuellement.
2. Placer le modele ONNX habituel dans data\bin\models et le charger
   dans Modeles. Aucun modele personnel n'est inclus dans le ZIP.
3. Ouvrir AUTO. Laisser la collecte automatique activee, puis revenir
   dans R6 sur le moniteur choisi. Elle demarre avec le jeu au premier plan.
4. Faire Calibration guidee : meme vue, cible immobile et decor visible,
   sans marcher ni tirer. Maintenir la touche de visee definie dans Aimmy
   et faire de petits mouvements gauche/droite puis haut/bas, dans les
   deux sens. La mesure sert a adapter les corrections a votre souris.
5. Une fois la calibration validee, activer l'assistance experimentale.
   Elle agit avec la touche de visee maintenue. F8 l'arrete.
   Le decor permet ensuite de remesurer la reponse et de choisir un profil
   de vue. Si la mesure reste incertaine, refaire la calibration guidee.
6. Faire deux sessions differentes. Revenir dans AUTO : la session
   automatique se termine apres environ 10 secondes hors de R6.
   Choisir Verifier les images : clic droit pour retirer une fausse boite,
   glisser gauche pour ajouter une cible, puis valider toute l'image.
   Verifier au moins 20 images de chaque session, dont au moins 20 cibles
   dans la session de validation. Une image vide peut etre validee aussi.
7. Laisser l'apprentissage automatique active et l'application ouverte
   pendant une pause, avec R6 hors du premier plan. L'application compare
   les seuils, prepare le moteur, entraine et evalue un candidat.
   Le calcul est interrompu quand R6 revient au premier plan.
   Un candidat refuse conserve le modele et le seuil deja acceptes.

POURQUOI ENCORE VERIFIER DES IMAGES ?
Le modele propose des cadres ; ses predictions seules ne prouvent pas
qu'ils sont corrects. Il faut au moins 40 images verifiees de deux sessions
pour comparer sur des exemples distincts. Aucun ZIP n'est a envoyer.

MOTEUR D'APPRENTISSAGE
Telechargement automatique unique d'environ 284 Mio depuis GitHub.
Prevoir au moins 2 Gio libres pour l'installation, plus les donnees.
Ensuite, analyse et entrainement restent sur ce PC, sans service payant.
Les exports YOLOv8 compatibles peuvent etre reconstruits depuis l'ONNX
apres comparaison de leurs sorties. Pour un export incompatible, les
poids source .pt de confiance restent necessaires pour reentrainer.
Un entrainement termine ne prouve pas une amelioration dans vos parties.

ENREGISTREMENT ET PROFILS
Lecture animee locale jusqu'a 5 images/s, sans audio ; pas de MP4.
Maximum 512 Mio/session, 2 Gio cumules et 30 minutes, puis arret de collecte.
Les sous-profils utilisent la taille apparente et le mouvement des cibles.
Les DPI physiques ne sont pas lisibles dans l'image du jeu.
Les reglages R6 sauvegardes sont importes sans questionnaire ; si plusieurs
comptes existent, choisir une fois le bon ensemble de reglages dans AUTO.

DONNEES ET RETOUR ARRIERE
Les captures, profils et modeles restent dans data, sans envoi automatique.
Retour-version-precedente.cmd restaure l'ancienne application.
AutoAimmy-hors-ligne.cmd ignore la verification des mises a jour.
Le premier telechargement du moteur demande une connexion : pour rester
entierement hors ligne avant son installation, desactiver l'apprentissage
automatique. Le ZIP du moteur peut etre extrait dans data\learning\runtime
avec python.exe directement dans ce dossier.

Guide complet :
https://github.com/Hugolitoo/AutoAimmy/blob/main/docs/Local-Automation-0.3.md
