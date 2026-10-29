# Snaplume

<img src="docs/branding/snaplume.svg" alt="Logo Snaplume" width="96" align="right" />

Logiciel **libre et gratuit** de capture d'écran et d'annotation pour Windows, inspiré des fonctionnalités de Screenpresso, **sans aucun service en ligne** : tout reste sur votre ordinateur.

- Licence : MIT
- Plateforme : Windows 10 (version 2004 ou plus récente) / 11, x64, .NET 10, WPF
- Langues : français, anglais, allemand, espagnol, italien, arabe (interface de droite à gauche)
- Thème clair, sombre ou automatique (suit le mode choisi dans Windows, même en cours d'utilisation)

## Fonctionnalités

### Capture
- **Impr. écran** : le curseur devient un viseur, les fenêtres et éléments survolés (boutons, zones, panneaux…) sont détectés et entourés en rouge.
  - **Glisser** pour une zone libre, **cliquer** pour la fenêtre ou l'élément survolé
  - **Molette** : passer à l'élément parent (contrôle → fenêtre → écran)
  - **F** : écran entier, **A** : tous les écrans, **flèches** : ajuster au pixel près (Maj = 10 px), **Échap** : annuler
  - Loupe avec coordonnées et couleur du pixel (#RRGGBB)
- Écran entier, tous les écrans, fenêtre active, répétition de la dernière zone
- **Retardateur** (3, 5, 10 s) pour capturer les menus contextuels et info-bulles
- Curseur de la souris inclus en option
- Multi-écrans et hauts DPI (Per-Monitor v2) : les captures sont au pixel physique près

### Reconnaissance de texte (OCR)
- **Capturer du texte** : sélectionnez une zone de l'écran, son texte est reconnu puis copié dans le presse-papiers
- **Lire le texte** d'une capture existante (panneau, clic droit dans la galerie) ou de l'image en cours d'édition
- Utilise le moteur OCR intégré à Windows : **hors ligne**, rien ne quitte l'ordinateur. Les langues reconnues sont celles installées dans Windows (Paramètres › Heure et langue › Langue et région)

### Enregistrement vidéo (MP4)
- Zone, fenêtre ou écran entier, avec le même viseur que les captures ; compte à rebours de 3 s
- MP4 H.264 (encodage matériel si disponible), son de l'ordinateur et/ou microphone, curseur, clics mis en évidence en option, 15 à 60 images/s
- Barre flottante : durée, pause / reprise, arrêter, annuler ; cadre rouge autour de la zone — tous deux **exclus de la vidéo**
- **Ctrl+Alt+Impr. écran** démarre puis arrête l'enregistrement
- Les vidéos apparaissent dans « Mes captures » avec leur miniature ; double-clic pour les lire

### Lecteur vidéo intégré (façon mpv)
- Fenêtre noire épurée, commandes qui s'effacent quand la souris ne bouge plus, messages à l'écran
- Raccourcis mpv : Espace pause, ←/→ ±5 s (Maj ±1 s), ↑/↓ ±1 min, molette ±10 s, `,` `.` image par image, `[` `]` vitesse, Retour arrière vitesse normale, `9` `0` volume, M muet, L boucle, F ou double-clic plein écran, Q quitter
- **S** enregistre l'image affichée dans « Mes captures »
- Utilise le moteur multimédia de Windows (MP4 H.264/AAC) ; O ouvre la vidéo dans le lecteur par défaut

### Éditeur d'images (vectoriel, annotations modifiables)
- Flèche, ligne, rectangle, ellipse (contour ou plein ; Maj = 45° / carré / cercle)
- Texte (avec halo de lisibilité ou sur fond blanc)
- Surligneur, **pixelisation** des informations confidentielles, **projecteur** (assombrit le reste)
- **Numéros d'étapes** : chaque clic ajoute le numéro (ou la lettre) suivant ; formes rond, rond avec repère, bulle, épingle, rond sombre
- **Tampons** vectoriels : symboles (validé, refusé, question, attention, bug, flèches…), tampons de texte (APPROUVÉ, CONFIDENTIEL, BROUILLON…) et souris (curseurs, main, clics, « Cliquez ici »…), taille réglable
- Crayon à main levée, recadrage, **redimensionnement** (pixels ou pourcentage, proportions conservées, annotations ajustées)
- **Styles prédéfinis** : un clic sur un outil propose ses styles (couleur, épaisseur, double flèche, trait sans pointe…) ; ajoutez ou supprimez les vôtres
- Barre de propriétés contextuelle : seuls les réglages utiles à l'outil ou à la sélection sont affichés
- Couleurs, épaisseurs, taille du texte ; annuler / rétablir illimité
- Les annotations restent **modifiables** après enregistrement (dossier caché `.snaplume`) ; les zones pixelisées y sont déjà floutées, aucune information masquée n'y reste lisible
- Copier (Ctrl+C), enregistrer (Ctrl+S), exporter en PNG / JPEG / BMP
- Raccourcis : V sélection, A flèche, L ligne, R rectangle, E ellipse, T texte, H surligneur, B pixeliser, O projecteur, N numéros, S tampons, P crayon, C recadrer, Ctrl+R redimensionner, Ctrl+Maj+S exporter, Suppr, flèches, Ctrl+molette (zoom)

### Gestion des captures
- **Panneau « Mes captures »** ancré en bas à droite (clic sur l'icône de notification) : miniatures, boutons Capturer ▾ / Éditer / Copier
- **Espace de travail** en grande fenêtre
- **Espaces de travail (sous-dossiers)** comme Screenpresso : « Base » et autant de sous-dossiers que voulu (créer, renommer, supprimer, ouvrir) ; les captures et vidéos vont dans l'espace actif, choisi dans la barre latérale, le panneau ou l'icône de notification ; glisser des captures sur un espace ou « Déplacer vers » pour les ranger
- Enregistrement automatique (aucun nom de fichier à saisir), PNG ou JPEG
- Glisser-déposer des captures vers d'autres applications (mail, messagerie, documents) et import d'images par glisser-déposer
- Copier, renommer, ouvrir avec…, afficher dans l'Explorateur, mettre à la corbeille
- Icône dans la zone de notification, raccourcis globaux configurables, lancement au démarrage de Windows
- **Mode portable** : créez un fichier vide nommé `portable` à côté de `Snaplume.exe` pour garder les paramètres dans ce dossier

## Raccourcis par défaut

| Action | Raccourci |
|---|---|
| Zone, fenêtre ou élément | Impr. écran |
| Écran entier (sous la souris) | Maj+Impr. écran |
| Fenêtre active | Alt+Impr. écran |
| Répéter la dernière zone | Ctrl+Impr. écran |
| Zone avec retardateur | Ctrl+Maj+Impr. écran |
| Démarrer / arrêter une vidéo | Ctrl+Alt+Impr. écran |
| Capturer du texte (OCR) | aucun (à définir dans les paramètres) |

> Un raccourci ne peut appartenir qu'à une seule application. Si Screenpresso, OneDrive, Dropbox ou l'outil Capture de Windows utilise déjà la touche Impr. écran, Snaplume vous prévient : quittez l'autre application ou changez les raccourcis dans les paramètres. Sous Windows 11, désactivez aussi *Paramètres > Accessibilité > Clavier > Utiliser la touche Impr. écran pour ouvrir la capture d'écran*.

## Ligne de commande

```
Snaplume.exe [--tray] [--capture region|screen|all|window|repeat|text] [--delay N] [--workspace nom]
                 [--record region|screen] [--duration N] [--exit] [--settings] [--window] [image.png]
```

- `--tray` : démarre dans la zone de notification
- `--capture screen --exit` : capture puis quitte (utile en script)
- `--capture text` : sélectionne une zone et en lit le texte (OCR)
- `--workspace "Projet client"` : choisit (ou crée) l'espace de travail où enregistrer
- `--window` : ouvre la grande fenêtre de l'espace de travail
- Si Snaplume tourne déjà, la commande est transmise à l'instance ouverte (canal réservé à l'utilisateur courant)
- `--record screen --duration 30 --exit` : enregistre 30 s de vidéo puis quitte
- `image.png` : ouvre l'image dans l'éditeur

## Compiler

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download). L'enregistrement vidéo utilise [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (MIT, Media Foundation), qui demande le [Visual C++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) — déjà présent sur la plupart des PC. L'application est compilée pour x64.

```
dotnet build Snaplume.slnx
dotnet run --project src/Snaplume
```

Exécutable autonome (sans installation de .NET) :

```
dotnet publish src/Snaplume -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

L'icône est générée par `tools/make-icon.ps1`.

## Structure

```
src/Snaplume/
  Capturing/    capture d'écran, détection des fenêtres, sélection de zone (loupe, surbrillance)
  Recording/    enregistrement vidéo, barre de contrôle, cadre de la zone
  Player/       lecteur vidéo intégré
  Editor/       éditeur : modèle d'annotations, rendu, annuler/rétablir, persistance
  Workspace/    panneau « Mes captures », espace de travail, galerie partagée
  Preferences/  fenêtre des paramètres
  Ocr/          fenêtre du texte reconnu (moteur OCR de Windows)
  Languages/    traductions de l'interface (un fichier JSON par langue)
  Core/         paramètres, raccourcis globaux, icône de notification, fichiers, presse-papiers
  Native/       appels Win32
```

## Feuille de route

Fonctionnalités de Screenpresso pas encore disponibles (hors services en ligne, volontairement exclus) :

- [x] Enregistrement vidéo de l'écran en MP4 avec son
- [ ] Export GIF, webcam en incrustation, découpe / vitesse / export d'images de la vidéo
- [ ] Capture de fenêtres défilantes (assemblage / stitching)
- [x] Reconnaissance de texte (OCR)
- [ ] Lecture de QR codes
- [ ] Générateur de documents (PDF, DOCX, HTML) depuis une sélection de captures
- [x] Redimensionnement, tampons, lecteur vidéo intégré
- [ ] Effets : ombre, bordure, coins arrondis, bord déchiré ; loupe dans l'éditeur
- [ ] Impression, envoi par e-mail (client local)
- [x] Traductions : français, anglais, allemand, espagnol, italien, arabe
