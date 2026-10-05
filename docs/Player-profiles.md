# Profils enregistres — 0.1.4

Au premier lancement, choisir N pour creer un profil. DPI, sensibilites, lunette, sensibilite ADS, FOV, ratio et resolution sont demandes une fois. Entree laisse une valeur inconnue. S permet de continuer sans renseigner les reglages et memorise ce choix.

Aux lancements suivants, le profil actif est reutilise sans nouveau questionnaire. Profil-joueur.cmd permet de choisir un profil deja enregistre ou d'en creer un nouveau si les reglages changent. L'application doit etre fermee pour changer le profil.

Les profils restent dans data/profiles et le choix actif dans data/active-profile.json. Ces fichiers survivent aux mises a jour. Ne pas enregistrer un mot de passe, un jeton ou un identifiant personnel dans les champs libres.

Au chargement du modele, l'enregistreur prend une copie immuable du profil. context.json accompagne chaque nouvelle session et son export. Une session deja enregistree n'est pas modifiee par un changement ulterieur de profil. Les rapports exportes contiennent volontairement les reglages de cette session, mais pas tous les profils ni la configuration generale. Aucune transmission automatique.

Ces donnees sont declarees par le joueur. Un DPI, une arme ou une lunette ne sont pas lus automatiquement dans le jeu. AdsUsageDeclared est un contexte prevu, pas une detection d'ADS dans les images. L'etat ADS reel reste inconnu. Si les reglages changent dans le jeu sans changement de profil, le contexte joint au rapport peut etre obsolete.

## Quoi faire et pourquoi

1. Creer le profil une fois : eviter de retaper ou de redonner les reglages dans chaque message.
2. Garder les reglages identiques pendant une session : comparer des observations recueillies dans le meme contexte.
3. Changer de profil avant une session avec une autre arme, lunette ou sensibilite : ne pas melanger les contextes.
4. Exporter le rapport apres fermeture : il contient maintenant le contexte et les mesures, et non des informations a reconstruire apres coup.
5. Pour la prochaine etape visuelle, fournir une courte video avec HUD et transitions ADS/sans ADS : les anciens rapports ne contiennent pas ces images et ne suffisent pas a valider un detecteur visuel.

Pour une ancienne installation, mettre a jour puis fermer et relancer AutoAimmy.cmd. Le nouveau client cree automatiquement Profil-joueur.cmd, meme si le bootstrap de l'ancienne installation ne connait pas cette action.

## Automatisation — 0.1.5

Le client recree Profil-joueur.cmd a chaque action, y compris Mettre-a-jour.cmd. Une installation qui vient de la 0.1.4 doit relancer AutoAimmy.cmd une fois pour executer le nouveau client. Les mises a jour suivantes se font sans question supplementaire ; le lancement hors ligne permet de rester sur une version.

Le choix C reprend le profil actif dans un nouveau profil et conserve DPI, sensibilites generales, FOV, ratio, resolution et usage declare. Seuls nom, arme, lunette et sensibilite ADS sont demandes. Le profil original reste intact. Verifier les valeurs reprises avant de jouer si les reglages generaux ont change.

Le rapport ZIP est cree automatiquement dans exports quand l'observation se termine ou quand l'application est fermee normalement. Il contient les memes cinq fichiers autorises que l'export manuel, sans evenements bruts ni tous les profils. Les fichiers de session sont preserves si la creation du ZIP echoue ; Exporter-rapport.cmd permet de reessayer. Aucun envoi automatique, aucune reconnaissance visuelle d'arme ou d'ADS ajoutee dans cette version.
