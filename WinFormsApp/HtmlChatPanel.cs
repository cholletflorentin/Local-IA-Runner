using System.Diagnostics;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAIApp;

/// <summary>
/// HTML-rendered conversation view. Model text is passed as data and escaped by
/// the renderer; it is never treated as trusted HTML.
/// </summary>
public sealed class ChatPanel : DoubleBufferedPanel
{
    private const string WebViewRuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private readonly WebView2 _webView;
    private readonly Panel _fallbackPanel;
    private readonly Label _fallbackLabel;
    private readonly LinkLabel _runtimeLink;
    private readonly List<ChatEntry> _entries = new();
    private readonly StringBuilder _pendingThinkingMarkup = new();
    private ChatEntry? _streamEntry;
    private string _title = "Nouvelle conversation";
    private string _modelName = "Aucun modèle sélectionné";
    private string _activity = "";
    private bool _isStreaming;
    private bool _streamIsThinking;
    private bool _documentReady;
    private bool _closing;

    public ChatPanel()
    {
        BackColor = Color.White;
        Padding = Padding.Empty;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false
        };
        _webView.NavigationCompleted += OnNavigationCompleted;
        Controls.Add(_webView);

        _fallbackPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Visible = true
        };
        _fallbackLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 82,
            Padding = new Padding(22, 22, 22, 6),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(73, 80, 96),
            Font = new Font("Segoe UI", 11f),
            Text = "Préparation de l’affichage du chat…"
        };
        _runtimeLink = new LinkLabel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Text = "Installer le runtime Microsoft Edge WebView2",
            Visible = false,
            TextAlign = ContentAlignment.MiddleCenter,
            LinkColor = Color.FromArgb(76, 91, 203),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _runtimeLink.LinkClicked += async (_, _) => await InstallWebViewRuntimeAsync();
        _fallbackPanel.Controls.Add(_runtimeLink);
        _fallbackPanel.Controls.Add(_fallbackLabel);
        Controls.Add(_fallbackPanel);

        _ = InitializeBrowserAsync();
    }

    public void SetConversationTitle(string? title)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "Nouvelle conversation" : title.Trim();
        RunScript($"window.localIA?.setHeader({ToJson(_title)}, {ToJson(_modelName)});");
    }

    public void SetModelName(string? modelName)
    {
        _modelName = string.IsNullOrWhiteSpace(modelName) ? "Aucun modèle sélectionné" : modelName.Trim();
        RunScript($"window.localIA?.setHeader({ToJson(_title)}, {ToJson(_modelName)});");
    }

    public void UpdateTheme()
    {
        BackColor = Color.White;
        _fallbackPanel.BackColor = Color.White;
    }

    public void Clear()
    {
        RunOnUi(() =>
        {
            _entries.Clear();
            _streamEntry = null;
            _activity = "";
            _isStreaming = false;
            _streamIsThinking = false;
            _pendingThinkingMarkup.Clear();
            RenderAll(forceBottom: true);
        });
    }

    public void RenderMessages(IEnumerable<Message> messages)
    {
        RunOnUi(() =>
        {
            _entries.Clear();
            _streamEntry = null;
            _activity = "";
            _isStreaming = false;
            _streamIsThinking = false;
            _pendingThinkingMarkup.Clear();
            foreach (var message in messages ?? Enumerable.Empty<Message>())
            {
                var isUser = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
                var parsed = ParseThinking(message.Content ?? "");
                _entries.Add(new ChatEntry
                {
                    Role = isUser ? "user" : "assistant",
                    Sender = isUser ? "Vous" : "Assistant",
                    Time = (message.Timestamp == default ? DateTime.Now : message.Timestamp).ToString("HH:mm"),
                    Thinking = parsed.Thinking,
                    Content = parsed.Response
                });
            }
            RenderAll(forceBottom: true);
        });
    }

    public void AddMessage(string sender, string content, bool isUser)
    {
        RunOnUi(() =>
        {
            var parsed = ParseThinking(content ?? "");
            if (!isUser && string.IsNullOrWhiteSpace(parsed.Thinking) && _streamEntry != null && _streamEntry.Thinking.Length > 0)
                parsed.Thinking = _streamEntry.Thinking;

            _entries.Add(new ChatEntry
            {
                Role = isUser ? "user" : "assistant",
                Sender = isUser ? "Vous" : (string.IsNullOrWhiteSpace(sender) ? "Assistant" : sender.Trim().TrimEnd(':')),
                Time = DateTime.Now.ToString("HH:mm"),
                Thinking = parsed.Thinking,
                Content = parsed.Response
            });
            if (isUser)
                _activity = "";
            _streamEntry = null;
            _isStreaming = false;
            _streamIsThinking = false;
            _pendingThinkingMarkup.Clear();
            RenderAll(forceBottom: true);
        });
    }

    public void AddSystemMessage(string content, MessageType type)
    {
        RunOnUi(() =>
        {
            _entries.Add(new ChatEntry
            {
                Role = "system",
                Sender = type switch
                {
                    MessageType.Success => "Terminé",
                    MessageType.Warning => "Attention",
                    MessageType.Error => "Erreur",
                    _ => "Information"
                },
                SystemKind = type.ToString().ToLowerInvariant(),
                Time = DateTime.Now.ToString("HH:mm"),
                Content = content ?? ""
            });
            RenderAll(forceBottom: true);
        });
    }

    public void AddToolStatusMessage(string content)
    {
        _activity = string.IsNullOrWhiteSpace(content) ? "Utilisation d’un outil…" : content.Trim();
        RunOnUi(() => RunScript($"window.localIA?.setActivity({ToJson(_activity)});"));
    }

    public void AppendThinkingPreviewToken(string content)
    {
        if (string.IsNullOrEmpty(content))
            return;
        RunOnUi(() =>
        {
            EnsureStreamEntry();
            _streamEntry!.Thinking += content;
            _activity = "Réflexion en cours…";
            RenderStreamingEntry();
            RunScript($"window.localIA?.setActivity({ToJson(_activity)});");
        });
    }

    public void ShowThinkingActivity()
    {
        _activity = "Réflexion en cours…";
        RunOnUi(() => RunScript($"window.localIA?.setActivity({ToJson(_activity)});"));
    }

    public void ClearThinkingPreview()
    {
        RunOnUi(() =>
        {
            if (_streamEntry != null)
            {
                _streamEntry.Thinking = "";
                if (string.IsNullOrWhiteSpace(_streamEntry.Content) && !_isStreaming)
                {
                    _entries.Remove(_streamEntry);
                    _streamEntry = null;
                }
                else
                {
                    RenderStreamingEntry();
                }
            }
            if (_activity == "Réflexion en cours…")
                _activity = "";
            RunScript($"window.localIA?.setActivity({ToJson(_activity)});");
        });
    }

    public void StartStreamingMessage()
    {
        RunOnUi(() =>
        {
            _activity = "";
            _isStreaming = true;
            _streamIsThinking = false;
            _pendingThinkingMarkup.Clear();
            if (_streamEntry == null)
                EnsureStreamEntry();
            else
                RenderStreamingEntry();
            RunScript("window.localIA?.setActivity('');");
        });
    }

    public void AppendStreamingToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        RunOnUi(() =>
        {
            if (!_isStreaming)
                return;
            EnsureStreamEntry();
            AppendStreamMarkup(token);
            RenderStreamingEntry();
        });
    }

    public void EndStreamingMessage(string fullContent)
    {
        RunOnUi(() =>
        {
            _activity = "";
            if (_streamEntry == null)
            {
                _isStreaming = false;
                RunScript("window.localIA?.setActivity('');");
                return;
            }

            if (_pendingThinkingMarkup.Length > 0)
            {
                AppendStreamSegment(_pendingThinkingMarkup.ToString());
                _pendingThinkingMarkup.Clear();
            }

            if (!string.IsNullOrWhiteSpace(fullContent))
            {
                var parsed = ParseThinking(fullContent);
                if (!string.IsNullOrWhiteSpace(parsed.Thinking))
                    _streamEntry.Thinking = parsed.Thinking;
                _streamEntry.Content = parsed.Response;
            }

            _streamEntry.IsStreaming = false;
            _isStreaming = false;
            _streamIsThinking = false;
            _streamEntry = null;
            _pendingThinkingMarkup.Clear();
            RenderAll(forceBottom: true);
        });
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var userDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalIARunner", "WebView2");
            Directory.CreateDirectory(userDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDirectory);
            await _webView.EnsureCoreWebView2Async(environment);
            if (_closing || IsDisposed)
                return;

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            using var htmlStream = typeof(ChatPanel).Assembly.GetManifestResourceStream("LocalAIApp.Assets.chat-surface.html")
                ?? throw new InvalidOperationException("La ressource HTML de l’interface du chat est absente de l’application.");
            using var htmlReader = new StreamReader(htmlStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var html = await htmlReader.ReadToEndAsync();

            _webView.Visible = true;
            _fallbackPanel.Visible = false;
            _webView.CoreWebView2.NavigateToString(html);
        }
        catch (Exception exception)
        {
            if (_closing || IsDisposed || !IsHandleCreated)
                return;
            RunOnUi(() =>
            {
                _documentReady = false;
                _webView.Visible = false;
                _fallbackPanel.Visible = true;
                _fallbackPanel.BringToFront();
                _fallbackLabel.Text = "Impossible d’initialiser la vue HTML. Vérifie que le runtime Microsoft Edge WebView2 est installé.\n" + exception.Message;
                _fallbackLabel.Height = 110;
                _runtimeLink.Text = File.Exists(GetWebViewBootstrapperPath())
                    ? "Installer le runtime Microsoft Edge WebView2"
                    : "Télécharger le runtime Microsoft Edge WebView2";
                _runtimeLink.Visible = true;
            });
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            RunOnUi(() =>
            {
                _documentReady = false;
                _webView.Visible = false;
                _fallbackPanel.Visible = true;
                _fallbackPanel.BringToFront();
                _fallbackLabel.Text = $"Impossible d’afficher le chat HTML (erreur WebView2 : {args.WebErrorStatus}).";
                _fallbackLabel.Height = 110;
                _runtimeLink.Visible = false;
            });
            return;
        }

        _documentReady = true;
        RunScript($"window.localIA?.setHeader({ToJson(_title)}, {ToJson(_modelName)});");
        RenderAll(forceBottom: true);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var request = JObject.Parse(args.WebMessageAsJson);
            var action = (string?)request["action"];
            var text = (string?)request["text"] ?? "";
            switch (action)
            {
                case "copy":
                    Clipboard.SetText(text);
                    Notify("Copié dans le presse-papiers.");
                    break;
                case "download":
                    SaveCodeFile((string?)request["filename"] ?? "script.txt", text);
                    break;
                case "openLink":
                    var uri = (string?)request["url"] ?? "";
                    if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) &&
                        parsedUri.Scheme is "http" or "https" or "mailto")
                        OpenExternalUrl(parsedUri.AbsoluteUri);
                    break;
            }
        }
        catch (Exception exception)
        {
            Notify("Action impossible : " + exception.Message);
        }
    }

    private void SaveCodeFile(string fileName, string content)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "script.txt";
        using var dialog = new SaveFileDialog
        {
            Title = "Télécharger le code",
            FileName = safeName,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;
        File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Notify("Fichier enregistré.");
    }

    private async Task InstallWebViewRuntimeAsync()
    {
        var installerPath = GetWebViewBootstrapperPath();
        if (!File.Exists(installerPath))
        {
            OpenExternalUrl(WebViewRuntimeUrl);
            return;
        }

        _runtimeLink.Enabled = false;
        _runtimeLink.Text = "Installation du runtime WebView2…";
        try
        {
            using var installer = Process.Start(new ProcessStartInfo(installerPath, "/silent /install")
            {
                UseShellExecute = true,
                CreateNoWindow = true
            });
            if (installer == null)
                throw new InvalidOperationException("Le programme d’installation de WebView2 n’a pas démarré.");
            await Task.Run(() => installer.WaitForExit());
            if (installer.ExitCode != 0)
                throw new InvalidOperationException($"L’installation du runtime a échoué (code {installer.ExitCode}).");

            MessageBox.Show(
                FindForm(),
                "Le runtime WebView2 est installé. Ferme puis relance Local IA Runner pour charger l’interface HTML.",
                "Installation terminée",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            _runtimeLink.Enabled = true;
            _runtimeLink.Text = "Réessayer ou télécharger WebView2";
            MessageBox.Show(FindForm(), exception.Message, "Installation de WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string GetWebViewBootstrapperPath() => Path.Combine(
        AppContext.BaseDirectory,
        "Dependencies",
        "MicrosoftEdgeWebView2Setup.exe");

    private static void OpenExternalUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void EnsureStreamEntry()
    {
        if (_streamEntry != null)
            return;
        _streamEntry = new ChatEntry
        {
            Role = "assistant",
            Sender = "Assistant",
            Time = DateTime.Now.ToString("HH:mm"),
            IsStreaming = true
        };
        _entries.Add(_streamEntry);
        RenderAll(forceBottom: true);
    }

    private void AppendStreamMarkup(string text)
    {
        _pendingThinkingMarkup.Append(text);
        var combined = _pendingThinkingMarkup.ToString();
        _pendingThinkingMarkup.Clear();
        var position = 0;
        while (position < combined.Length)
        {
            var openIndex = combined.IndexOf("<think>", position, StringComparison.OrdinalIgnoreCase);
            var closeIndex = combined.IndexOf("</think>", position, StringComparison.OrdinalIgnoreCase);
            var opening = openIndex >= 0 && (closeIndex < 0 || openIndex < closeIndex);
            var tagIndex = opening ? openIndex : closeIndex;
            if (tagIndex < 0)
            {
                var keepLength = FindPartialMarkerSuffixLength(combined, position);
                var safeEnd = combined.Length - keepLength;
                if (safeEnd > position)
                    AppendStreamSegment(combined.Substring(position, safeEnd - position));
                if (keepLength > 0)
                    _pendingThinkingMarkup.Append(combined, safeEnd, keepLength);
                return;
            }

            if (tagIndex > position)
                AppendStreamSegment(combined.Substring(position, tagIndex - position));
            _streamIsThinking = opening;
            position = tagIndex + (opening ? 7 : 8);
        }
    }

    private static int FindPartialMarkerSuffixLength(string text, int start)
    {
        var maxLength = Math.Min(7, text.Length - start);
        for (var length = maxLength; length > 0; length--)
        {
            var suffix = text.Substring(text.Length - length, length);
            if ("<think>".StartsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                "</think>".StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return length;
        }
        return 0;
    }

    private void AppendStreamSegment(string text)
    {
        if (text.Length == 0 || _streamEntry == null)
            return;
        if (_streamIsThinking)
            _streamEntry.Thinking += text;
        else
            _streamEntry.Content += text;
    }

    private static (string Thinking, string Response) ParseThinking(string content)
    {
        var thinking = new StringBuilder();
        var response = new StringBuilder();
        var target = response;
        var position = 0;
        while (position < content.Length)
        {
            var openIndex = content.IndexOf("<think>", position, StringComparison.OrdinalIgnoreCase);
            var closeIndex = content.IndexOf("</think>", position, StringComparison.OrdinalIgnoreCase);
            var opening = openIndex >= 0 && (closeIndex < 0 || openIndex < closeIndex);
            var tagIndex = opening ? openIndex : closeIndex;
            if (tagIndex < 0)
            {
                target.Append(content, position, content.Length - position);
                break;
            }
            target.Append(content, position, tagIndex - position);
            if (opening)
            {
                target = thinking;
                position = tagIndex + 7;
            }
            else
            {
                target = response;
                position = tagIndex + 8;
            }
        }
        return (thinking.ToString().Trim(), response.ToString().Trim());
    }

    private void RenderAll(bool forceBottom)
    {
        if (!_documentReady || IsDisposed)
            return;
        var json = ToJson(_entries.Select(entry => entry.ToViewModel()).ToArray());
        RunScript($"window.localIA?.render({json}, {(forceBottom ? "true" : "false")});");
        RunScript($"window.localIA?.setActivity({ToJson(_activity)});");
    }

    private void RenderStreamingEntry()
    {
        if (_streamEntry == null || !_documentReady || IsDisposed)
            return;
        var index = _entries.IndexOf(_streamEntry);
        if (index < 0)
            return;
        var json = ToJson(_streamEntry.ToViewModel());
        RunScript($"window.localIA?.updateStreaming({index}, {json});");
    }

    private void Notify(string text) => RunScript($"window.localIA?.notify({ToJson(text)});");

    private void RunScript(string script)
    {
        if (!_documentReady || _closing || IsDisposed || !_webView.IsHandleCreated)
            return;
        try { _ = _webView.ExecuteScriptAsync(script); }
        catch { }
    }

    private static string ToJson(object? value) => JsonConvert.SerializeObject(
        value,
        new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });

    private void RunOnUi(Action action)
    {
        if (IsDisposed || _closing)
            return;
        if (InvokeRequired && IsHandleCreated)
        {
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
            return;
        }
        action();
    }

    protected override void Dispose(bool disposing)
    {
        _closing = true;
        if (disposing)
            _webView.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ChatEntry
    {
        public string Role { get; set; } = "assistant";
        public string Sender { get; set; } = "Assistant";
        public string Time { get; set; } = "";
        public string Content { get; set; } = "";
        public string Thinking { get; set; } = "";
        public string SystemKind { get; set; } = "info";
        public bool IsStreaming { get; set; }

        public object ToViewModel() => new
        {
            role = Role,
            sender = Sender,
            time = Time,
            content = Content,
            thinking = Thinking,
            systemKind = SystemKind,
            streaming = IsStreaming
        };
    }
}
