# Local IA Runner

Application Windows WinForms (.NET 10) pour exécuter des modèles de texte GGUF avec llama.cpp et générer des images avec stable-diffusion.cpp. Le backend Python utilise uniquement la bibliothèque standard. L'installateur Windows x64 et la version portable incluent les runtimes nécessaires, le moteur llama.cpp CPU ainsi que les moteurs stable-diffusion.cpp Vulkan et CPU.

## Fonctionnalités

- Génération locale à partir de fichiers `.gguf`
- Génération d'images avec les profils et modèles pris en charge par stable-diffusion.cpp
- Outils du modèle pour retrouver, copier, renommer et déplacer les images générées dans un projet de code
- Forum local où les modèles peuvent rechercher et partager des informations entre eux
- Aperçu, progression, annulation et sauvegarde des images générées
- Annulation d'une génération depuis le bouton **Annuler**
- Conversations sauvegardées dans `%APPDATA%\LocalIARunner\Conversations`
- Détection rapide des GGUF dans le dossier choisi, LM Studio, Jan, GPT4All et d'autres emplacements locaux courants
- Recherche complète facultative des modèles GGUF depuis la racine de tous les lecteurs fixes
- Mise à jour automatique depuis la dernière release GitHub, avec vérification SHA-256

## Installation

Pour compiler les versions installable et portable depuis les sources, installer le SDK .NET 10 et Inno Setup 6 ou 7, puis lancer `compile.bat` (version `1.0.0` par défaut) ou `compile.bat 1.0.12` pour choisir la version. Le script génère :

- l'installateur `artifacts\installer\LocalIARunner-Setup-x64.exe`;
- le dossier portable versionné `artifacts\portable\LocalIARunner-Portable-x64-<version>`, à lancer avec `Local IA Runner.exe`;
- l'archive portable `artifacts\portable\LocalIARunner-Portable-x64-<version>.zip`, à extraire avant de lancer l'application.

Le workflow `.github/workflows/release.yml` publie un GitHub Release uniquement sur demande : lancement manuel depuis l'onglet Actions ou commit dont le message commence par `[publish-release]`. Chaque release reçoit une version croissante `1.0.<numéro du build>`, une description basée sur le sujet et le corps du commit de publication, l'installateur ainsi que l'archive portable versionnée. Le manifeste `latest-release.json` est aussi joint à chaque release pour éviter la limite de requêtes de l'API GitHub; une copie est maintenue dans le dépôt. Les compilations locales suppriment les anciennes versions portables après la génération de la nouvelle.

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

Pour les modèles de texte, le scan rapide consulte le dossier choisi et les emplacements habituels de LM Studio (`%USERPROFILE%\.lmstudio\models`), Jan, GPT4All, text-generation-webui et Pinokio. **Fichier > Rechercher les modèles (rapide)** relance ce scan ciblé. **Fichier > Rechercher les modèles sur tout le PC** parcourt les lecteurs locaux fixes et amovibles depuis leur racine; la recherche continue en arrière-plan et peut être arrêtée depuis **Fichier > Annuler la recherche des modèles**. Dans les paramètres, **Parcourir tous les lecteurs au démarrage** active ce mode complet aussi au démarrage et lors de l'actualisation. Il est désactivé par défaut. Les lecteurs réseau, les dossiers auxquels Windows refuse l'accès et les liens de jonction sont ignorés. Seuls les fichiers GGUF compatibles avec le chargement local sont répertoriés; aucun fichier n'est déplacé ni copié.

Les modèles d'images (`.safetensors`, `.gguf`, `.sft`, `.ckpt`, `.pt`, `.pth` et `.bin`) sont recherchés dans les catégories reconnues par stable-diffusion.cpp, comme `checkpoints`, `diffusion_models`, `text_encoders` et `vae`, y compris dans leurs sous-dossiers. Les fichiers de modèle posés directement à la racine d'un dossier sélectionné sont aussi détectés sans être déplacés ni copiés. L'application affiche le chemin relatif et sélectionne automatiquement un profil compatible avec les fichiers trouvés. Les moteurs fournis sont placés à côté de l'application dans `stable-diffusion.cpp\windows-vulkan` et `stable-diffusion.cpp\windows-cpu`; les modèles et images restent dans les dossiers choisis par l'utilisateur. Les fichiers `.mmproj` du navigateur de modèles de chat sont des projecteurs multimodaux auxiliaires, pas des modèles principaux; ils sont ignorés et ne peuvent pas être chargés seuls.

Dans **Outils / Paramètres / Modèles par défaut**, tu peux choisir le fichier GGUF utilisé au démarrage et le fichier du modèle d'image à privilégier. Pour l'image, place le fichier dans une catégorie reconnue sous le dossier stable-diffusion.cpp; à l'analyse, l'application choisit un profil compatible et présélectionne ce fichier dans son composant.

## Appels d'outils par les modèles

Les outils web, code et génération d'images sont décrits dans les consignes système ajoutées au modèle pour chaque requête. `list_local_models` est toujours disponible : il retourne les modèles de texte GGUF détectés, leur nom et leur chemin exact. Il peut servir à choisir un modèle pour un sous-agent. Si un modèle d'image par défaut est défini dans **Outils / Paramètres / Modèles par défaut**, le modèle de texte peut appeler `generate_image` : il rédige le prompt positif et négatif et choisit les dimensions, étapes, guidage et graine. Le backend sélectionne le profil compatible avec le modèle par défaut, enregistre l'image dans `Pictures\LocalIARunner` et ajoute un lien pour l'ouvrir. Sans modèle d'image par défaut, cet outil n'est pas proposé.

