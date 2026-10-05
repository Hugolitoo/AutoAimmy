# Reglages automatiques — 0.1.6

Aucun questionnaire au lancement. AutoAimmy cherche GameSettings.ini dans Documents Windows (y compris Documents redirige dans OneDrive) puis dans Documents du profil Windows, sous My Games/Rainbow Six - Siege. Il lit uniquement ce fichier et ne le modifie jamais.

Quand un seul fichier est trouve, les champs reconnus et valides sont importes : sensibilites horizontale et verticale, multiplicateurs souris/ADS, sensibilites ADS globales et par grossissement, activation des sensibilites ADS specifiques, FOV et resolution. Le code enum du ratio est conserve sous AspectRatioSetting ; aucune correspondance non verifiee vers un ratio textuel n'est inventee.

La lecture se fait a chaque lancement et avec Profil-joueur.cmd. Ce raccourci ne demande plus de saisir les reglages : il relit la configuration et affiche le resultat. Le profil automatique est stocke dans data/active-profile.json. L'ancien profil actif est sauvegarde et les anciens profils dans data/profiles sont conserves, mais leurs armes, lunettes et DPI declares ne sont pas repris automatiquement.

## Informations encore inconnues

Le DPI materiel, l'arme equipee, la lunette equipee et l'etat ADS reel restent inconnus. Tous les reglages ADS disponibles sont conserves, sans choisir un grossissement suppose. La configuration sur disque peut etre ancienne ou differer de changements non enregistres dans le jeu. Sa date de modification est jointe au contexte ; aucune verification des valeurs actives en memoire n'est effectuee. Apres un changement, enregistrer les reglages dans le jeu puis relancer AutoAimmy. Pas d'injection ni de lecture de la memoire du jeu.

Si plusieurs fichiers de comptes sont trouves, ImportStatus=AmbiguousAccounts et les valeurs restent inconnues. L'application ne devine pas le compte actif a partir de la date des fichiers. Si aucun fichier n'est trouve, ImportStatus=NotFound. Une valeur invalide, un format non reconnu ou un fichier inaccessible n'est pas transforme en valeur supposee. Aucun questionnaire ne revient pour combler ces informations.

## Rapports et mises a jour

Au chargement du modele, le contexte courant est copie dans context.json. SettingsSource=SettingsFile distingue ces valeurs des anciens rapports UserDeclared. Seuls les champs autorises sont exportes, sans chemin de compte, identifiant de compte, contenu complet du fichier ou donnees ONLINE. Les sessions deja enregistrees ne changent pas.

Le ZIP du rapport est cree automatiquement dans exports en fin d'observation ou a la fermeture normale. Exporter-rapport.cmd permet de reessayer si l'export echoue. Aucun envoi automatique. Les mises a jour restent automatiques au demarrage ; AutoAimmy-hors-ligne.cmd permet de conserver la version.

La prochaine etape est la reconnaissance visuelle de l'arme, de la lunette et des transitions ADS. Elle reste a developper et valider sur une courte video montrant le HUD. La 0.1.6 n'effectue pas de reentrainement du modele ni de reglage automatique de l'assistance.
