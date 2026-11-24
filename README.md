# Local IA Runner

Application Windows WinForms (.NET 10) pour exécuter des modèles GGUF avec llama.cpp et générer des images avec stable-diffusion.cpp. Les modèles multimodaux compatibles peuvent recevoir des images, de l'audio et des vidéos dans le chat. Le backend Python utilise uniquement la bibliothèque standard. L'installateur Windows x64 et la version portable incluent les runtimes nécessaires, le moteur llama.cpp CPU ainsi que les moteurs stable-diffusion.cpp Vulkan et CPU.

## Fonctionnalités

- Génération locale à partir de fichiers `.gguf`
- Envoi d'images, d'audio et de vidéos aux modèles multimodaux compatibles, avec aperçus et lecteurs dans la conversation
- Outil `analyze_image` pour qu'un modèle texte ou code, y compris un sous-agent, puisse faire analyser une image par un modèle Omni vision
- Affichage direct dans le chat des images créées par l'outil de génération
- Génération d'images avec les profils et modèles pris en charge par stable-diffusion.cpp
- Outils du modèle pour retrouver, copier, renommer et déplacer les images générées dans un projet de code
- Forum local où les modèles peuvent rechercher et partager des informations entre eux
- Aperçu, progression, annulation et sauvegarde des images générées
- Annulation d'une génération depuis le bouton **Annuler**
- Conversations sauvegardées dans `%APPDATA%\LocalIARunner\Conversations`
- Détection rapide des GGUF dans le dossier choisi, LM Studio, Jan, GPT4All et d'autres emplacements locaux courants
- Recherche complète facultative des modèles GGUF depuis la racine de tous les lecteurs fixes
- Mise à jour automatique depuis la dernière release GitHub, avec vérification SHA-256
- Interface de conversation modernisée avec mise en forme Markdown, liens cliquables et réflexion repliable
- Blocs de code avec commandes intégrées pour copier ou enregistrer les scripts

## Installation

Pour compiler les versions installable et portable depuis les sources, installer le SDK .NET 10 et Inno Setup 6 ou 7, puis lancer `compile.bat` (version `1.0.0` par défaut) ou `compile.bat 1.0.12` pour choisir la version. Le script génère :

- l'installateur `artifacts\installer\LocalIARunner-Setup-x64.exe`;
- le dossier portable versionné `artifacts\portable\LocalIARunner-Portable-x64-<version>`, à lancer avec `Local IA Runner.exe`;
- l'archive portable `artifacts\portable\LocalIARunner-Portable-x64-<version>.zip`, à extraire avant de lancer l'application.

L'affichage du chat utilise Microsoft Edge WebView2. L'installateur vérifie le runtime et installe le bootstrapper officiel de Microsoft si nécessaire. La version portable contient aussi ce bootstrapper : si le runtime manque, l'application propose de l'installer ou d'ouvrir la page officielle de téléchargement. Le runtime est déjà présent sur Windows 11 et peut aussi être installé séparément depuis la [page Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).

Les réponses acceptent un sous-ensemble courant de Markdown : titres, listes, citations, liens, emphase et blocs de code. Le HTML fourni par les modèles est affiché comme du texte et n'est pas exécuté. Les blocs de code peuvent être copiés ou téléchargés depuis la conversation.

