AUTOAIMMY — TEST OFFLINE / AIM TRAINER

1. Extraire TOUT le ZIP dans un dossier, par exemple Documents\AutoAimmy.
2. Double-cliquer AutoAimmy.cmd. Aucun SDK ni runtime a installer.
3. Mettre le modele ONNX du trainer dans data\bin\models.
4. Ouvrir le trainer offline et charger le modele dans AutoAimmy.
5. Verifier OBSERVING. L'application ne commande pas la souris.
6. Jouer 2-3 minutes puis fermer AutoAimmy. Ensuite tester 10 minutes.
7. Double-cliquer Exporter-rapport.cmd et envoyer le ZIP dans exports.

Les reglages et sessions sont conserves dans data. Ne jamais supprimer ce dossier.
Les rapports ne sont jamais envoyes automatiquement.

Une nouvelle version est proposee au demarrage si GitHub est accessible.
Sans Internet, utiliser AutoAimmy-hors-ligne.cmd.
Pour revenir a la version precedente : fermer AutoAimmy puis Retour-version-precedente.cmd.
Apres un retour en arriere, utiliser le lancement hors ligne pour ne pas reprendre la mise a jour.

Pour un depot GitHub prive : votre compte doit avoir acces au depot.
Connexion-GitHub.cmd enregistre votre propre jeton d'acces en le chiffrant pour votre compte Windows.
Le jeton doit seulement avoir Contents: Read sur le depot AutoAimmy.
Ne partagez jamais data\github-access.xml. Un depot public ne demande pas de connexion.

Les mises a jour ne modifient pas les donnees du joueur. Le rollback restaure le programme,
pas les changements eventuels de format des donnees d'une future version.