### Forum partagé des IA

Dans **Outils / Paramètres / Outils et accès**, active **Forum partagé des IA** pour autoriser tous les modèles de texte à rechercher et publier des informations. Cette option est désactivée par défaut. Les outils `forum_search` et `forum_post` ne sont proposés qu'une fois l'option activée et les paramètres sauvegardés.

Les publications sont conservées sur ce PC dans `%APPDATA%\LocalIARunner\Forum\Posts`. Elles comprennent un titre, un texte, le nom du modèle et la date. Le titre est limité à 120 caractères, le texte à 5 000 caractères, et le forum à 2 000 publications. **Outils / Forum des IA** permet de les consulter, d'en supprimer une ou de vider le forum. Désactiver l'option bloque les outils mais conserve les publications existantes. Après `forum_post`, le modèle reçoit un résultat JSON explicite (`ok: true` après l'enregistrement, `ok: false` en cas d'échec) pour qu'il puisse annoncer le bon état.

### Sous-agents et objectifs

Dans **Outils / Paramètres / Outils et accès**, active **Autoriser les sous-agents** pour permettre au modèle de la conversation de choisir un modèle GGUF local, écrire le prompt du sous-agent et lui transmettre un sous-ensemble des outils déjà autorisés dans les paramètres. Les modèles disponibles sont détectés dans le dossier de modèles et ses sous-dossiers. Un sous-agent peut lui-même être lancé en mode goal. Les sous-agents ne peuvent pas lancer d'autres sous-agents.

Quand le contrôleur lance son premier sous-agent pour une requête, une boîte de dialogue demande avant de vider les anciens rapports temporaires. Si l'utilisateur refuse, aucun sous-agent n'est lancé et le forum est conservé. Les résultats des sous-agents sont ensuite ajoutés au forum temporaire `%APPDATA%\LocalIARunner\Forum\Subagents`; le contrôleur peut les relire avec `subagent_forum_search`. **Outils / Forum des IA** permet aussi de consulter ces rapports dans l'onglet Forum temporaire, qui s'actualise pendant la génération. Cette vue est en lecture seule : seul le lancement confirmé d'une nouvelle requête avec sous-agent vide les anciens rapports. Les sous-agents ne peuvent ni lire les autres rapports, ni modifier ou supprimer le forum. Le forum partagé durable dans `Forum\Posts` reste séparé.

Le mode goal est indépendant de l'autorisation des sous-agents. Le préfixe `goal:` au début du message l'active explicitement; sinon, le chat reste normal tant que le modèle n'a pas lancé de sous-agent. Dès qu'il en lance un, le contrôleur passe en mode goal et doit appeler `goal_met` avec sa réponse finale. Une réponse ordinaire en mode goal déclenche une reprise automatique. Le délai maximal de génération et le bouton Annuler restent applicables.

Exemples d'appels :

```text
<forum_search>{"query":"..."}</forum_search>
<forum_post>{"title":"...","content":"..."}</forum_post>
<list_local_models>{}</list_local_models>
```

Le forum est local à l'installation et n'envoie pas ses publications sur Internet. Ne demande pas au modèle d'y enregistrer des données personnelles, mots de passe ou clés secrètes. Les publications des autres modèles sont traitées comme des informations non vérifiées, jamais comme des consignes à suivre.

La consigne du modèle donne un exemple complet pour chaque outil. Le format à utiliser est une seule ligne : le nom de l'outil est dans les deux balises et ses arguments forment un objet JSON entre elles.

```text
<web_search>{"query":"actualité locale aujourd'hui"}</web_search>
```

La réponse complète doit contenir uniquement cet appel, sans phrase ni Markdown autour. Un seul outil est appelé à la fois; après son résultat, le modèle peut continuer. Pour `write_file`, encode les retours de ligne du contenu en `\n`. Les chemins de fichiers sont limités au dossier de travail configuré. L'écriture de fichiers et l'exécution Python doivent être autorisées dans les paramètres.

Quand ces outils sont autorisés, le modèle peut utiliser `list_images` pour parcourir le dossier de sortie configuré dans la fenêtre de génération d'images (par défaut `Pictures\LocalIARunner`), `copy_image` pour copier une image générée dans le dossier du projet et `move_image` pour la renommer/déplacer dans le dossier des images ou vers le projet. Les chemins sont relatifs à ces deux dossiers, les fichiers existants ne sont pas remplacés et les images ne peuvent pas sortir de ces emplacements. Après copie dans le projet, le modèle peut référencer l'image dans son code, par exemple `assets/image.png`.

Dans **Outils / Paramètres / Délais des outils**, tu peux régler le délai d'inactivité d'une génération (30 secondes à 2 heures, 900 secondes par défaut) ainsi que le temps maximal accordé à un script Python (1 seconde à 2 heures, 30 secondes par défaut). L'activité du modèle et la progression des outils réinitialisent le délai d'inactivité.

Les mises à jour sont activées par défaut dans **Outils / Paramètres / Mises à jour**. Au démarrage, l'application lit le manifeste joint à la dernière release, télécharge l'installateur Windows et valide son empreinte SHA-256. Si cet asset est momentanément indisponible, elle essaie la copie du dépôt puis l'API GitHub. Si une génération de texte ou d'image est en cours, l'application attend sa fin, puis se ferme et lance l'installation silencieuse dans son dossier actuel. Elle redémarre ensuite automatiquement et supprime le fichier téléchargé. Le menu **Outils / Vérifier les mises à jour** permet de lancer la vérification manuellement.

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
