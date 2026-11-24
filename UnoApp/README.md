# Local IA Runner — application Uno Platform (bêta)

Cette application Uno est ajoutée à côté de l'application WinForms. Le code WinForms et son backend existant restent en place et continuent d'être la version principale.

## Ce que contient cette première bêta

- Une interface commune pour Windows, Linux, macOS et Android, écrite en C# et XAML avec Uno Platform.
- Une liste de discussions enregistrée localement, avec réponses en streaming et bouton d'annulation.
- Une connexion à un serveur compatible avec l'API OpenAI de llama.cpp (`/v1/models` et `/v1/chat/completions`).
- Une adresse de serveur et une clé API configurables depuis l'application.

Cette bêta Uno n'embarque pas encore llama.cpp ou stable-diffusion.cpp et ne reprend pas encore les outils, les réglages avancés, le forum, les sous-agents ni les fonctions propres au backend WinForms. Il faut lancer un serveur llama.cpp séparément et connecter l'application à son API. Les fichiers et les fonctions de la version WinForms restent conservés dans le dépôt.

Les conversations sont enregistrées localement dans le dossier de données de l'application. La clé API reste seulement en mémoire pendant l'exécution et n'est pas enregistrée.

## Démarrage

Lance un serveur llama.cpp avec un modèle GGUF, puis dans l'application saisis son adresse, par exemple `http://127.0.0.1:8080`, et appuie sur **Enregistrer et connecter**. La liste des modèles est chargée depuis `/v1/models`.

Sur Android, `127.0.0.1` désigne le téléphone lui-même. Pour joindre un serveur sur un autre ordinateur, indique son adresse IP locale et configure llama.cpp pour accepter les connexions du réseau local. L'application n'accepte HTTP que pour localhost et les adresses privées; les adresses publiques doivent utiliser HTTPS. N'expose pas un serveur sans authentification sur un réseau public.

## Téléchargements bêta

Les archives Windows, Linux et macOS (Intel et ARM64), ainsi que l'APK Android, sont publiés dans les préversions GitHub intitulées **Local IA Runner — bêta Uno Platform**. Ce sont des versions de test séparées des releases stables WinForms. L'APK est signé avec une clé temporaire de compilation; il faut désinstaller une bêta précédente avant d'installer la suivante.

Pour développer avec le projet, installer le SDK .NET 10 et suivre les prérequis Uno Platform pour Android. Le projet est ciblé par `global.json` avec `Uno.Sdk` 6.7.30.
