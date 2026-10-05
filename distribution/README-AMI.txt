AUTOAIMMY 0.2.0 — SESSION LOCALE EXPERIMENTALE

1. Extraire tout le ZIP, puis ouvrir AutoAimmy.cmd.
   Pour une installation existante : Mettre-a-jour.cmd puis fermer/relancer.
2. Placer votre ONNX dans data\bin\models et le charger dans Modeles.
3. Ouvrir AUTO puis Demarrer la session locale.
4. Calibration guidee : cible immobile, sans marcher ni tirer, meme vue.
   Maintenir la touche de visee configuree dans Aimmy et faire de petits
   mouvements gauche/droite puis haut/bas dans les deux sens.
5. Quand la calibration est validee, activer l'assistance experimentale.
   Elle agit avec la touche de visee maintenue. F8 l'arrete.
   Recalibrer apres changement de DPI, souris, sensibilite ou zoom.
6. Arreter la session puis Verifier les images dans AUTO.
   Corriger les boites (clic droit = enlever, glisser gauche = ajouter),
   puis valider. Les comparaisons restent sur votre PC, sans ZIP a envoyer.

La capture enregistre jusqu'a 5 images/s et offre une lecture animee locale.
Ce n'est pas une video MP4 haute frequence. Maximum 512 Mio/session,
2 Gio au total et 30 minutes, puis arret de la collecte sans suppression.
Les sous-profils changent selon taille et mouvement des cibles a l'ecran.
Les DPI physiques, les vrais impacts et le recul isole ne sont pas reconnus.

Un minimum de 40 images verifiees dans deux sessions est requis pour mesurer
une nouvelle configuration. Les propositions du modele ne sont jamais
considerees automatiquement comme des annotations correctes.
Le reentrainement des poids exige le fichier source .pt de confiance et
Python/PyTorch/Ultralytics/ONNX/Pillow locaux. Ils ne sont pas fournis.
Aucun poids du modele n'a ete reentraine ou prouve meilleur dans cette version.

Tout le traitement et les donnees de jeu restent sur ce PC.
Les mises a jour consultent GitHub ; AutoAimmy-hors-ligne.cmd ignore le reseau.
Retour-version-precedente.cmd permet de revenir a l'ancienne application.
Ne pas supprimer data : modeles, profils et enregistrements y sont conserves.
Guide complet : https://github.com/Hugolitoo/AutoAimmy/blob/main/docs/Local-Automation-0.2.md