Le bouton **Joindre** accepte les images PNG, JPEG, BMP et GIF, l'audio MP3, WAV et FLAC, ainsi que les vidéos MP4, WebM, MOV, MKV, AVI et MPEG. Les pièces jointes sont copiées dans `%APPDATA%\LocalIARunner\ChatMedia` pour rester disponibles au rechargement du chat. Il faut choisir un GGUF multimodal accompagné d'un fichier `.mmproj` compatible; les modèles sans projecteur ne reçoivent pas les fichiers. La vidéo est échantillonnée à une image par seconde. Au premier envoi vidéo, si FFmpeg et FFprobe ne sont pas dans le PATH, l'application télécharge la compilation Windows Essentials, vérifie son empreinte SHA-256 et l'installe dans `%APPDATA%\LocalIARunner\runtime\ffmpeg`. Ce téléchargement à la demande provient des [compilations Windows Gyan.dev](https://www.gyan.dev/ffmpeg/builds/); leur compilation Essentials est distribuée séparément sous licence GPLv3. Les images générées avec l'outil `generate_image` sont montrées directement dans le message de l'assistant.

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

Pour les modèles de texte et multimodaux, le scan rapide consulte le dossier choisi et les emplacements habituels de LM Studio (`%USERPROFILE%\.lmstudio\models`), Jan, GPT4All, text-generation-webui et Pinokio. **Fichier > Rechercher les modèles (rapide)** relance ce scan ciblé. **Fichier > Rechercher les modèles sur tout le PC** parcourt les lecteurs locaux fixes et amovibles depuis leur racine; la recherche continue en arrière-plan et peut être arrêtée depuis **Fichier > Annuler la recherche des modèles**. Dans les paramètres, **Parcourir tous les lecteurs au démarrage** active ce mode complet aussi au démarrage et lors de l'actualisation. Il est désactivé par défaut. Les lecteurs réseau, les dossiers auxquels Windows refuse l'accès et les liens de jonction sont ignorés. Seuls les fichiers GGUF compatibles avec le chargement local sont répertoriés; aucun fichier n'est déplacé ni copié.

Les modèles d'images (`.safetensors`, `.gguf`, `.sft`, `.ckpt`, `.pt`, `.pth` et `.bin`) sont recherchés dans les catégories reconnues par stable-diffusion.cpp, comme `checkpoints`, `diffusion_models`, `text_encoders` et `vae`, y compris dans leurs sous-dossiers. Les fichiers de modèle posés directement à la racine d'un dossier sélectionné sont aussi détectés sans être déplacés ni copiés. L'application affiche le chemin relatif et sélectionne automatiquement un profil de la même famille que le checkpoint. Pour les fichiers à la racine, son petit catalogue de liens durs est placé dans `.localia-image-model-catalog` à côté des modèles : les checkpoints restent à leur emplacement et ne sont pas copiés dans AppData. Les moteurs fournis sont placés à côté de l'application dans `stable-diffusion.cpp\windows-vulkan` et `stable-diffusion.cpp\windows-cpu`; les modèles et images restent dans les dossiers choisis par l'utilisateur. Pour les modèles GGUF multimodaux, place le fichier `.mmproj` à côté du GGUF principal : l'application le passe à `llama-server` automatiquement lorsqu'il est le seul projecteur du dossier, ou lorsqu'un seul correspond clairement au nom du modèle. Le fichier `.mmproj` reste auxiliaire et ne se sélectionne pas seul.

Dans **Outils / Paramètres / Modèles par défaut**, tu peux choisir le fichier GGUF utilisé au démarrage, le modèle d'image, le **modèle Omni vision** et, facultativement, un modèle GGUF dédié au nommage des conversations. Le nommage dédié est désactivé par défaut et utilise alors le modèle du chat; si tu l'actives, le modèle choisi génère le titre avant la réponse, puis l'application recharge le modèle du chat. Le modèle Omni doit être un GGUF vision avec un fichier `.mmproj` compatible dans le même dossier. Le réglage **Mémoire pendant l'analyse Omni** détermine si l'application décharge le modèle du chat le temps de l'analyse (mode économe, par défaut) ou garde les deux modèles chargés sur des serveurs séparés (RAM/VRAM supplémentaires nécessaires).

Dans **Outils / Paramètres / Apparence**, active **Mode sombre** pour appliquer la palette sombre au chat et aux fenêtres de l'application. Le choix est enregistré avec les autres paramètres et désactivé par défaut.

Dans **Outils / Paramètres / Performances**, le mappage SSD des modèles texte reste désactivé par défaut. Le mode **CPU sécurisé pour les images** est aussi désactivé par défaut; quand il est activé, il évite le GPU intégré et laisse l'application réduire la résolution si le moteur manque de mémoire. Le processus du moteur d'image est plafonné à **4 Gio** par défaut (réglable) et la génération s'arrête si le disque système ou le dossier de sortie passe sous **5 Gio libres** (réglable). Ces limites protègent le disque contre une croissance incontrôlée du fichier d'échange. L'application libère le modèle texte pendant la génération d'image, puis le recharge pour poursuivre la conversation.

## Appels d'outils par les modèles


Les outils web, code, génération d'images et analyse visuelle sont décrits dans les consignes système ajoutées au modèle pour chaque requête. `list_local_models` est toujours disponible : il retourne les modèles de texte GGUF détectés, leur nom et leur chemin exact. Il peut servir à choisir un modèle pour un sous-agent. Si un modèle d'image par défaut est défini dans **Outils / Paramètres / Modèles par défaut**, le modèle de texte peut appeler `generate_image` : il rédige le prompt positif et négatif et choisit les dimensions, étapes, guidage et graine. Le backend sélectionne le profil compatible avec la famille du modèle, enregistre l'image dans `Pictures\LocalIARunner` et l'attache à la réponse. Si le modèle de texte accepte les images et possède un projecteur `.mmproj`, l'image lui est aussi jointe dans le résultat de l'outil pour qu'il puisse l'examiner. Sans capacité vision, il reçoit le chemin du fichier, mais ne peut pas voir son contenu. Si un modèle de conversation n'a pas de `.mmproj`, `analyze_image` transmet les images jointes au modèle Omni, puis donne sa description au modèle qui a posé la question. Les pixels ne sont pas envoyés au modèle de chat. Un modèle orienté code peut utiliser cet outil lui aussi; un sous-agent peut le recevoir dans la liste d'outils autorisés par son contrôleur. Sans modèle Omni valide et sans image jointe, l'outil n'est pas proposé.

Dans **Outils / Paramètres / Outils et accès**, **Commandes système directes** est désactivé par défaut. Une fois autorisé, `run_system_command` lance un programme avec les droits Windows de l'utilisateur; son dossier de départ est limité au dossier de travail et un délai maximal s'applique. Cette permission est séparée de l'exécution Python. Un script Python autorisé peut lui aussi lancer des commandes système.

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

La réponse complète doit contenir uniquement cet appel, sans phrase ni Markdown autour. Un seul outil est appelé à la fois; après son résultat, le modèle peut continuer. Pour `write_file`, encode les retours de ligne du contenu en `\n`. Les outils `list_files`, `read_file` et `write_file` restent limités au dossier de travail. L'exécution Python et les commandes système s'exécutent avec les permissions Windows de l'utilisateur et peuvent accéder à d'autres ressources disponibles pour cette session. Chaque permission est désactivée par défaut et se règle séparément dans les paramètres.

Exemple de commande système directe après activation :

```text
<run_system_command>{"executable":"git","args":["--version"],"working_directory":".","timeout_seconds":30}</run_system_command>
```

`web_search` renvoie au maximum trois résultats avec des extraits courts pour qu'ils tiennent dans le contexte du modèle. Le backend retire d'abord les anciens échanges si nécessaire et conserve l'appel courant. Pour consulter davantage de détails, le modèle peut ouvrir l'URL d'un résultat avec `browse_url`.

Quand ces outils sont autorisés, le modèle peut utiliser `list_images` pour parcourir le dossier de sortie configuré dans la fenêtre de génération d'images (par défaut `Pictures\LocalIARunner`), `copy_image` pour copier une image générée dans le dossier du projet et `move_image` pour la renommer/déplacer dans le dossier des images ou vers le projet. Les chemins sont relatifs à ces deux dossiers, les fichiers existants ne sont pas remplacés et les images ne peuvent pas sortir de ces emplacements. Après copie dans le projet, le modèle peut référencer l'image dans son code, par exemple `assets/image.png`.

Dans **Outils / Paramètres / Délais des outils**, tu peux régler le délai d'inactivité d'une génération (30 secondes à 2 heures, 900 secondes par défaut) ainsi que le temps maximal accordé à un script Python ou à une commande système (1 seconde à 2 heures, 30 secondes par défaut). L'activité du modèle et la progression des outils réinitialisent le délai d'inactivité.

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

