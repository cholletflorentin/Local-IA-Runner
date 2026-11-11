# Local IA Runner

Application Windows WinForms (.NET 10) pour exécuter des modèles de texte GGUF avec llama.cpp et générer des images avec stable-diffusion.cpp. Le backend Python utilise uniquement la bibliothèque standard. L'installateur Windows x64 et la version portable incluent les runtimes nécessaires, le moteur llama.cpp CPU ainsi que les moteurs stable-diffusion.cpp Vulkan et CPU.

## Fonctionnalités

- Génération locale à partir de fichiers `.gguf`
- Génération d'images avec les profils et modèles pris en charge par stable-diffusion.cpp
- Outils du modèle pour retrouver, copier, renommer et déplacer les images générées dans un projet de code
- Aperçu, progression, annulation et sauvegarde des images générées
- Annulation d'une génération depuis le bouton **Annuler**
- Conversations sauvegardées dans `%APPDATA%\LocalIARunner\Conversations`
- Détection des modèles dans un dossier choisi

## Installation

Pour compiler les versions installable et portable depuis les sources, installer le SDK .NET 10 et Inno Setup 6 ou 7, puis lancer `compile.bat` (version `1.0.0` par défaut) ou `compile.bat 1.0.12` pour choisir la version. Le script génère :

- l'installateur `artifacts\installer\LocalIARunner-Setup-x64.exe`;
- le dossier portable versionné `artifacts\portable\LocalIARunner-Portable-x64-<version>`, à lancer avec `Local IA Runner.exe`;
- l'archive portable `artifacts\portable\LocalIARunner-Portable-x64-<version>.zip`, à extraire avant de lancer l'application.

Le workflow `.github/workflows/release.yml` publie un GitHub Release uniquement sur demande : lancement manuel depuis l'onglet Actions ou commit dont le message commence par `[publish-release]`. Chaque release reçoit une version croissante `1.0.<numéro du build>` et contient l'installateur ainsi que l'archive portable versionnée. Les compilations locales suppriment les anciennes versions portables après la génération de la nouvelle.

Pour compiler uniquement l'installateur, exécuter :

```powershell
powershell -ExecutionPolicy Bypass -File .\WinFormsApp\Installer\build-installer.ps1
```

Le fichier `artifacts\installer\LocalIARunner-Setup-x64.exe` installe l'application en 64 bits sur Windows 10 (version 1809 ou ultérieure) et Windows 11, sans exiger Python, .NET ou les droits administrateur sur le PC cible. La version portable contient aussi ses dépendances et n'a pas besoin d'être installée. `install.bat` génère l'installateur puis le lance.

L'installateur contient les moteurs d'inférence, mais pas les modèles : ils occupent plusieurs Go et doivent être téléchargés séparément ou déjà présents sur le PC. Une connexion Internet reste nécessaire pour télécharger le moteur CUDA de llama.cpp sur un PC NVIDIA. Si le moteur Vulkan d'images ne peut pas démarrer, l'application essaie le moteur CPU inclus. Les générations suivantes réutilisent le serveur et le modèle d'images jusqu'au changement de moteur.

Les licences des composants redistribués sont installées dans le dossier `Licenses`, avec un récapitulatif dans `THIRD-PARTY-NOTICES.txt`.

Pour développer et lancer directement depuis les sources, compiler `WinFormsApp\LocalAIApp.csproj` puis exécuter `launch.bat` depuis la racine du projet. L'application utilise Python installé sur le poste hors du paquet d'installation.

## Utilisation

1. Pour discuter, choisir le dossier contenant les modèles GGUF, sélectionner un fichier `.gguf` puis envoyer un message.
2. Pour générer une image, ouvrir **Outils > Générer une image**, choisir le dossier de modèles stable-diffusion.cpp puis cliquer sur **Analyser les modèles**.
3. Choisir un profil et ses composants, écrire un prompt et régler les dimensions, le nombre d'étapes et le guidage. L'application affiche la progression, permet d'annuler et enregistre l'image dans le dossier de sortie choisi.

