# Reglages automatiques — 0.1.6

## Page AutoAimmy — 0.1.7

La page AutoAimmy s'ouvre au lancement en mode observation. Le bouton AUTO dans la barre de gauche y revient. Le resultat Imported ou le motif d'indisponibilite est visible dans l'application, avec sensibilites, FOV, resolution, valeurs ADS et date de modification du fichier source. Les DPI, l'arme, la lunette equipee et l'etat ADS reel sont explicitement inconnus.

La page montre aussi le modele, la reference du viseur, la taille du moniteur, l'etat de l'observation et le temps ecoule. Relire les reglages R6 relance l'import sans questionnaire. Pendant un enregistrement, ce bouton est desactive et les reglages affiches correspondent au contexte fige de la session. Une relecture apres la session ne modifie pas ses mesures.

Ouvrir les modeles ouvre data/bin/models. Ouvrir les rapports ouvre exports ; Afficher le dernier ZIP selectionne le rapport le plus recent dans l'Explorateur. Sans rapport, ce dernier bouton est desactive. Les dossiers et rapports restent locaux. L'application ne filme pas automatiquement le jeu.

Aucun questionnaire au lancement. AutoAimmy cherche GameSettings.ini dans Documents Windows (y compris Documents redirige dans OneDrive) puis dans Documents du profil Windows, sous My Games/Rainbow Six - Siege. Il lit uniquement ce fichier et ne le modifie jamais.

Quand un seul fichier est trouve, les champs reconnus et valides sont importes : sensibilites horizontale et verticale, multiplicateurs souris/ADS, sensibilites ADS globales et par grossissement, activation des sensibilites ADS specifiques, FOV et resolution. Le code enum du ratio est conserve sous AspectRatioSetting ; aucune correspondance non verifiee vers un ratio textuel n'est inventee.

La lecture se fait a chaque lancement et avec Profil-joueur.cmd. Ce raccourci ne demande plus de saisir les reglages : il relit la configuration et affiche le resultat. Le profil automatique est stocke dans data/active-profile.json. L'ancien profil actif est sauvegarde et les anciens profils dans data/profiles sont conserves, mais leurs armes, lunettes et DPI declares ne sont pas repris automatiquement.

## Informations encore inconnues

Le DPI materiel, l'arme equipee, la lunette equipee et l'etat ADS reel restent inconnus. Tous les reglages ADS disponibles sont conserves, sans choisir un grossissement suppose. La configuration sur disque peut etre ancienne ou differer de changements non enregistres dans le jeu. Sa date de modification est jointe au contexte ; aucune verification des valeurs actives en memoire n'est effectuee. Apres un changement, enregistrer les reglages dans le jeu puis relancer AutoAimmy. Pas d'injection ni de lecture de la memoire du jeu.

Depuis 0.1.8, si plusieurs fichiers sont trouves, le plus recent est affiche comme candidat : ImportStatus=ImportedCandidate. Ce choix ne prouve pas quel compte est actif. La page propose la liste des configurations avec leurs sensibilites/FOV/resolution et date, sans identifiant de compte. Choisir le bon ensemble puis Utiliser ces reglages memorise ce choix. Aucun nombre n'est a retaper. Les clefs locales et chemins de comptes ne sont pas inclus dans le contexte du rapport. Si aucun fichier n'est trouve, ImportStatus=NotFound. Une valeur invalide ou un fichier inaccessible reste inconnu.

## Rapports et mises a jour

Au chargement du modele, le contexte courant est copie dans context.json. SettingsSource=SettingsFile distingue ces valeurs des anciens rapports UserDeclared. Seuls les champs autorises sont exportes, sans chemin de compte, identifiant de compte, contenu complet du fichier ou donnees ONLINE. Les sessions deja enregistrees ne changent pas.

Le ZIP du rapport est cree automatiquement dans exports en fin d'observation ou a la fermeture normale. Exporter-rapport.cmd permet de reessayer si l'export echoue. Aucun envoi automatique. Les mises a jour restent automatiques au demarrage ; AutoAimmy-hors-ligne.cmd permet de conserver la version.

## Lecture en direct — 0.1.8, experimentale

Le lecteur local Windows OCR demarre automatiquement en mode observation, independamment du modele de cibles. Il traite uniquement le coin inferieur droit de la fenetre R6 au premier plan, sur le moniteur selectionne, au maximum une fois par seconde. Les autres applications au premier plan suspendent la lecture ; un mauvais moniteur est signale. Deux lectures consecutives identiques sont necessaires pour afficher un nom d'arme ou un grossissement reconnu par le parseur.

Ces resultats sont du texte reconnu, pas une preuve de l'arme/lunette equipee. Si le HUD montre seulement une icone ou aucun nom, le resultat reste inconnu. La liste de noms pris en charge est limitee. En pause, le dernier texte peut rester visible, avec la date de l'image et la mention qu'il ne s'agit pas d'une detection actuelle. Aucun etat ADS n'est deduit d'un clic droit, du curseur ou des sensibilites sauvegardees.

Le DPI materiel ne figure pas dans une image de jeu. Une integration du logiciel du fabricant est necessaire pour le recuperer automatiquement. La lecture d'icones et des transitions ADS reste a developper et valider sur des images de jeu annotees ; le test OCR valide le moteur sur du texte genere, pas la fiabilite du HUD reel.

Pendant une observation, visual-events.jsonl enregistre la chronologie des resultats et de leur disponibilite. Ce fichier est ajoute au ZIP. En mode OCR normal, les images et transcriptions completes restent en memoire puis sont eliminees : elles ne sont ni sauvegardees ni envoyees. Le moteur de capture selectionne dans Aimmy est reutilise. Windows 10 version 2004 ou Windows 11 et une langue OCR Windows sont requis.

Collecter 20 images pour validation est une action optionnelle explicite qui sauvegarde des images sur ce PC. Apres le clic, remettre R6 au premier plan et alterner avec/sans visee et avec quelques tirs. Vingt paires centre/HUD sont collectees au rythme du lecteur, puis un ZIP AutoAimmy-validation-... est cree dans exports. Compter environ 20 a 40 secondes selon les performances. Les images locales restent dans data/validation. Aucun envoi automatique. Les images sont marquees Unannotated et l'etat ADS Unknown : elles doivent etre annotees et servir a une validation separee avant de developper le classifieur. Le collecteur ne remplace pas encore la reconnaissance d'icones ou d'ADS, et aucun reentrainement n'est effectue.
