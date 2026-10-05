AUTOAIMMY 0.1.8 — COMPTES R6 / LECTURE EXPERIMENTALE DU HUD

1. Extraire TOUT le ZIP dans un dossier, par exemple Documents\AutoAimmy.
2. Double-cliquer AutoAimmy.cmd. Aucun SDK ni runtime a installer.
   Aucun questionnaire : les reglages R6 sont lus depuis GameSettings.ini dans Documents.
   Profil-joueur.cmd relit et affiche ces reglages, sans saisie manuelle.
   La page AutoAimmy affiche maintenant Imported et les reglages dans l'application.
   Le bouton AUTO dans la barre de gauche ouvre cette page.
   Relire les reglages R6 : actualiser sans questionnaire, hors enregistrement.
   Ouvrir les modeles / rapports : acceder directement aux dossiers utiles.
3. Mettre le modele ONNX dans data\bin\models puis le charger dans l'application.
4. Choisir le moniteur utilise et verifier que les cadres suivent les bonnes cibles.
5. Jouer la session de test contre les IA puis fermer normalement AutoAimmy.
6. Le rapport ZIP est cree automatiquement dans exports ; envoyer ce ZIP manuellement.

Lecture automatique : sensibilites horizontale/verticale, ADS par grossissement,
multiplicateurs, FOV, resolution et code du ratio. Les valeurs non reconnues restent inconnues.
DPI materiel, arme equipee, lunette equipee et etat ADS reel ne sont pas encore detectes.
Le fichier contient les reglages sauvegardes, pas necessairement les valeurs actives a cet instant.
Apres un changement dans le jeu : enregistrer les reglages puis relancer AutoAimmy.
Si plusieurs comptes sont trouves, le dernier fichier sauvegarde est affiche comme candidat.
Verifier les valeurs : choisir le bon ensemble dans la liste puis Utiliser ces reglages.
Ce choix est memorise ; aucune sensibilite n'est a retaper.

La lecture du HUD demarre automatiquement quand R6 est au premier plan.
Elle reconnait du texte explicite, pas les icones seules. Deux lectures coherentes sont requises.
Minimiser Aimmy et jouer ; la page montre la date et le dernier texte reconnu.
DPI materiel et etat ADS reel restent inconnus. La reconnaissance n'est pas encore validee
sur votre HUD : les resultats sont experimentaux, pas des detections garanties.
visual-events.jsonl accompagne le rapport. En mode OCR normal, aucune image ni transcription sauvegardee.
Pour preparer la reconnaissance d'icones et de visee, cliquer Collecter 20 images pour validation,
remettre R6 au premier plan et alterner avec/sans visee pendant environ 20-40 secondes.
Ce clic sauvegarde localement 20 paires centre/HUD et cree AutoAimmy-validation-...zip dans exports.
Envoyer ce ZIP manuellement. Les images restent non annotees ; aucune reconnaissance ADS n'est encore activee.
Windows 10 version 2004 ou Windows 11 et une langue OCR Windows sont requis.

Observation uniquement : l'application ne commande pas la souris et ne reentraine pas le modele.
Les reglages et sessions restent dans data. Ne jamais supprimer ce dossier.
Les anciens profils sont conserves ; leurs valeurs ne remplacent pas les valeurs inconnues.
Aucun rapport n'est envoye automatiquement. L'export contient seulement les mesures
et le contexte autorise, sans chemin de compte ni fichier complet du jeu.
Exporter-rapport.cmd reste disponible si la creation automatique du ZIP echoue.

Les mises a jour se font automatiquement au demarrage si GitHub est accessible.
AutoAimmy-hors-ligne.cmd conserve la version installee et saute la verification reseau.
Mettre-a-jour.cmd permet une mise a jour seule. Relancer une fois apres le passage
d'une ancienne version pour executer le nouveau lanceur.
Retour-version-precedente.cmd revient a l'ancienne version sans supprimer data.
Apres rollback, utiliser le lancement hors ligne pour conserver cette version.
Installation 0.1.1/0.1.2 : placer Reparer-mise-a-jour.cmd a cote d'AutoAimmy.cmd,
fermer l'application puis executer la reparation avant la mise a jour normale.

Pour un depot public, aucun compte GitHub ni jeton n'est necessaire.
Ne jamais partager data\github-access.xml si une connexion privee a ete configuree.
