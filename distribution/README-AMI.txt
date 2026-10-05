AUTOAIMMY — TEST OFFLINE / AIM TRAINER

1. Extraire TOUT le ZIP dans un dossier, par exemple Documents\AutoAimmy.
2. Double-cliquer AutoAimmy.cmd. Aucun SDK ni runtime a installer.
   Au premier lancement : N cree un profil avec vos reglages, S garde les valeurs inconnues.
   Ce questionnaire ne revient pas a chaque session. Profil-joueur.cmd change le profil actif.
3. Mettre le modele ONNX du trainer dans data\bin\models.
4. Ouvrir le trainer offline et charger le modele dans AutoAimmy.
5. Verifier OBSERVING. L'application ne commande pas la souris.
   Lors du premier lancement apres la mise a jour 0.1.2 : choisir 2 pour un viseur fixe au centre.
   Choisir le moniteur du trainer dans Aimmy, et utiliser le trainer en plein ecran.
   Afficher les cadres de detection et verifier qu'ils suivent les bonnes cibles avant le test.
   Les cadres indiquent des detections du modele, pas des hits confirmes.
6. Jouer la session prevue puis fermer AutoAimmy normalement.
7. Le ZIP du rapport est cree automatiquement dans exports en fin d'observation.
   Envoyer ce ZIP manuellement. Exporter-rapport.cmd reste disponible si necessaire.

Les reglages et sessions sont conserves dans data. Ne jamais supprimer ce dossier.
Les rapports ne sont jamais envoyes automatiquement.
Le profil actif (reglages declares) est copie dans chaque nouveau rapport via context.json.
Changer de profil si vous changez d'arme, de lunette ou de reglages ; l'application ne les lit pas dans le jeu.
Pour changer de reference, lancer Configurer-viseur.cmd (nouvelle installation),
ou modifier AimReference dans data\adaptive.json pour une ancienne installation.
1 = curseur mobile ; 2 = viseur fixe au centre. Aucun reglage angulaire n'est devine.

Les prochaines mises a jour sont installees automatiquement au demarrage si GitHub est accessible.
Pour rester sur la version actuelle, utiliser AutoAimmy-hors-ligne.cmd.
Depuis 0.1.5, le raccourci Profil-joueur.cmd est repare meme lors d'une mise a jour seule.
Dans le menu des profils, C copie les reglages du profil actif : seuls le nom, l'arme,
la lunette et sa sensibilite ADS sont demandes. Verifier que les autres reglages restent identiques.
Installation 0.1.1 ou 0.1.2 : telecharger Reparer-mise-a-jour.cmd depuis la release 0.1.3,
le mettre a cote de l'ancien AutoAimmy.cmd, fermer Aimmy puis double-cliquer la reparation.
Une sauvegarde de l'updater est conservee. Accepter la mise a jour, puis relancer AutoAimmy.cmd.
Sans Internet, utiliser AutoAimmy-hors-ligne.cmd.
Pour revenir a la version precedente : fermer AutoAimmy puis Retour-version-precedente.cmd.
Apres un retour en arriere, utiliser le lancement hors ligne pour ne pas reprendre la mise a jour.

Pour un depot GitHub prive : votre compte doit avoir acces au depot.
Connexion-GitHub.cmd enregistre votre propre jeton d'acces en le chiffrant pour votre compte Windows.
Le jeton doit seulement avoir Contents: Read sur le depot AutoAimmy.
Ne partagez jamais data\github-access.xml. Un depot public ne demande pas de connexion.

Les mises a jour ne modifient pas les donnees du joueur. Le rollback restaure le programme,
pas les changements eventuels de format des donnees d'une future version.
