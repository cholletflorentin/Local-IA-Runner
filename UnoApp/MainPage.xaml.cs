using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LocalIARunner.Uno;

public sealed partial class MainPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ObservableCollection<string> _models = [];
    private AppState _state = new();
    private Conversation? _activeConversation;
    private CancellationTokenSource? _generationCts;
    private string _statePath = "";
    private bool _isNarrow;
    private bool _isPreparingRequest;

    public ObservableCollection<Conversation> Conversations { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public MainPage()
    {
        InitializeComponent();
        ModelPicker.ItemsSource = _models;
#if ANDROID
        AndroidModelPanel.Visibility = Visibility.Visible;
#endif
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = AppContext.BaseDirectory;
            dataDirectory = Path.Combine(dataDirectory, "LocalIARunner", "Uno");
            Directory.CreateDirectory(dataDirectory);
            _statePath = Path.Combine(dataDirectory, "state.json");
            if (File.Exists(_statePath))
            {
                var json = await File.ReadAllTextAsync(_statePath);
                _state = JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
            }

            EndpointBox.Text = _state.Endpoint;
            foreach (var conversation in _state.Conversations.OrderByDescending(c => c.UpdatedAt))
                Conversations.Add(conversation);
#if ANDROID
            AndroidModelPanel.Visibility = Visibility.Visible;
            if (!File.Exists(_state.LocalModelPath))
            {
                _state.LocalModelPath = "";
                _state.LocalModelName = "";
            }
            LocalModelText.Text = string.IsNullOrWhiteSpace(_state.LocalModelPath)
                ? "Aucun modèle GGUF choisi"
                : "Modèle : " + (_state.LocalModelName.Length > 0
                    ? _state.LocalModelName
                    : Path.GetFileName(_state.LocalModelPath));
            SetStatus(string.IsNullOrWhiteSpace(_state.LocalModelPath)
                ? "Choisis un modèle GGUF sur l’appareil; llama.cpp démarrera automatiquement au premier message."
                : "Modèle prêt. llama.cpp démarrera automatiquement au premier message.");
#else
            StatusText.Text = "Serveur local compatible llama.cpp prêt à configurer.";
#endif
            if (Conversations.Count > 0)
                ConversationList.SelectedItem = Conversations[0];
        }
        catch (Exception ex)
        {
            SetStatus("Chargement des discussions impossible : " + ex.Message);
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 680;
        if (_isNarrow == narrow) return;
        _isNarrow = narrow;
        RootGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(220);
        ConversationPane.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        MobileStatusText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        MobileConversationPicker.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        TaglineText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = narrow ? "Local IA" : "Local IA Runner";
        ModelPicker.MinWidth = narrow ? 90 : 160;
        ApiKeyBox.Width = narrow ? 96 : 180;
        ConnectionGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 96 : 180);
        ConnectButton.Content = narrow ? "Connecter" : "Enregistrer et connecter";
        NewChatButton.Content = narrow ? "+" : "Nouvelle discussion";
        RefreshModelsButton.Content = narrow ? "↻" : "Actualiser";
        if (narrow) MobileConversationPicker.SelectedItem = _activeConversation;
    }

    private void OnConversationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _activeConversation = ConversationList.SelectedItem as Conversation;
        if (!ReferenceEquals(MobileConversationPicker.SelectedItem, _activeConversation))
            MobileConversationPicker.SelectedItem = _activeConversation;
        ShowConversation(_activeConversation);
    }

    private void OnMobileConversationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MobileConversationPicker.SelectedItem is Conversation selected)
            ConversationList.SelectedItem = selected;
    }

    private void ShowConversation(Conversation? conversation)
    {
        Messages.Clear();
        if (conversation is null) return;
        foreach (var message in conversation.History)
            Messages.Add(message);
        ScrollToBottom();
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        ConversationList.SelectedItem = null;
        MobileConversationPicker.SelectedItem = null;
        _activeConversation = null;
        Messages.Clear();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveConnectionSettings();
            ValidateEndpoint();
            await SaveStateAsync();
#if ANDROID
            if (LocalLlamaEndpointIsSelected() && !string.IsNullOrWhiteSpace(_state.LocalModelPath))
            {
                SetStatus("Modèle prêt. llama.cpp démarrera au premier message.");
                return;
            }
#endif
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Connexion ou démarrage de llama.cpp impossible : " + ex.Message);
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
#if ANDROID
        if (LocalLlamaEndpointIsSelected() && !string.IsNullOrWhiteSpace(_state.LocalModelPath))
        {
            SetStatus("llama.cpp démarrera automatiquement quand tu enverras un message.");
            return;
        }
#endif
        await RefreshModelsAsync();
    }

    private async Task<bool> RefreshModelsAsync()
    {
        try
        {
            SaveConnectionSettings();
            ValidateEndpoint();
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildApiUrl("models"));
            AddAuthorization(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(body);
            _models.Clear();
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } model)
                        _models.Add(model);
            }
            if (_models.Count > 0) ModelPicker.SelectedIndex = 0;
            SetStatus(_models.Count == 0 ? "Connecté, mais aucun modèle n’a été retourné." : $"Connecté · {_models.Count} modèle(s) disponible(s).");
            await SaveStateAsync();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Connexion impossible : " + ex.Message);
            return false;
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async void OnChooseLocalModelClick(object sender, RoutedEventArgs e)
    {
#if ANDROID
        ChooseModelButton.IsEnabled = false;
        try
        {
            var uri = await LocalIARunner.Uno.Droid.MainActivity.PickModelAsync();
            if (uri is null) return;

            SetStatus("Copie du fichier GGUF dans l’espace privé de l’application…");
            var dispatcher = DispatcherQueue;
            var imported = await LocalIARunner.Uno.Droid.AndroidLlamaEngine.ImportModelAsync(uri, copiedBytes =>
            {
                dispatcher.TryEnqueue(() =>
                    SetStatus($"Copie du modèle : {copiedBytes / (1024 * 1024)} Mio."));
            });

            var previousPath = _state.LocalModelPath;
            LocalIARunner.Uno.Droid.AndroidLlamaEngine.Stop();
            _state.LocalModelPath = imported.Path;
            _state.LocalModelName = imported.DisplayName;
            if (!string.Equals(previousPath, imported.Path, StringComparison.Ordinal))
                LocalIARunner.Uno.Droid.AndroidLlamaEngine.DeleteManagedModel(previousPath);

            _models.Clear();
            ModelPicker.SelectedIndex = -1;
            LocalModelText.Text = "Modèle : " + imported.DisplayName;
            await SaveStateAsync();
            SetStatus("Modèle choisi. llama.cpp démarrera automatiquement au premier message.");
        }
        catch (Exception ex)
        {
            SetStatus("Import du modèle GGUF impossible : " + ex.Message);
        }
        finally
        {
            ChooseModelButton.IsEnabled = true;
        }
#else
        SetStatus("Le choix d’un modèle local est disponible dans l’APK Android.");
#endif
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private async Task SendPromptAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 || _generationCts is not null || _isPreparingRequest) return;

        _isPreparingRequest = true;
        SendButton.IsEnabled = false;
        try
        {
            SaveConnectionSettings();
            ValidateEndpoint();
#if ANDROID
            if (LocalLlamaEndpointIsSelected() && !string.IsNullOrWhiteSpace(_state.LocalModelPath))
            {
                SetStatus("Démarrage de llama.cpp et chargement du modèle… Cela peut prendre un moment.");
                await EnsureAndroidLocalEngineAsync();
                if (!await RefreshModelsAsync() || _models.Count == 0)
                    return;
            }
            else if (LocalLlamaEndpointIsSelected() && _models.Count == 0)
            {
                await RefreshModelsAsync();
            }
#endif
            if (_models.Count == 0)
            {
#if ANDROID
                SetStatus(LocalLlamaEndpointIsSelected()
                    ? "Choisis un modèle GGUF local pour démarrer llama.cpp au premier message, ou connecte-toi à un serveur."
                    : "Connecte-toi à un serveur et actualise la liste des modèles.");
#else
                SetStatus("Connecte-toi d’abord à un serveur et actualise la liste des modèles.");
#endif
                return;
            }
            await SaveStateAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Préparation de la requête impossible : " + ex.Message);
            return;
        }
        finally
        {
            _isPreparingRequest = false;
            if (_generationCts is null)
                SendButton.IsEnabled = true;
        }
        if (_activeConversation is null)
        {
            _activeConversation = new Conversation();
            Conversations.Insert(0, _activeConversation);
            ConversationList.SelectedItem = _activeConversation;
        }

        var userMessage = new ChatMessage { Role = "user", Content = prompt };
        _activeConversation.History.Add(userMessage);
        Messages.Add(userMessage);
        if (_activeConversation.History.Count == 1)
            _activeConversation.Title = prompt.Length > 52 ? prompt[..52].TrimEnd() + "…" : prompt;
        _activeConversation.UpdatedAt = DateTime.UtcNow;
        PromptBox.Text = "";

        var assistantMessage = new ChatMessage { Role = "assistant", Content = "" };
        _activeConversation.History.Add(assistantMessage);
        Messages.Add(assistantMessage);
        _generationCts = new CancellationTokenSource();
        _generationCts.CancelAfter(TimeSpan.FromMinutes(15));
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SetStatus("Génération en cours…");
        ScrollToBottom();

        try
        {
            await StreamCompletionAsync(_activeConversation, assistantMessage, _generationCts.Token);
            if (assistantMessage.Content.Length == 0)
                _activeConversation.History.Remove(assistantMessage);
            SetStatus("Génération terminée.");
        }
        catch (OperationCanceledException)
        {
            if (assistantMessage.Content.Length == 0)
                _activeConversation.History.Remove(assistantMessage);
            SetStatus("Génération annulée.");
        }
        catch (Exception ex)
        {
            assistantMessage.Content = "Erreur de génération : " + ex.Message;
            SetStatus("La requête a échoué.");
        }
        finally
        {
            _generationCts.Dispose();
            _generationCts = null;
            SendButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _activeConversation.UpdatedAt = DateTime.UtcNow;
            await SaveStateAsync();
            ScrollToBottom();
        }
    }

    private async Task StreamCompletionAsync(Conversation conversation, ChatMessage assistantMessage, CancellationToken cancellationToken)
    {
        var model = ModelPicker.SelectedItem as string ?? _models.FirstOrDefault() ?? "default";
        var messages = conversation.History
            .Where(m => !ReferenceEquals(m, assistantMessage))
            .Select(m => new { role = m.Role, content = m.Content })
            .ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("chat/completions"))
        {
            Content = JsonContent.Create(new { model, messages, stream = true })
        };
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Shorten(details, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;
            using var json = JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;
            if (!delta.TryGetProperty("content", out var content)) continue;
            var fragment = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray().Where(x => x.TryGetProperty("text", out _)).Select(x => x.GetProperty("text").GetString())),
                _ => null
            };
            if (!string.IsNullOrEmpty(fragment))
            {
                assistantMessage.Content += fragment;
                ScrollToBottom();
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _generationCts?.Cancel();

#if ANDROID
    private bool LocalLlamaEndpointIsSelected() =>
        LocalIARunner.Uno.Droid.AndroidLlamaEngine.IsLoopbackEndpoint(_state.Endpoint);

    private async Task EnsureAndroidLocalEngineAsync()
    {
        if (string.IsNullOrWhiteSpace(_state.LocalModelPath) || !File.Exists(_state.LocalModelPath))
            throw new FileNotFoundException("Choisis à nouveau un fichier GGUF valide dans l’application.", _state.LocalModelPath);

        await LocalIARunner.Uno.Droid.AndroidLlamaEngine.EnsureStartedAsync(
            _state.LocalModelPath,
            _state.Endpoint,
            ApiKeyBox.Password.Trim());
    }
#endif

    private void SaveConnectionSettings()
    {
        _state.Endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? "http://127.0.0.1:8080" : EndpointBox.Text.Trim().TrimEnd('/');
    }

    private async Task SaveStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        try
        {
            _state.Conversations = Conversations.ToList();
            await File.WriteAllTextAsync(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch (Exception ex)
        {
            SetStatus("Sauvegarde impossible : " + ex.Message);
        }
    }

    private string BuildApiUrl(string path)
    {
        var endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? _state.Endpoint : EndpointBox.Text.Trim().TrimEnd('/');
        ValidateEndpoint(endpoint);
        var apiRoot = endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint : endpoint + "/v1";
        return apiRoot + "/" + path.TrimStart('/');
    }

    private void ValidateEndpoint(string? endpoint = null)
    {
        endpoint ??= string.IsNullOrWhiteSpace(EndpointBox.Text) ? _state.Endpoint : EndpointBox.Text.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("L’adresse doit commencer par http:// ou https://.");

        if (uri.Scheme != Uri.UriSchemeHttp) return;
        var host = uri.Host;
        var isLocalName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        var isPrivateAddress = false;
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            var bytes = address.GetAddressBytes();
            isPrivateAddress = System.Net.IPAddress.IsLoopback(address)
                || address.IsIPv6LinkLocal
                || (bytes.Length == 4 && (bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254)));
        }
        if (!isLocalName && !isPrivateAddress)
            throw new InvalidOperationException("HTTP non chiffré est limité à localhost et au réseau privé. Utilise HTTPS pour une adresse publique.");
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) return;

        var uri = request.RequestUri;
        if (uri is null) throw new InvalidOperationException("Adresse du serveur invalide.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (!System.Net.IPAddress.TryParse(uri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
                throw new InvalidOperationException("N’envoie pas de clé API en HTTP sur le réseau local; utilise HTTPS ou localhost.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private void ScrollToBottom()
    {
        MessageScroller.UpdateLayout();
        MessageScroller.ChangeView(null, MessageScroller.ScrollableHeight, null, true);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        MobileStatusText.Text = value;
    }

    private static string Shorten(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}