Les modèles d'images (`.safetensors`, `.gguf`, `.sft`, `.ckpt`, `.pt`, `.pth` et `.bin`) sont recherchés dans les catégories reconnues par stable-diffusion.cpp, comme `checkpoints`, `diffusion_models`, `text_encoders` et `vae`, y compris dans leurs sous-dossiers. Les fichiers de modèle posés directement à la racine d'un dossier sélectionné sont aussi détectés sans être déplacés ni copiés. L'application affiche le chemin relatif et sélectionne automatiquement un profil compatible avec les fichiers trouvés. Les moteurs fournis sont placés à côté de l'application dans `stable-diffusion.cpp\windows-vulkan` et `stable-diffusion.cpp\windows-cpu`; les modèles et images restent dans les dossiers choisis par l'utilisateur. Les fichiers `.mmproj` du navigateur de modèles de chat sont des projecteurs multimodaux auxiliaires, pas des modèles principaux; ils sont ignorés et ne peuvent pas être chargés seuls.

Dans **Outils / Paramètres / Modèles par défaut**, tu peux choisir le fichier GGUF utilisé au démarrage et le fichier du modèle d'image à privilégier. Pour l'image, place le fichier dans une catégorie reconnue sous le dossier stable-diffusion.cpp; à l'analyse, l'application choisit un profil compatible et présélectionne ce fichier dans son composant.

## Appels d'outils par les modèles

Les outils web, code et génération d'images sont décrits dans les consignes système ajoutées au modèle pour chaque requête. Si un modèle d'image par défaut est défini dans **Outils / Paramètres / Modèles par défaut**, le modèle de texte peut appeler `generate_image` : il rédige le prompt positif et négatif et choisit les dimensions, étapes, guidage et graine. Le backend sélectionne le profil compatible avec le modèle par défaut, enregistre l'image dans `Pictures\LocalIARunner` et ajoute un lien pour l'ouvrir. Sans modèle d'image par défaut, cet outil n'est pas proposé.

Un appel d'outil est exécuté uniquement si la sortie complète du modèle contient un seul bloc d'outil, sans texte avant ni après. Le modèle peut utiliser une balise ou la syntaxe d'appel de fonction `web_search({"query":"..."})` :

```text
<web_search {"query":"actualité locale aujourd'hui"}></web_search>
```

Un appel accompagné d'une phrase est affiché comme texte et n'est pas exécuté. Une balise finale `</response>` parfois ajoutée par le format de chat marque la fin de l'appel. Le modèle ne peut émettre l'appel suivant qu'après avoir reçu le résultat du précédent. Pour `write_file`, encode les retours de ligne du contenu en `\n`; les retours bruts sont aussi acceptés. Les fichiers sont écrits dans le dossier de travail configuré. L'écriture de fichiers et l'exécution Python doivent être autorisées dans les paramètres.

Quand ces outils sont autorisés, le modèle peut utiliser `list_images` pour parcourir le dossier de sortie configuré dans la fenêtre de génération d'images (par défaut `Pictures\LocalIARunner`), `copy_image` pour copier une image générée dans le dossier du projet et `move_image` pour la renommer/déplacer dans le dossier des images ou vers le projet. Les chemins sont relatifs à ces deux dossiers, les fichiers existants ne sont pas remplacés et les images ne peuvent pas sortir de ces emplacements. Après copie dans le projet, le modèle peut référencer l'image dans son code, par exemple `assets/image.png`.

Dans **Outils / Paramètres / Délais des outils**, tu peux régler le délai d'inactivité d'une génération (30 secondes à 2 heures, 900 secondes par défaut) ainsi que le temps maximal accordé à un script Python (1 seconde à 2 heures, 30 secondes par défaut). L'activité du modèle et la progression des outils réinitialisent le délai d'inactivité.

## Structure

```text
LocalAIApp/
  .github/workflows/release.yml
  install.bat
  compile.bat
  launch.bat
  WinFormsApp/LocalAIApp.csproj
  WinFormsApp/Program.cs
  WinFormsApp/MainForm.cs
  WinFormsApp/ImageGeneratorForm.cs
  WinFormsApp/Installer/build-installer.ps1
  WinFormsApp/Installer/setup.iss
  WinFormsApp/Installer/THIRD-PARTY-NOTICES.txt
  WinFormsApp/Installer/licenses/
  PythonBackend/ai_backend.py
```
