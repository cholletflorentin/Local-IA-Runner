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

Configure ensuite `http://127.0.0.1:8080` dans l'application. L'APK Android reste un client réseau; la préversion GitHub fournit séparément l'archive llama.cpp b12676 pour Android ARM64. L'application Uno ne reprend pas encore stable-diffusion.cpp, les outils, le forum, les sous-agents ni les autres fonctions propres au backend WinForms. Les fichiers et les fonctions de la version WinForms restent conservés dans le dépôt.

Les conversations sont enregistrées localement dans le dossier de données de l'application. La clé API reste seulement en mémoire pendant l'exécution et n'est pas enregistrée.

## Démarrage

Lance un serveur llama.cpp avec un modèle GGUF, puis dans l'application saisis son adresse, par exemple `http://127.0.0.1:8080`, et appuie sur **Enregistrer et connecter**. La liste des modèles est chargée depuis `/v1/models`.

Sur Android, `127.0.0.1` désigne le téléphone lui-même. Pour joindre un serveur sur un autre ordinateur, indique son adresse IP locale et configure llama.cpp pour accepter les connexions du réseau local. L'application n'accepte HTTP que pour localhost et les adresses privées; les adresses publiques doivent utiliser HTTPS. N'expose pas un serveur sans authentification sur un réseau public.

## Téléchargements bêta

Les archives Windows, Linux et macOS (Intel et ARM64), ainsi que l'APK Android, sont publiés dans les préversions GitHub intitulées **Local IA Runner — bêta Uno Platform**. Ce sont des versions de test séparées des releases stables WinForms. L'APK est signé avec une clé temporaire de compilation; il faut désinstaller une bêta précédente avant d'installer la suivante.

Pour développer avec le projet, installer le SDK .NET 10 et suivre les prérequis Uno Platform pour Android. Le projet est ciblé par `global.json` avec `Uno.Sdk` 6.7.30.
