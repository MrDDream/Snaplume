Snaplume {{VERSION}} - portable edition / édition portable
https://github.com/MrDDream/Snaplume

=== English ===

Snaplume is a free and open-source (MIT) screenshot and annotation tool for
Windows 10 (version 2004 or later) and Windows 11, 64-bit.

Usage
- Unzip the whole "Snaplume" folder anywhere you can write to (USB stick,
  Documents...), then run Snaplume.exe. No installation and no .NET install
  are needed.
- The empty file named "portable" tells Snaplume to keep its settings
  (settings.json) next to Snaplume.exe instead of %APPDATA%\Snaplume.
  Delete it to use the normal per-user settings folder.
- Captures are saved in Pictures\Snaplume by default (configurable).
- Command line: Snaplume.exe --capture screen --exit (see the README on GitHub).
[[applocal]]- The Microsoft Visual C++ runtime DLLs it needs are included in the folder.
[[redist]]- Requires the Microsoft Visual C++ 2015-2022 Redistributable (x64), already
[[redist]]  present on most PCs. If Snaplume does not start or video recording fails,
[[redist]]  install it from https://aka.ms/vs/17/release/vc_redist.x64.exe (the
[[redist]]  installer version of Snaplume does it for you).
- Windows SmartScreen may warn on first launch because the binaries are not
  code-signed: click "More info" then "Run anyway". Checksums (SHA256SUMS.txt)
  are published with each GitHub release.
- To remove Snaplume, quit it (tray icon > Quit), turn off "Start with
  Windows" in its settings if you enabled it, and delete the folder.

=== Français ===

Snaplume est un logiciel libre (MIT) de capture d'écran et d'annotation pour
Windows 10 (version 2004 ou ultérieure) et Windows 11, 64 bits.

Utilisation
- Décompressez tout le dossier « Snaplume » dans un emplacement accessible en
  écriture (clé USB, Documents...), puis lancez Snaplume.exe. Aucune
  installation, ni de .NET, n'est nécessaire.
- Le fichier vide nommé « portable » indique à Snaplume de conserver ses
  paramètres (settings.json) à côté de Snaplume.exe plutôt que dans
  %APPDATA%\Snaplume. Supprimez-le pour revenir au dossier habituel.
- Les captures sont enregistrées par défaut dans Images\Snaplume (modifiable).
- Ligne de commande : Snaplume.exe --capture screen --exit (voir le README sur
  GitHub).
[[applocal]]- Les DLL du runtime Microsoft Visual C++ nécessaires sont incluses dans le
[[applocal]]  dossier.
[[redist]]- Nécessite le Microsoft Visual C++ 2015-2022 Redistributable (x64), déjà
[[redist]]  présent sur la plupart des PC. Si Snaplume ne démarre pas ou si
[[redist]]  l'enregistrement vidéo échoue, installez-le depuis
[[redist]]  https://aka.ms/vs/17/release/vc_redist.x64.exe (la version avec
[[redist]]  programme d'installation le fait pour vous).
- Windows SmartScreen peut afficher un avertissement au premier lancement car
  les exécutables ne sont pas signés : cliquez sur « Informations
  complémentaires » puis « Exécuter quand même ». Les sommes de contrôle
  (SHA256SUMS.txt) sont publiées avec chaque version sur GitHub.
- Pour désinstaller : quittez Snaplume (icône de la zone de notification >
  Quitter), désactivez « Démarrer avec Windows » dans ses paramètres si vous
  l'aviez activé, puis supprimez le dossier.

License / Licence : MIT, see LICENSE / voir LICENSE.
