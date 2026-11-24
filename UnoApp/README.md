# Local IA Runner — application Uno Platform (bêta)

Cette application Uno est ajoutée à côté de l'application WinForms. Le code WinForms et son backend existant restent en place et continuent d'être la version principale.

## Ce que contient cette bêta

- Une interface commune pour Windows, Linux, macOS et Android, écrite en C# et XAML avec Uno Platform.
- Une liste de discussions enregistrée localement, avec réponses en streaming et bouton d'annulation.
- Une connexion à un serveur compatible avec l'API OpenAI de llama.cpp (`/v1/models` et `/v1/chat/completions`).
- Une adresse de serveur et une clé API configurables depuis l'application.
- Les archives desktop incluent le moteur CPU llama.cpp b12676 et un petit lanceur pour démarrer le serveur.
- Les notices de licence sont incluses dans chaque archive desktop.

L'application Uno reste un client : elle ne charge pas directement les modèles et ne démarre pas le moteur toute seule. Sur Windows, Linux et macOS, utilise `run-llama-server.cmd` ou `sh ./run-llama-server.sh` depuis le dossier de l'archive, en passant le chemin d'un modèle GGUF. Exemple :

```text
Windows : run-llama-server.cmd -m "C:\ia\text\modele.gguf" --host 127.0.0.1 --port 8080
Linux/macOS : sh ./run-llama-server.sh -m "/chemin/vers/modele.gguf" --host 127.0.0.1 --port 8080
```

Configure ensuite `http://127.0.0.1:8080` dans l'application. L'APK Android contient l'archive compressée llama.cpp b12676 pour ARM64; l'interface Uno reste toutefois un client réseau et ne lance pas encore le serveur Android elle-même. L'application Uno ne reprend pas encore stable-diffusion.cpp, les outils, le forum, les sous-agents ni les autres fonctions propres au backend WinForms. Les fichiers et les fonctions de la version WinForms restent conservés dans le dépôt.

Les conversations sont enregistrées localement dans le dossier de données de l'application. La clé API reste seulement en mémoire pendant l'exécution et n'est pas enregistrée.

## Démarrage

Lance un serveur llama.cpp avec un modèle GGUF, puis dans l'application saisis son adresse, par exemple `http://127.0.0.1:8080`, et appuie sur **Enregistrer et connecter**. La liste des modèles est chargée depuis `/v1/models`.

Sur Android, `127.0.0.1` désigne le téléphone lui-même. Pour joindre un serveur sur un autre ordinateur, indique son adresse IP locale et configure llama.cpp pour accepter les connexions du réseau local. L'application n'accepte HTTP que pour localhost et les adresses privées; les adresses publiques doivent utiliser HTTPS. N'expose pas un serveur sans authentification sur un réseau public.

## Installer les versions bêta

Télécharge les paquets depuis la dernière préversion GitHub intitulée **Local IA Runner — bêta Uno Platform**. Cette bêta est distincte de la version WinForms stable.

- **Windows** : extrais `LocalIARunner-Uno-Windows-x64.zip`, puis lance `LocalIARunner.Uno.exe`.
- **Linux** : extrais `LocalIARunner-Uno-Linux-x64.zip`, puis lance `./LocalIARunner.Uno` depuis le dossier extrait. Si nécessaire, rends le fichier exécutable avec `chmod +x ./LocalIARunner.Uno`.
- **macOS** : extrais l'archive correspondant au processeur (`macOS-x64` pour Intel ou `macOS-arm64` pour Apple Silicon), puis lance `./LocalIARunner.Uno` depuis Terminal. Cette bêta n'est pas signée ni notarisée.
- **Android** : télécharge `LocalIARunner-Uno-Android.apk`, ouvre-le sur le téléphone et confirme l'installation depuis ta source de téléchargement. L'APK contient llama.cpp b12676; l'application doit encore se connecter à un serveur déjà lancé.

L'APK est signé avec une clé temporaire de compilation; désinstalle une bêta précédente avant d'installer la suivante.

Pour développer avec le projet, installer le SDK .NET 10 et suivre les prérequis Uno Platform pour Android. Le projet est ciblé par `global.json` avec `Uno.Sdk` 6.7.30.
