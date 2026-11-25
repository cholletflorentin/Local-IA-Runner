# Local IA Runner — application Uno Platform (bêta)

Cette application Uno est ajoutée à côté de l'application WinForms. Le code WinForms et son backend existant restent en place et continuent d'être la version principale.

## Ce que contient cette bêta

- Une interface commune pour Windows, Linux, macOS et Android, écrite en C# et XAML avec Uno Platform.
- Une liste de discussions enregistrée localement, avec réponses en streaming et bouton d'annulation.
- Une connexion à un serveur compatible avec l'API OpenAI de llama.cpp (`/v1/models` et `/v1/chat/completions`).
- Une adresse de serveur et une clé API configurables depuis l'application.
- Les archives desktop incluent le moteur CPU llama.cpp b12676 et un petit lanceur pour démarrer le serveur.
- L'APK Android inclut llama.cpp b12676 ARM64 et démarre son serveur local au premier message après le choix d'un modèle GGUF.
- Les notices de licence sont incluses dans chaque archive desktop et dans l'APK.

Sur Windows, Linux et macOS, saisis le chemin complet d'un modèle `.gguf` dans l'application puis clique sur **Choisir ce GGUF**. Le bouton **Actualiser** démarre le `llama-server` CPU inclus dans l'archive et attend que le modèle soit prêt. Le chemin du modèle reste enregistré dans l'application; le modèle lui-même n'est pas copié. Les lanceurs `run-llama-server.cmd` et `run-llama-server.sh` restent disponibles pour un démarrage manuel. Exemple :

```text
Windows : run-llama-server.cmd -m "C:\ia\text\modele.gguf" --host 127.0.0.1 --port 8080
Linux/macOS : sh ./run-llama-server.sh -m "/chemin/vers/modele.gguf" --host 127.0.0.1 --port 8080
```

Utilise ensuite `http://127.0.0.1:8080` dans l'application desktop (c'est l'adresse par défaut). Sur Android, choisis un fichier GGUF avec le bouton **Choisir un modèle GGUF**. L'application le copie dans son espace privé; llama.cpp est extrait de l'APK puis démarre lorsque tu cliques sur **Actualiser** ou envoies le premier message à localhost. Le premier démarrage peut prendre plusieurs minutes pendant que le modèle se charge. Le fichier GGUF n'est pas inclus dans l'APK et demande suffisamment d'espace disque et de mémoire. Cette version du moteur fonctionne sur les appareils Android ARM64. Tu peux toujours saisir l'adresse d'un autre serveur llama.cpp pour utiliser un serveur distant. L'application Uno ne reprend pas encore stable-diffusion.cpp, les outils, le forum, les sous-agents ni les autres fonctions propres au backend WinForms.

Les conversations sont enregistrées localement dans le dossier de données de l'application. La clé API reste seulement en mémoire pendant l'exécution et n'est pas enregistrée.

## Démarrage

Sur Windows, Linux et macOS, saisis le chemin complet d'un modèle GGUF, clique sur **Choisir ce GGUF**, puis sur **Actualiser**. L'application lance le moteur inclus et charge la liste des modèles depuis `/v1/models`. Sur Android, choisis d'abord le GGUF sur le téléphone; **Actualiser** ou le premier message démarre llama.cpp. Pour utiliser un serveur distant, renseigne son URL et clique sur **Enregistrer et connecter** ou **Actualiser**.

Sur Android, `127.0.0.1` désigne le téléphone lui-même. Pour joindre un serveur sur un autre ordinateur, indique son adresse IP locale et configure llama.cpp pour accepter les connexions du réseau local. L'application n'accepte HTTP que pour localhost et les adresses privées; les adresses publiques doivent utiliser HTTPS. N'expose pas un serveur sans authentification sur un réseau public.

## Installer les versions bêta

Télécharge les paquets depuis la dernière préversion GitHub intitulée **Local IA Runner — bêta Uno Platform**. Cette bêta est distincte de la version WinForms stable.

- **Windows** : extrais `LocalIARunner-Uno-Windows-x64.zip`, puis lance `LocalIARunner.Uno.exe`.
- **Linux** : extrais `LocalIARunner-Uno-Linux-x64.zip`, puis lance `./LocalIARunner.Uno` depuis le dossier extrait. Si nécessaire, rends le fichier exécutable avec `chmod +x ./LocalIARunner.Uno`.
- **macOS** : extrais l'archive correspondant au processeur (`macOS-x64` pour Intel ou `macOS-arm64` pour Apple Silicon), puis lance `./LocalIARunner.Uno` depuis Terminal. Cette bêta n'est pas signée ni notarisée.
- **Android** : télécharge `LocalIARunner-Uno-Android.apk`, ouvre-le sur le téléphone et confirme l'installation depuis ta source de téléchargement. Au premier lancement, choisis un modèle GGUF; il sera copié dans l'espace privé de l'application. Clique sur **Actualiser** ou envoie un message pour que llama.cpp se lance et charge le modèle. L'APK ne contient pas le modèle GGUF; prévois l'espace disque nécessaire.

L'APK est signé avec une clé temporaire de compilation; désinstalle une bêta précédente avant d'installer la suivante.

Pour développer avec le projet, installer le SDK .NET 10 et suivre les prérequis Uno Platform pour Android. Le projet est ciblé par `global.json` avec `Uno.Sdk` 6.7.30.
