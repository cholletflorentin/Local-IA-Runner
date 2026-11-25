using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAIApp
{
    public partial class MainForm : Form, IMessageFilter
    {
        private Process _pythonProcess;
        private StreamWriter _pythonInput;
        private readonly string _conversationsDir;
        private string _currentConversationFile = "";
        private List<Message> _currentConversation = new();
        private bool _isPythonReady = false;
        private bool _isStartingBackend = false;
        private bool _updateCheckInProgress = false;
        private bool _updateHelperLaunched = false;
        private string? _pendingUpdatePackagePath;
        private string _selectedModelPath = "";
        private string _modelsFolder = "";
        private string[] _discoveredModelPaths = Array.Empty<string>();
        private System.Threading.CancellationTokenSource? _modelsScanCancellation;
        private bool _isGenerating = false;
        private bool _isStreamingResponse = false;
        private bool _isCancellingGeneration = false;
        private bool _generationTimedOut = false;
        private string _pendingBackendError = "";
        private bool _imageGenerationActive = false;
        private string _selectedModelName = "";
        private string _currentConversationId = Guid.NewGuid().ToString("N");
        private string _currentConversationTitle = "";
        private bool _currentConversationTitleGenerated = false;
        private bool _conversationTitleRequestPending = false;
        private int? _editingMessageIndex;
        private readonly Dictionary<string, string> _pendingConversationTitleFiles = new(StringComparer.Ordinal);
        private readonly StringBuilder _streamingConversationTitle = new();
        private string _streamingConversationTitleId = "";
        private AppSettings _appSettings = new AppSettings();
        private readonly object _pythonInputLock = new object();
        private readonly object _pythonWriteQueueLock = new();
        private Task _pythonWriteQueue = Task.CompletedTask;
        
        // Watchdog pour détecter génération bloquée
        private System.Threading.Timer _generationWatchdog;
        private readonly object _streamBufferLock = new();
        private readonly StringBuilder _pendingStreamTokens = new();
        private System.Windows.Forms.Timer _streamRenderTimer;
        private DateTime _generationLastProgressTime;
        private int GenerationTimeoutSeconds => Math.Clamp(_appSettings.GenerationTimeoutSeconds, 30, 7200);
        private bool _receivedFirstToken = false;
        private long _generationOutputTokenCount;
        private long _generationFirstTokenTimestamp;
        private long _lastGenerationSpeedUpdateTimestamp;
        private long _generationPausedTimestamp;
        private long _generationPausedTicks;
        
        // File logger
        private readonly string _logFile = Path.Combine(Path.GetTempPath(), "LocalAIApp_Debug.log");
        private readonly object _logLock = new object();

        // UI Controls
        private ChatPanel _chatPanel;
        private InputPanel _inputPanel;
        private SidebarPanel _sidebarPanel;
        private StatusBar _statusBar;
        private ModernMenuStrip _menuStrip;
        private ImageGeneratorForm _imageGeneratorForm;
        private ForumForm? _forumForm;
        private SubagentActivityForm? _subagentActivityForm;

        public MainForm()
        {
            EnableHighDpiAwareness();
            InitializeComponent();
            Application.AddMessageFilter(this);
            _streamRenderTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _streamRenderTimer.Tick += (s, e) =>
            {
                FlushPendingStreamingTokens();
                UpdateGenerationSpeedIndicator();
            };
            Log("=== MainForm constructor START ===");
            
            _conversationsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalIARunner", "Conversations");
            
            Directory.CreateDirectory(_conversationsDir);
            _sidebarPanel.SetConversationsDirectory(_conversationsDir);
            _currentConversationFile = CreateNewConversationPath();
            
            LoadConversationList();
            Log("=== MainForm constructor END ===");
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int awareness);

        private void EnableHighDpiAwareness()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch
            {
                try { SetProcessDpiAwareness(2); } catch { }
                try { SetProcessDpiAwareness(1); } catch { }
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }

        private void InitializeComponent()
        {
            this.Text = "Local IA Runner";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            this.Size = new Size(1440, 900);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(1040, 680);
            this.BackColor = ThemeColors.Background;
            this.ForeColor = ThemeColors.TextPrimary;
            this.Font = new Font("Segoe UI", 9.5f);
            
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | 
                         ControlStyles.UserPaint | 
                         ControlStyles.DoubleBuffer | 
                         ControlStyles.ResizeRedraw | 
                         ControlStyles.OptimizedDoubleBuffer | 
                         ControlStyles.SupportsTransparentBackColor, true);
            this.UpdateStyles();

            // Main container with padding
            var mainContainer = new Panel { Dock = DockStyle.Fill, BackColor = ThemeColors.Background, Padding = new Padding(12, 8, 12, 10) };
            
            // Sidebar - Fixed width, Dock Left
            _sidebarPanel = new SidebarPanel { Dock = DockStyle.Left, Width = 320, Margin = new Padding(0, 0, 10, 0) };
            _sidebarPanel.ModelsFolderChanged += OnModelsFolderChanged;
            _sidebarPanel.ModelSelected += OnModelSelected;
            _sidebarPanel.ConversationSelected += OnConversationSelected;
            _sidebarPanel.NewConversationRequested += OnNewConversation;
            _sidebarPanel.DeleteConversationRequested += OnDeleteConversation;
            
            // Chat Area - Fill
            var chatContainer = new Panel { Dock = DockStyle.Fill, BackColor = ThemeColors.Background };
            
            var chatLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F));

            _chatPanel = new ChatPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            _chatPanel.EditMessageRequested += OnEditMessageRequested;
            chatLayout.Controls.Add(_chatPanel, 0, 0);

            _inputPanel = new InputPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _inputPanel.SendClicked += OnSendMessage;
            _inputPanel.CancelClicked += OnCancelGeneration;
            _inputPanel.MessageEditCancelled += OnMessageEditCancelled;
            chatLayout.Controls.Add(_inputPanel, 0, 1);

            var chatSurface = new RoundedPanel { BackColor = ThemeColors.Surface, Padding = new Padding(0), Dock = DockStyle.Fill };
            chatSurface.Controls.Add(chatLayout);
            chatContainer.Controls.Add(chatSurface);

            // Order matters: Fill first, then Left
            mainContainer.Controls.Add(chatContainer);
            mainContainer.Controls.Add(_sidebarPanel);
            
            this.Controls.Add(mainContainer);

            // Menu
            _menuStrip = new ModernMenuStrip();
            _menuStrip.NewConversationClicked += OnNewConversation;
            _menuStrip.OpenFolderClicked += OnOpenConversationsFolder;
            _menuStrip.SetModelsFolderClicked += OnSetModelsFolder;
            _menuStrip.RefreshModelsClicked += OnRefreshModels;
            _menuStrip.SearchKnownModelsClicked += (s, e) => ScanModelsFolder(forceQuickScan: true);
            _menuStrip.SearchAllModelsClicked += (s, e) => ScanModelsFolder(forceFullScan: true);
            _menuStrip.CancelModelSearchClicked += (s, e) => CancelModelScan();
            _menuStrip.RestartBackendClicked += OnRestartBackend;
            _menuStrip.SettingsClicked += OnSettings;
            _menuStrip.ImageGeneratorClicked += OnOpenImageGenerator;
            _menuStrip.ForumClicked += OnOpenForum;
            _menuStrip.SubagentActivityClicked += OnOpenSubagentActivity;
            _menuStrip.CheckForUpdatesClicked += OnCheckForUpdates;
            _menuStrip.ExitClicked += (s, e) => Application.Exit();
            this.Controls.Add(_menuStrip);
            this.MainMenuStrip = _menuStrip;

            // Status bar
            _statusBar = new StatusBar();
            this.Controls.Add(_statusBar);

            this.Load += (s, e) => 
            {
                LoadAppSettings();
                _inputPanel.SetGoalModeEnabled(!_appSettings.UltraLiteMode);
                _inputPanel.SetShowGenerationSpeed(_appSettings.ShowGenerationSpeed);
                ThemeColors.SetDarkMode(_appSettings.DarkMode);
                UpdateTheme();
                LoadSavedModelsFolder();
                StartPythonBackend();
            };

            this.Shown += async (s, e) =>
            {
                if (_appSettings.AutoUpdateEnabled)
                    await CheckForUpdatesAsync(userInitiated: false);
            };
        }

        private void UpdateTheme()
        {
            ThemeColors.ApplyToControlTree(this);
            this.BackColor = ThemeColors.Background;
            this.ForeColor = ThemeColors.TextPrimary;
            _menuStrip.UpdateTheme();
            _statusBar.UpdateTheme();
            _chatPanel.UpdateTheme();
            _inputPanel.UpdateTheme();
            _sidebarPanel.UpdateTheme();
            if (_imageGeneratorForm != null && !_imageGeneratorForm.IsDisposed)
                ThemeColors.ApplyToControlTree(_imageGeneratorForm);
            if (_forumForm != null && !_forumForm.IsDisposed)
                ThemeColors.ApplyToControlTree(_forumForm);
            if (_subagentActivityForm != null && !_subagentActivityForm.IsDisposed)
                ThemeColors.ApplyToControlTree(_subagentActivityForm);
        }

        private void LoadSavedModelsFolder()
        {
            var configFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalAIApp", "config.json");
            
            
            try
            {
                if (File.Exists(configFile))
                {
                    var json = File.ReadAllText(configFile);
                    var config = JsonConvert.DeserializeObject<AppConfig>(json);
                    if (!string.IsNullOrEmpty(config?.ModelsFolder) && Directory.Exists(config.ModelsFolder))
                    {
                        _modelsFolder = config.ModelsFolder;
                        _sidebarPanel.SetModelsFolder(_modelsFolder);
                    }
                }
                else
                {
                }
            }
            catch (Exception ex)
            {
            }

            if (!Directory.Exists(_modelsFolder) &&
                File.Exists(_appSettings.DefaultTextModelPath) &&
                string.Equals(Path.GetExtension(_appSettings.DefaultTextModelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            {
                _modelsFolder = Path.GetDirectoryName(_appSettings.DefaultTextModelPath) ?? "";
                if (Directory.Exists(_modelsFolder))
                {
                    _sidebarPanel.SetModelsFolder(_modelsFolder);
                }
            }

            // The quick scan also checks common local-model folders when no
            // custom model directory has been selected.
            ScanModelsFolder();
        }

        private void SaveModelsFolder()
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalAIApp");
            Directory.CreateDirectory(configDir);
            
            var configFile = Path.Combine(configDir, "config.json");
            var config = new AppConfig { ModelsFolder = _modelsFolder };
            File.WriteAllText(configFile, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private void LoadAppSettings()
        {
            var settingsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalAIApp", "settings.json");
            
            try
            {
                if (File.Exists(settingsFile))
                {
                    var json = File.ReadAllText(settingsFile);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        var migrateMemorySettings = !settings.MemoryOptionsInitialized;
                        if (migrateMemorySettings)
                        {
                            // The previous release enabled text mmap for every user by default.
                            // Treat that old implicit value as opt-out during the first upgrade.
                            settings.UseMmapForModel = false;
                            settings.MemoryOptionsInitialized = true;
                        }
                        // Upgrade the previous built-in system prompt to the new empty default.
                        if (string.Equals(settings.SystemPrompt,
                            "Tu es un assistant IA utile et amical. Reponds en francais de maniere claire et concise.",
                            StringComparison.Ordinal))
                            settings.SystemPrompt = "";
                        _appSettings = settings;
                        if (migrateMemorySettings)
                            SaveAppSettings();
                    }
                }
            }
            catch { }
        }

        private void SaveAppSettings()
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalAIApp");
            Directory.CreateDirectory(configDir);
            
            var settingsFile = Path.Combine(configDir, "settings.json");
            File.WriteAllText(settingsFile, JsonConvert.SerializeObject(_appSettings, Formatting.Indented));
        }

        private void OnSetModelsFolder(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Selectionnez le dossier contenant vos modeles IA",
                ShowNewFolderButton = true,
                SelectedPath = _modelsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseDescriptionForTitle = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _modelsFolder = dialog.SelectedPath;
                    _sidebarPanel.SetModelsFolder(_modelsFolder);
                    SaveModelsFolder();
                    ScanModelsFolder();
                }
            }
        }

        private void OnModelsFolderChanged(object sender, string folder)
        {
            _modelsFolder = folder;
            SaveModelsFolder();
            ScanModelsFolder();
        }

        private void ScanModelsFolder(bool forceFullScan = false, bool forceQuickScan = false)
        {
            var modelFolder = _modelsFolder ?? "";
            var searchAllDrives = forceFullScan || (!forceQuickScan && _appSettings.SearchAllDrivesForModels);
            var defaultTextModelPath = _appSettings.DefaultTextModelPath ?? "";
            var scanCancellation = new System.Threading.CancellationTokenSource();
            var previousScan = System.Threading.Interlocked.Exchange(ref _modelsScanCancellation, scanCancellation);
            previousScan?.Cancel();
            var cancellationToken = scanCancellation.Token;

            _sidebarPanel.SetModelsLoading(true);
            _statusBar.SetStatus(
                searchAllDrives
                    ? "Recherche complète des modèles depuis les racines des lecteurs fixes…"
                    : "Recherche rapide dans le dossier choisi et les emplacements connus…",
                StatusType.Info);

            Task.Run(() =>
            {
                try
                {
                    var models = new List<LocalModelInfo>();
                    var skippedMmprojCount = 0;

                    if (Directory.Exists(modelFolder))
                    {
                        try
                        {
                            var rootModel = DetectModel(modelFolder);
                            if (rootModel != null)
                                models.Add(rootModel);
                        }
                        catch { }

                        string[] subdirectories;
                        try { subdirectories = Directory.GetDirectories(modelFolder); }
                        catch { subdirectories = Array.Empty<string>(); }

                        foreach (var directory in subdirectories)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                var modelInfo = DetectModel(directory);
                                if (modelInfo != null)
                                    models.Add(modelInfo);
                            }
                            catch { }
                        }
                    }

                    var searchRoots = GetModelSearchRoots(modelFolder, searchAllDrives);
                    var modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in EnumerateGgufModels(searchRoots, cancellationToken, visitedDirectories =>
                    {
                        if (!searchAllDrives || IsDisposed || !IsHandleCreated)
                            return;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (ReferenceEquals(_modelsScanCancellation, scanCancellation))
                                    _statusBar.SetStatus(
                                        $"Recherche complète en cours… {visitedDirectories:N0} dossiers parcourus.",
                                        StatusType.Info);
                            }));
                        }
                        catch { }
                    }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var fullPath = Path.GetFullPath(file);
                            if (!modelPaths.Add(fullPath))
                                continue;
                            if (IsMmprojFile(fullPath))
                            {
                                skippedMmprojCount++;
                                continue;
                            }

                            models.Add(new LocalModelInfo
                            {
                                Name = Path.GetFileNameWithoutExtension(fullPath),
                                Path = fullPath,
                                Type = ModelType.GGUF,
                                Size = new FileInfo(fullPath).Length,
                                Folder = Path.GetDirectoryName(fullPath)
                            });
                        }
                        catch { }
                    }

                    if (File.Exists(defaultTextModelPath) &&
                        string.Equals(Path.GetExtension(defaultTextModelPath), ".gguf", StringComparison.OrdinalIgnoreCase) &&
                        !IsMmprojFile(defaultTextModelPath) &&
                        !models.Any(model => string.Equals(model.Path, defaultTextModelPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            models.Add(new LocalModelInfo
                            {
                                Name = Path.GetFileNameWithoutExtension(defaultTextModelPath),
                                Path = Path.GetFullPath(defaultTextModelPath),
                                Type = ModelType.GGUF,
                                Size = new FileInfo(defaultTextModelPath).Length,
                                Folder = Path.GetDirectoryName(defaultTextModelPath) ?? ""
                            });
                        }
                        catch { }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    models = models
                        .GroupBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();

                    if (IsDisposed || !IsHandleCreated)
                    {
                        System.Threading.Interlocked.CompareExchange(ref _modelsScanCancellation, null, scanCancellation);
                        scanCancellation.Dispose();
                        return;
                    }

                    BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(
                                System.Threading.Interlocked.CompareExchange(ref _modelsScanCancellation, null, scanCancellation),
                                scanCancellation))
                        {
                            scanCancellation.Dispose();
                            return;
                        }

                        try
                        {
                            _discoveredModelPaths = models
                                .Where(model => model.Type == ModelType.GGUF)
                                .Select(model => model.Path)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();

                            var preferredModel = models.FirstOrDefault(model =>
                                    model.Type == ModelType.GGUF &&
                                    string.Equals(model.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase))
                                ?? models.FirstOrDefault(model =>
                                    model.Type == ModelType.GGUF &&
                                    string.Equals(model.Path, defaultTextModelPath, StringComparison.OrdinalIgnoreCase))
                                ?? models
                                    .Where(model => model.Type == ModelType.GGUF)
                                    .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                                    .FirstOrDefault();

                            if (preferredModel == null && !_isGenerating && !_imageGenerationActive)
                            {
                                if (!string.IsNullOrWhiteSpace(_selectedModelPath))
                                    RequestCurrentModelUnload();
                                _selectedModelPath = "";
                                _selectedModelName = "";
                                _inputPanel.SetModelSelected(false);
                            }

                            _sidebarPanel.LoadModels(models, preferredModel?.Path);
                            _sidebarPanel.SetModelsLoading(false);
                            var scanLabel = searchAllDrives ? "après la recherche complète" : "dans les emplacements connus";
                            _statusBar.SetStatus(
                                $"{models.Count} modèle(s) trouvé(s) {scanLabel}",
                                models.Count > 0 ? StatusType.Success : StatusType.Warning);

                            if (skippedMmprojCount > 0)
                            {
                                _chatPanel.AddSystemMessage(
                                    $"{skippedMmprojCount} projecteur(s) mmproj détecté(s). Ils ne sont pas sélectionnables seuls et seront associés automatiquement au modèle GGUF du même dossier lorsqu'une correspondance unique est trouvée.",
                                    MessageType.Info);
                            }
                        }
                        finally
                        {
                            scanCancellation.Dispose();
                        }
                    }));
                }
                catch (OperationCanceledException)
                {
                    var wasCurrentScan = ReferenceEquals(
                        System.Threading.Interlocked.CompareExchange(ref _modelsScanCancellation, null, scanCancellation),
                        scanCancellation);
                    if (wasCurrentScan && !IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _sidebarPanel.SetModelsLoading(false);
                                _statusBar.SetStatus("Recherche des modèles annulée.", StatusType.Warning);
                            }));
                        }
                        catch { }
                    }
                    scanCancellation.Dispose();
                }
                catch (Exception exception)
                {
                    var wasCurrentScan = ReferenceEquals(
                        System.Threading.Interlocked.CompareExchange(ref _modelsScanCancellation, null, scanCancellation),
                        scanCancellation);
                    if (wasCurrentScan && !IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _sidebarPanel.SetModelsLoading(false);
                                _statusBar.SetStatus("Recherche des modèles impossible : " + exception.Message, StatusType.Error);
                            }));
                        }
                        catch { }
                    }
                    scanCancellation.Dispose();
                }
            });
        }

        private void CancelModelScan()
        {
            var activeScan = System.Threading.Interlocked.Exchange(ref _modelsScanCancellation, null);
            if (activeScan == null)
            {
                _statusBar.SetStatus("Aucune recherche de modèles n'est en cours.", StatusType.Info);
                return;
            }

            activeScan.Cancel();
            _sidebarPanel.SetModelsLoading(false);
            _statusBar.SetStatus("Annulation de la recherche des modèles…", StatusType.Warning);
        }

        private static List<string> GetModelSearchRoots(string selectedFolder, bool searchAllDrives)
        {
            var roots = new List<string>();
            var normalizedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (Directory.Exists(fullPath) && normalizedRoots.Add(fullPath))
                        roots.Add(fullPath);
                }
                catch { }
            }

            AddRoot(selectedFolder);

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            AddRoot(Path.Combine(userProfile, ".lmstudio", "models"));
            AddRoot(Path.Combine(userProfile, "LM Studio", "models"));
            AddRoot(Path.Combine(localAppData, "LM Studio", "models"));
            var janRoot = Path.Combine(roamingAppData, "Jan");
            AddRoot(Path.Combine(janRoot, "data", "llamacpp", "models"));
            AddRoot(Path.Combine(janRoot, "data", "models"));
            AddRoot(Path.Combine(localAppData, "Jan", "data", "llamacpp", "models"));
            AddRoot(Path.Combine(localAppData, "Jan", "data", "models"));
            try
            {
                var janSettingsFile = Path.Combine(janRoot, "settings.json");
                if (File.Exists(janSettingsFile))
                {
                    var janSettings = JObject.Parse(File.ReadAllText(janSettingsFile));
                    var janDataFolder = (string?)janSettings["data_folder"];
                    if (!string.IsNullOrWhiteSpace(janDataFolder))
                    {
                        AddRoot(Path.Combine(janDataFolder, "llamacpp", "models"));
                        AddRoot(Path.Combine(janDataFolder, "models"));
                    }
                }
            }
            catch { }
            AddRoot(Path.Combine(localAppData, "nomic.ai", "GPT4All"));
            AddRoot(Path.Combine(userProfile, "text-generation-webui", "models"));
            AddRoot(Path.Combine(userProfile, "pinokio", "api", "text-generation-webui.git", "models"));
            AddRoot(Path.Combine(userProfile, "pinokio", "api", "koboldcpp.git", "models"));
            AddRoot(Path.Combine(userProfile, "Models"));
            AddRoot(Path.Combine(documents, "LocalAIModels"));

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady)
                            continue;

                        if (drive.DriveType == DriveType.Fixed)
                            AddRoot(Path.Combine(drive.RootDirectory.FullName, "models"));

                        if (searchAllDrives && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                            AddRoot(drive.RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch { }

            return roots;
        }

        private static IEnumerable<string> EnumerateGgufModels(
            IEnumerable<string> roots,
            System.Threading.CancellationToken cancellationToken,
            Action<int>? progress = null)
        {
            var pendingDirectories = new Stack<string>(roots.Reverse());
            var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoriesVisited = 0;

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pendingDirectories.Pop();
                string fullDirectory;
                try { fullDirectory = Path.GetFullPath(directory); }
                catch { continue; }
                if (!visitedDirectories.Add(fullDirectory))
                    continue;

                string[] modelFiles;
                try { modelFiles = Directory.GetFiles(fullDirectory, "*.gguf", SearchOption.TopDirectoryOnly); }
                catch { modelFiles = Array.Empty<string>(); }
                foreach (var file in modelFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return file;
                }

                string[] children;
                try { children = Directory.GetDirectories(fullDirectory, "*", SearchOption.TopDirectoryOnly); }
                catch { children = Array.Empty<string>(); }
                foreach (var child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pendingDirectories.Push(child);
                    }
                    catch { }
                }

                directoriesVisited++;
                if (directoriesVisited % 500 == 0)
                    progress?.Invoke(directoriesVisited);
            }
        }
        private LocalModelInfo DetectModel(string folder)
        {
            try
            {
                var name = Path.GetFileName(folder);
                var files = Directory.GetFiles(folder);

                var ggufInFolder = Directory.GetFiles(folder, "*.gguf")
                    .Where(file => !IsMmprojFile(file))
                    .ToArray();
                if (ggufInFolder.Length > 0)
                {
                    var largest = ggufInFolder.OrderByDescending(f => new FileInfo(f).Length).First();
                    return new LocalModelInfo
                    {
                        Name = Path.GetFileNameWithoutExtension(largest),
                        Path = largest,
                        Type = ModelType.GGUF,
                        Size = new FileInfo(largest).Length,
                        Folder = folder
                    };
                }
                
                if (files.Any(f => f.EndsWith("config.json", StringComparison.OrdinalIgnoreCase)))
                {
                    string modelType = "Transformers";
                    long size = 0;
                    
                    var modelFiles = files.Where(f => 
                        f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) || 
                        f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)).ToArray();
                    
                    foreach (var mf in modelFiles)
                        size += new FileInfo(mf).Length;
                    
                    try
                    {
                        var configJson = File.ReadAllText(files.First(f => f.EndsWith("config.json", StringComparison.OrdinalIgnoreCase)));
                        if (configJson.Contains("llama", StringComparison.OrdinalIgnoreCase)) modelType = "LLaMA";
                        else if (configJson.Contains("gpt2", StringComparison.OrdinalIgnoreCase)) modelType = "GPT-2";
                        else if (configJson.Contains("bloom", StringComparison.OrdinalIgnoreCase)) modelType = "BLOOM";
                        else if (configJson.Contains("opt", StringComparison.OrdinalIgnoreCase)) modelType = "OPT";
                        else if (configJson.Contains("gpt_neox", StringComparison.OrdinalIgnoreCase)) modelType = "GPT-NeoX";
                        else if (configJson.Contains("mpt", StringComparison.OrdinalIgnoreCase)) modelType = "MPT";
                        else if (configJson.Contains("falcon", StringComparison.OrdinalIgnoreCase)) modelType = "Falcon";
                        else if (configJson.Contains("mistral", StringComparison.OrdinalIgnoreCase)) modelType = "Mistral";
                        else if (configJson.Contains("phi", StringComparison.OrdinalIgnoreCase)) modelType = "Phi";
                        else if (configJson.Contains("gemma", StringComparison.OrdinalIgnoreCase)) modelType = "Gemma";
                        else if (configJson.Contains("qwen", StringComparison.OrdinalIgnoreCase)) modelType = "Qwen";
                    }
                    catch { }
                    
                    return new LocalModelInfo
                    {
                        Name = name,
                        Path = folder,
                        Type = ModelType.Transformers,
                        ModelSubType = modelType,
                        Size = size,
                        Folder = folder
                    };
                }
                
                return null;
            }
            catch { return null; }
        }

        private static bool IsMmprojFile(string path)
        {
            return Path.GetFileNameWithoutExtension(path)
                .Contains("mmproj", StringComparison.OrdinalIgnoreCase);
        }

        private void OnModelSelected(object sender, string modelPath)
        {
            if (_imageGenerationActive)
            {
                _chatPanel.AddSystemMessage("Attendez la fin de la génération d'image avant de changer de modèle.", MessageType.Info);
                if (!string.IsNullOrEmpty(_selectedModelPath))
                    _sidebarPanel.SelectModel(_selectedModelPath);
                return;
            }

            if (_isGenerating)
            {
                _chatPanel.AddSystemMessage("Annulez la generation avant de changer de modele.", MessageType.Info);
                if (!string.IsNullOrEmpty(_selectedModelPath))
                    _sidebarPanel.SelectModel(_selectedModelPath);
                return;
            }

            if (IsMmprojFile(modelPath))
            {
                _statusBar.SetStatus("Choisissez le modèle GGUF principal", StatusType.Warning);
                _chatPanel.AddSystemMessage(
                    "Un fichier mmproj est un projecteur multimodal auxiliaire et ne se charge pas seul. Choisissez le GGUF principal correspondant : l’application n’associe un projecteur nommé que si son nom identifie clairement ce modèle.",
                    MessageType.Warning);
                if (!string.IsNullOrEmpty(_selectedModelPath))
                    _sidebarPanel.SelectModel(_selectedModelPath);
                return;
            }

            if (!string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase))
            {
                _statusBar.SetStatus("Ce backend accepte uniquement les modeles GGUF", StatusType.Warning);
                _chatPanel.AddSystemMessage("Selectionnez un fichier .gguf. Les dossiers Transformers ne sont pas pris en charge par ce backend.", MessageType.Warning);
                if (!string.IsNullOrEmpty(_selectedModelPath))
                    _sidebarPanel.SelectModel(_selectedModelPath);
                return;
            }

            var modelChanged = !string.Equals(_selectedModelPath, modelPath, StringComparison.OrdinalIgnoreCase);
            if (modelChanged && !string.IsNullOrEmpty(_selectedModelPath))
                RequestCurrentModelUnload();

            if (modelChanged)
            {
                CancelMessageEdit(restoreDraft: true);
                // Save the old conversation with its old model, then start a
                // clean history/file for the newly selected model.
                if (_currentConversation.Count > 0)
                    SaveConversation();

                _currentConversation.Clear();
                ResetCurrentConversationTitle();
                _currentConversationFile = CreateNewConversationPath();
                _chatPanel.Clear();
                _sidebarPanel.SelectConversation(null);
            }
            
            _selectedModelPath = modelPath;
            var modelName = GetModelDisplayName(modelPath);
            _selectedModelName = modelName;
            _chatPanel.SetModelName(modelName);
            
            _statusBar.SetStatus("Modele selectionne: " + modelName, StatusType.Info);
            _inputPanel.SetModelSelected(true, modelName);
            RequestModelCapabilities(modelPath);
        }

        private static string GetModelDisplayName(string modelPath)
        {
            var fileName = Path.GetFileName(modelPath);
            return string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(modelPath)
                : fileName;
        }

        private void RequestCurrentModelUnload()
        {
            if (!_isPythonReady)
                return;

            try
            {
                lock (_pythonInputLock)
                {
                    if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                        return;

                    _pythonInput.WriteLine(JsonConvert.SerializeObject(new { type = "unload_model" }));
                    _pythonInput.Flush();
                }

                Log("Demande de déchargement du modèle après changement de sélection");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Unload model] " + ex.Message);
            }
        }

        private void RequestModelCapabilities(string modelPath)
        {
            if (!_isPythonReady || string.IsNullOrWhiteSpace(modelPath))
                return;

            var json = JsonConvert.SerializeObject(new
            {
                type = "check_model_capabilities",
                model_path = modelPath,
                default_omni_model_path = _appSettings.UltraLiteMode ? "" : _appSettings.DefaultOmniModelPath
            });
            Task.Run(() =>
            {
                try
                {
                    lock (_pythonInputLock)
                    {
                        if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                            return;
                        _pythonInput.WriteLine(json);
                        _pythonInput.Flush();
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine("[Model capabilities] " + exception.Message);
                }
            });
        }

        private void OnRefreshModels(object sender, EventArgs e)
        {
            ScanModelsFolder();
        }

        private void StartPythonBackend()
        {
            if (_isStartingBackend || (_pythonProcess != null && !_pythonProcess.HasExited))
                return;

            _isStartingBackend = true;
            _statusBar.SetStatus("Demarrage du backend Python...", StatusType.Info);
            
            Task.Run(() =>
            {
                try
                {
                    var pythonPath = FindPython();
                    if (string.IsNullOrEmpty(pythonPath))
                    {
                        Invoke(() => {
                            _statusBar.SetStatus("Python non trouve - Installez Python 3.10+", StatusType.Error);
                        });
                        return;
                    }

                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var scriptPath = Path.Combine(baseDir, "PythonBackend", "ai_backend.py");
                    
                    if (!File.Exists(scriptPath))
                    {
                        var altPaths = new[]
                        {
                            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "PythonBackend", "ai_backend.py")),
                        };
                        foreach (var p in altPaths)
                        {
                            if (File.Exists(p)) { scriptPath = p; break; }
                        }
                    }

                    if (!File.Exists(scriptPath))
                    {
                        Invoke(() => {
                            _statusBar.SetStatus("Script non trouve: " + scriptPath, StatusType.Error);
                        });
                        return;
                    }

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = pythonPath,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(scriptPath),
                            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        },
                        EnableRaisingEvents = true
                    };
                    process.StartInfo.ArgumentList.Add(scriptPath);
                    process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
                    process.StartInfo.Environment["PYTHONUTF8"] = "1";
                    _pythonProcess = process;

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null) HandlePythonOutput(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            Log("[Python stderr] " + e.Data);
                        }
                    };
                    process.Exited += (s, e) =>
                    {
                        var exitCode = process.ExitCode;
                        System.Diagnostics.Debug.WriteLine($"[Python Exited] ExitCode={exitCode}");
                        if (!ReferenceEquals(_pythonProcess, process)) return;
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() => 
                        {
                            if (!ReferenceEquals(_pythonProcess, process)) return;
                            _statusBar.SetStatus($"Backend Python arrete (code {exitCode})", StatusType.Warning);
                            if (_isGenerating)
                            {
                                _generationWatchdog?.Dispose();
                                _streamRenderTimer.Stop();
                                ClearPendingStreamingTokens();
                                _isGenerating = false;
                                _isStreamingResponse = false;
                                _isCancellingGeneration = false;
                                _inputPanel.SetGenerating(false);
                                _chatPanel.AddSystemMessage($"[ERREUR] Backend Python arrete inopinement (code {exitCode})", MessageType.Error);
                            }
                            _isPythonReady = false;
                            ClearPendingConversationTitleRequests();
                            _inputPanel.SetReady(false);
                            if (_imageGenerationActive)
                            {
                                _imageGenerationActive = false;
                                _imageGeneratorForm?.SetCompleted(new List<string>(), "Le backend Python s'est arrêté.", false);
                            }
                            ApplyPendingUpdateWhenIdle();
                        }));
                    };

                    process.Start();
                    System.Diagnostics.Debug.WriteLine($"[Python Process] Started PID={process.Id}");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    _pythonInput = process.StandardInput;
                    // Note: StandardOutput is read asynchronously via OutputDataReceived event
                    // Do NOT access StandardOutput property when using BeginOutputReadLine()

                    Invoke(() => _statusBar.SetStatus("Backend demarre, en attente de modele...", StatusType.Info));
                }
                catch (Exception ex)
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() => _statusBar.SetStatus("Erreur: " + ex.Message, StatusType.Error)));
                }
                finally
                {
                    _isStartingBackend = false;
                }
            });
        }

        private string FindPython()
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDirectory, "PythonRuntime", "python.exe"),
                "python.exe", "python3.exe",
                @"C:\Python312\python.exe", @"C:\Python311\python.exe", @"C:\Python310\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python310", "python.exe"),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
                try
                {
                    var psi = new ProcessStartInfo(c, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using var process = Process.Start(psi);
                    if (process == null)
                        continue;
                    if (process.WaitForExit(1000) && process.ExitCode == 0)
                        return c;
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(1000);
                    }
                }
                catch { }
            }
            return null;
        }

        private void HandlePythonOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Token output is high-frequency. Buffer it off the UI thread and
            // render at a fixed cadence instead of synchronously invoking the UI
            // once for every fragment from llama.cpp.
            try
            {
                var tokenMessage = JsonConvert.DeserializeObject<PythonMessage>(line);
                if (tokenMessage?.Type == "generation_token")
                {
                    RecordGenerationToken();
                    return;
                }
                if (tokenMessage?.Type == "stream_token")
                {
                    if (!string.IsNullOrEmpty(tokenMessage.Content))
                        QueueStreamingToken(tokenMessage.Content);
                    return;
                }
            }
            catch
            {
                // Let the normal UI path report malformed or unexpected output.
            }

            if (IsDisposed || Disposing || !IsHandleCreated)
                return;

            try
            {
                Invoke(() =>
                {
                    try
                    {
                        var msg = JsonConvert.DeserializeObject<PythonMessage>(line);
                        if (msg == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[C# Parse Failed] Null msg from: {line.Substring(0, Math.Min(100, line.Length))}");
                            return;
                        }

                        System.Diagnostics.Debug.WriteLine($"[C# Received] Type={msg.Type}, ContentLen={msg.Content?.Length ?? 0}");

                        switch (msg.Type)
                        {
                        case "ready":
                            _isPythonReady = true;
                            _statusBar.SetStatus("Pret - " + msg.Model, StatusType.Success);
                            _inputPanel.SetReady(true);
                            if (!string.IsNullOrWhiteSpace(_selectedModelPath))
                                RequestModelCapabilities(_selectedModelPath);
                            break;
                        case "response":
                            _generationWatchdog?.Dispose();
                            _streamRenderTimer.Stop();
                            _isGenerating = false;
                            _isStreamingResponse = false;
                            _isCancellingGeneration = false;
                            _generationTimedOut = false;
                            _inputPanel.SetGenerating(false);
                            var responseContent = msg.Content ?? "";
                            var responseMedia = ImportGeneratedMedia(msg.Media);
                            _chatPanel.AddMessage("Assistant", responseContent, false, attachments: responseMedia);
                            _currentConversation.Add(new Message
                            {
                                Role = "assistant",
                                Content = responseContent,
                                Attachments = responseMedia,
                                Timestamp = DateTime.Now
                            });
                            SaveConversation();
                            _statusBar.SetStatus("Pret", StatusType.Success);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "generation_start":
                            MarkGenerationProgress();
                            _isStreamingResponse = true;
                            _streamRenderTimer.Start();
                            break;
                        case "generation_pause":
                            PauseGenerationSpeed();
                            break;
                        case "generation_resume":
                            ResumeGenerationSpeed();
                            break;
                        case "subagent_forum_clear_request":
                        {
                            var decision = MessageBox.Show(
                                this,
                                "Le modèle veut lancer un sous-agent. Pour commencer cette nouvelle série de rapports, " +
                                "le forum temporaire doit être vidé. Les rapports précédents seront supprimés. Autoriser le vidage ?",
                                "Vider le forum temporaire des sous-agents",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2);
                            AcknowledgeSubagentForumClear(
                                msg.RequestId,
                                decision == DialogResult.Yes);
                            break;
                        }
                        case "tool_status":
                        {
                            MarkGenerationProgress();
                            var toolStatus = msg.Content ?? "Utilisation d'un outil...";
                            _statusBar.SetStatus(toolStatus, StatusType.Info);
                            _chatPanel.AddToolStatusMessage(toolStatus);
                            _chatPanel.AppendToolStatusToThinking(toolStatus);
                            break;
                        }
                        case "subagent_activity":
                            if (string.Equals(msg.ActivityKind, "start", StringComparison.Ordinal))
                                ShowSubagentActivity(activate: false);
                            _subagentActivityForm?.AppendActivity(
                                msg.ActivitySessionId,
                                msg.ActivityId,
                                msg.Model,
                                msg.ActivityKind,
                                msg.Content ?? "");
                            break;
                        case "thinking_token":
                            MarkGenerationProgress();
                            if (!string.IsNullOrEmpty(msg.Content))
                            {
                                if (string.Equals(msg.Source, "conversation_title", StringComparison.Ordinal))
                                    _chatPanel.ShowThinkingActivity();
                                else
                                {
                                    _chatPanel.AppendThinkingPreviewToken(msg.Content);
                                }
                            }
                            break;
                        case "thinking_clear":
                            _chatPanel.ClearThinkingPreview();
                            break;
                        case "stream_start":
                            System.Diagnostics.Debug.WriteLine($"[C# stream_start] Received");
                            MarkGenerationProgress();
                            ClearPendingStreamingTokens();
                            _isStreamingResponse = true;
                            _receivedFirstToken = false;
                            _chatPanel.StartStreamingMessage();
                            _streamRenderTimer.Start();
                            break;
                        case "stream_token":
                            MarkGenerationProgress();
                            if (!string.IsNullOrEmpty(msg.Content))
                            {
                                QueueStreamingToken(msg.Content);
                            }
                            break;
                        case "stream_end":
                            System.Diagnostics.Debug.WriteLine($"[C# stream_end] Before EndStreamingMessage, isGenerating={_isGenerating}");
                            var generationTimedOut = _generationTimedOut;
                            var streamError = msg.Error;
                            if (!string.IsNullOrWhiteSpace(_pendingBackendError) &&
                                (string.IsNullOrWhiteSpace(streamError) ||
                                 streamError.Contains("Impossible de charger le modele", StringComparison.OrdinalIgnoreCase) ||
                                 streamError.Contains("Impossible de charger le modèle", StringComparison.OrdinalIgnoreCase)))
                            {
                                streamError = _pendingBackendError;
                            }
                            _pendingBackendError = "";
                            _generationWatchdog?.Dispose();
                            _streamRenderTimer.Stop();
                            try
                            {
                                FlushPendingStreamingTokens();
                            }
                            catch (Exception flushException)
                            {
                                ClearPendingStreamingTokens();
                                LogException("Affichage des derniers jetons", flushException);
                            }

                            // A stream_end is terminal even if WebView rendering fails.
                            // Unlock the composer before finalizing the visual message.
                            _isGenerating = false;
                            _isStreamingResponse = false;
                            _isCancellingGeneration = false;
                            _generationTimedOut = false;
                            _inputPanel.SetGenerating(false);
                            Log("stream_end received; send controls restored");
                            try {
                                Log("finalizing streamed message");
                                _chatPanel.EndStreamingMessage(msg.Content ?? "");
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end] EndStreamingMessage done");
                                Log("stream message finalized");
                                var streamMedia = ImportGeneratedMedia(msg.Media);
                                if (!string.IsNullOrWhiteSpace(msg.Content) || streamMedia.Count > 0)
                                {
                                    _currentConversation.Add(new Message
                                    {
                                        Role = "assistant",
                                        Content = msg.Content ?? "",
                                        Attachments = streamMedia,
                                        Timestamp = DateTime.Now
                                    });
                                    _chatPanel.RenderMessages(_currentConversation);
                                    SaveConversation();
                                }
                                if (!string.IsNullOrWhiteSpace(streamError))
                                {
                                    var displayedError = AddApplicationLogLocation(streamError);
                                    Log("[GENERATION ERROR] " + streamError);
                                    _statusBar.SetStatus("Erreur: " + streamError, StatusType.Error);
                                    _chatPanel.AddSystemMessage(displayedError, MessageType.Error);
                                }
                                else if (generationTimedOut)
                                {
                                    _statusBar.SetStatus("Generation arretee (delai depasse)", StatusType.Error);
                                }
                                else if (msg.Cancelled)
                                {
                                    _statusBar.SetStatus("Generation arretee", StatusType.Warning);
                                }
                                else
                                {
                                    _statusBar.SetStatus("Pret", StatusType.Success);
                                }
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end] Flags reset, isGenerating={_isGenerating}");
                            } catch (Exception ex) {
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end ERROR] {ex.Message}\n{ex.StackTrace}");
                                _statusBar.SetStatus("Erreur stream_end", StatusType.Error);
                            }
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "conversation_title_start":
                            MarkGenerationProgress();
                            _chatPanel.ShowThinkingActivity();
                            StartStreamingConversationTitle(msg);
                            break;
                        case "conversation_title_token":
                            MarkGenerationProgress();
                            AppendStreamingConversationTitle(msg);
                            break;
                        case "conversation_title":
                            MarkGenerationProgress();
                            ApplyGeneratedConversationTitle(msg);
                            break;
                        case "error":
                            var backendError = string.IsNullOrWhiteSpace(msg.Content)
                                ? "Erreur inconnue du backend."
                                : msg.Content;
                            Log("[BACKEND ERROR] " + backendError);
                            var isPendingGenerationError = _isGenerating && !_isStreamingResponse;
                            if (isPendingGenerationError)
                                _pendingBackendError = backendError;
                            else
                            {
                                _pendingBackendError = "";
                                _chatPanel.AddSystemMessage(AddApplicationLogLocation(backendError), MessageType.Error);
                            }
                            if (!_isStreamingResponse)
                            {
                                _generationWatchdog?.Dispose();
                                _streamRenderTimer.Stop();
                                _isGenerating = false;
                                _isCancellingGeneration = false;
                                _generationTimedOut = false;
                                _inputPanel.SetGenerating(false);
                            }
                            _statusBar.SetStatus("Erreur: " + backendError, StatusType.Error);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "model_loading":
                            MarkGenerationProgress();
                            _statusBar.SetStatus("Chargement: " + msg.Content, StatusType.Info);
                            break;
                        case "model_loaded":
                            _pendingBackendError = "";
                            if (string.IsNullOrWhiteSpace(msg.ModelPath) ||
                                string.Equals(Path.GetFullPath(msg.ModelPath), Path.GetFullPath(_selectedModelPath), StringComparison.OrdinalIgnoreCase))
                            {
                                _inputPanel.SetMultimodalSupport(msg.SupportsMultimodal, msg.CanAnalyzeImagesWithOmni);
                                _statusBar.SetStatus("Modele charge: " + msg.Content, StatusType.Success);
                            }
                            break;
                        case "model_capabilities":
                            if (!string.IsNullOrWhiteSpace(msg.ModelPath) &&
                                string.Equals(Path.GetFullPath(msg.ModelPath), Path.GetFullPath(_selectedModelPath), StringComparison.OrdinalIgnoreCase))
                                _inputPanel.SetMultimodalSupport(msg.SupportsMultimodal, msg.CanAnalyzeImagesWithOmni);
                            break;
                        case "image_engine_status":
                            MarkGenerationProgress();
                            _imageGeneratorForm?.SetStatus(msg.Content ?? "Préparation du moteur d'images…");
                            _subagentActivityForm?.AppendImageActivity("status", msg.Content ?? "Préparation du moteur d'images…");
                            break;
                        case "image_engine_progress":
                            _imageGeneratorForm?.SetDownloadProgress(msg.Downloaded ?? 0, msg.Total ?? 0);
                            _subagentActivityForm?.AppendImageActivity(
                                "engine_progress", downloaded: msg.Downloaded, downloadTotal: msg.Total);
                            break;
                        case "image_catalog":
                            _imageGeneratorForm?.SetCatalog(
                                msg.Recipes?.ToObject<List<ImageRecipeInfo>>() ?? new List<ImageRecipeInfo>(),
                                msg.Files?.ToObject<List<ImageModelFileInfo>>() ?? new List<ImageModelFileInfo>(),
                                msg.Errors?.ToObject<List<ImageCatalogError>>() ?? new List<ImageCatalogError>());
                            break;
                        case "image_start":
                        case "image_loading":
                            MarkGenerationProgress();
                            ShowSubagentActivity(activate: false);
                            _imageGenerationActive = true;
                            _imageGeneratorForm?.SetGenerating(true);
                            var imageStartText = msg.Content ?? "Chargement du modèle d'images…";
                            _imageGeneratorForm?.SetStatus(imageStartText);
                            _subagentActivityForm?.AppendImageActivity("start", imageStartText);
                            break;
                        case "image_progress":
                            MarkGenerationProgress();
                            _imageGeneratorForm?.SetProgress(msg.Stage, msg.Detail, msg.Completed, msg.Total, msg.Pass);
                            _subagentActivityForm?.AppendImageActivity(
                                "progress",
                                string.Join(" — ", new[] { msg.Stage, msg.Detail }.Where(value => !string.IsNullOrWhiteSpace(value))),
                                msg.Completed, msg.Total, msg.Pass);
                            break;
                        case "image_preview":
                            if (!string.IsNullOrEmpty(msg.Content))
                            {
                                _imageGeneratorForm?.SetPreview(msg.Content);
                                _subagentActivityForm?.AppendImageActivity("preview", msg.Content);
                            }
                            break;
                        case "image_complete":
                            _imageGenerationActive = false;
                            _subagentActivityForm?.AppendImageActivity(
                                msg.Error != null ? "error" : msg.Cancelled ? "cancelled" : "complete",
                                msg.Error ?? msg.Content ?? "");
                            _imageGeneratorForm?.SetCompleted(
                                msg.Paths?.ToObject<List<string>>() ?? new List<string>(),
                                msg.Error,
                                msg.Cancelled,
                                msg.Content);
                            _statusBar.SetStatus(msg.Error != null ? "Erreur génération image" : msg.Cancelled ? "Génération image annulée" : "Image générée", msg.Error != null ? StatusType.Error : msg.Cancelled ? StatusType.Warning : StatusType.Success);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "image_error":
                            _imageGenerationActive = false;
                            var imageError = msg.Content ?? "Erreur inconnue";
                            _subagentActivityForm?.AppendImageActivity("error", imageError);
                            _imageGeneratorForm?.SetCompleted(new List<string>(), imageError, false);
                            _statusBar.SetStatus("Erreur génération image", StatusType.Error);
                            ShowImageMemoryError(imageError);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "debug":
                            Log("Backend: " + msg.Content);
                            System.Diagnostics.Debug.WriteLine("[Python Debug] " + msg.Content);
                            break;
                        default:
                            System.Diagnostics.Debug.WriteLine($"[Python Unknown Type] {msg.Type}: {msg.Content}");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException("Traitement de la réponse Python", ex);
                        System.Diagnostics.Debug.WriteLine($"[C# JSON Parse Error] {ex.Message} | Line: {line.Substring(0, Math.Min(200, line.Length))}");
                        if (line.StartsWith("[DEBUG]"))
                            _chatPanel.AddSystemMessage(line, MessageType.Info);
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                // The backend can finish writing while the window is closing.
            }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // The UI handle can disappear between the checks and Invoke.
            }
        }

        private void RecordGenerationToken()
        {
            MarkGenerationProgress();
            if (!_appSettings.ShowGenerationSpeed)
                return;

            var timestamp = Stopwatch.GetTimestamp();
            System.Threading.Interlocked.Increment(ref _generationOutputTokenCount);
            System.Threading.Interlocked.CompareExchange(ref _generationFirstTokenTimestamp, timestamp, 0);
        }

        private void UpdateGenerationSpeedIndicator()
        {
            if (!_isGenerating || !_appSettings.ShowGenerationSpeed)
                return;

            var start = System.Threading.Interlocked.Read(ref _generationFirstTokenTimestamp);
            var tokenCount = System.Threading.Interlocked.Read(ref _generationOutputTokenCount);
            if (start <= 0 || tokenCount <= 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var lastUpdate = System.Threading.Interlocked.Read(ref _lastGenerationSpeedUpdateTimestamp);
            if (lastUpdate > 0 && now - lastUpdate < Stopwatch.Frequency / 2)
                return;
            System.Threading.Interlocked.Exchange(ref _lastGenerationSpeedUpdateTimestamp, now);

            var pausedAt = System.Threading.Interlocked.Read(ref _generationPausedTimestamp);
            var elapsedTicks = (pausedAt > 0 ? pausedAt : now) - start
                - System.Threading.Interlocked.Read(ref _generationPausedTicks);
            var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
            if (elapsedSeconds > 0)
                _inputPanel.SetGenerationSpeed(tokenCount / elapsedSeconds);
        }

        private void PauseGenerationSpeed()
        {
            if (!_appSettings.ShowGenerationSpeed)
                return;

            System.Threading.Interlocked.CompareExchange(
                ref _generationPausedTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        private void ResumeGenerationSpeed()
        {
            var pausedAt = System.Threading.Interlocked.Exchange(ref _generationPausedTimestamp, 0);
            if (pausedAt <= 0)
                return;

            var pausedTicks = Stopwatch.GetTimestamp() - pausedAt;
            if (pausedTicks > 0)
                System.Threading.Interlocked.Add(ref _generationPausedTicks, pausedTicks);
        }

        private void QueueStreamingToken(string token)
        {
            lock (_streamBufferLock)
                _pendingStreamTokens.Append(token);
        }

        private void FlushPendingStreamingTokens()
        {
            string batch;
            lock (_streamBufferLock)
            {
                if (_pendingStreamTokens.Length == 0)
                    return;

                batch = _pendingStreamTokens.ToString();
                _pendingStreamTokens.Clear();
            }

            if (!_isStreamingResponse || IsDisposed || Disposing)
                return;

            if (!_receivedFirstToken)
            {
                _receivedFirstToken = true;
                System.Diagnostics.Debug.WriteLine("[C# stream_token] FIRST token received");
            }

            _chatPanel.AppendStreamingToken(batch);
        }

        private void ClearPendingStreamingTokens()
        {
            lock (_streamBufferLock)
                _pendingStreamTokens.Clear();
        }

        private string[] GetAvailableLocalModelPaths()
        {
            var paths = new List<string>();
            paths.AddRange(_discoveredModelPaths);
            paths.Add(_selectedModelPath);
            paths.Add(_appSettings.DefaultTextModelPath);

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Where(path => string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
                .Where(path => !IsMmprojFile(path))
                .Select(path => Path.GetFullPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => string.Equals(path, _selectedModelPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();
        }

        private string[] GetAvailableSubagentModelPaths() =>
            GetAvailableLocalModelPaths().Take(64).ToArray();

        private void OnEditMessageRequested(int conversationIndex)
        {
            if (_isGenerating || _imageGenerationActive)
            {
                _chatPanel.AddSystemMessage("Attendez la fin de la génération avant de modifier un message.", MessageType.Info);
                return;
            }
            if (conversationIndex < 0 || conversationIndex >= _currentConversation.Count ||
                !string.Equals(_currentConversation[conversationIndex].Role, "user", StringComparison.OrdinalIgnoreCase))
                return;

            _editingMessageIndex = conversationIndex;
            _inputPanel.BeginMessageEdit(
                _currentConversation[conversationIndex].Content ?? "",
                _currentConversation[conversationIndex].Attachments);
            _statusBar.SetStatus("Modification du message : renvoyez-le pour reprendre le contexte à cet endroit.", StatusType.Info);
        }

        private void OnMessageEditCancelled(object sender, EventArgs e)
        {
            _editingMessageIndex = null;
            _statusBar.SetStatus("Modification annulée", StatusType.Info);
        }

        private void CancelMessageEdit(bool restoreDraft)
        {
            if (_editingMessageIndex == null)
                return;
            _editingMessageIndex = null;
            _inputPanel.EndMessageEdit(restoreDraft);
        }

        private void OnSendMessage(object sender, EventArgs e)
        {
            if (_imageGenerationActive)
            {
                _statusBar.SetStatus("Attendez la fin de la génération d'image avant de lancer le chat.", StatusType.Warning);
                return;
            }
            if (_isGenerating)
            {
                CancelGeneration(false);
                return;
            }
            if (!_isPythonReady) return;
            if (string.IsNullOrEmpty(_selectedModelPath))
            {
                _statusBar.SetStatus("Selectionnez d'abord un modele GGUF", StatusType.Warning);
                return;
            }
            
            var text = _inputPanel.GetInputText();
            var requestedGoalMode = InputPanel.HasGoalModePrefix(text);
            if (requestedGoalMode && _appSettings.UltraLiteMode)
            {
                _statusBar.SetStatus("Désactive le mode ultra léger pour utiliser goal:.", StatusType.Warning);
                return;
            }
            var goalMode = InputPanel.TryStripGoalModePrefix(ref text);
            var attachments = _inputPanel.GetAttachments();
            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            {
                if (goalMode)
                    _statusBar.SetStatus("Ajoute une instruction après goal:.", StatusType.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
                text = attachments.Count == 1 ? "Analyse le fichier joint." : "Analyse les fichiers joints.";

            if (_editingMessageIndex is int editIndex)
            {
                if (editIndex < 0 || editIndex >= _currentConversation.Count ||
                    !string.Equals(_currentConversation[editIndex].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    CancelMessageEdit(restoreDraft: false);
                    _statusBar.SetStatus("Ce message ne peut plus être modifié.", StatusType.Warning);
                    return;
                }

                _currentConversation[editIndex].Content = text;
                _currentConversation[editIndex].Attachments = attachments;
                _currentConversation[editIndex].Timestamp = DateTime.Now;
                if (editIndex < _currentConversation.Count - 1)
                    _currentConversation.RemoveRange(editIndex + 1, _currentConversation.Count - editIndex - 1);
                if (editIndex == 0)
                {
                    ClearPendingConversationTitleRequests();
                    ResetCurrentConversationTitle();
                }
                _editingMessageIndex = null;
                _inputPanel.EndMessageEdit(restoreDraft: false);
            }
            else
            {
                _currentConversation.Add(new Message
                {
                    Role = "user",
                    Content = text,
                    Attachments = attachments,
                    Timestamp = DateTime.Now
                });
            }
            _chatPanel.RenderMessages(_currentConversation);
            _inputPanel.ClearInput();

            // Create the conversation file as soon as its first prompt is sent.
            // The selected model will replace this provisional name with its
            // generated title before the first answer starts.
            SaveConversation();

            _isGenerating = true;
            _isCancellingGeneration = false;
            _generationTimedOut = false;
            _pendingBackendError = "";
            _receivedFirstToken = false;
            System.Threading.Interlocked.Exchange(ref _generationOutputTokenCount, 0);
            System.Threading.Interlocked.Exchange(ref _generationFirstTokenTimestamp, 0);
            System.Threading.Interlocked.Exchange(ref _lastGenerationSpeedUpdateTimestamp, 0);
            System.Threading.Interlocked.Exchange(ref _generationPausedTimestamp, 0);
            System.Threading.Interlocked.Exchange(ref _generationPausedTicks, 0);
            _generationLastProgressTime = DateTime.Now;
            _inputPanel.SetGenerating(true);
            _statusBar.SetStatus("Generation en cours...", StatusType.Info);

            // Watchdog: stoppe une génération sans aucun progrès pendant trop longtemps.
            _generationWatchdog?.Dispose();
            _generationWatchdog = new System.Threading.Timer(GenerationWatchdogCallback, null, 
                TimeSpan.FromSeconds(GenerationTimeoutSeconds), Timeout.InfiniteTimeSpan);

            var conversationId = _currentConversationId;
            var shouldGenerateConversationTitle = false;
            try
            {
                // Build history with system prompt
                var history = new List<object>();
                if (!_appSettings.UltraLiteMode && !string.IsNullOrWhiteSpace(_appSettings.SystemPrompt))
                {
                    history.Add(new { role = "system", content = _appSettings.SystemPrompt });
                }
                // Conversation titles are file/sidebar metadata, never chat messages or model history.
                // The current message is also sent as `prompt`; do not include it twice in history.
                history.AddRange(_currentConversation
                    .Take(Math.Max(0, _currentConversation.Count - 1))
                    .Select(m => new
                    {
                        role = m.Role,
                        content = m.Content,
                        attachments = (m.Attachments ?? new List<ChatAttachment>()).Select(attachment => new
                        {
                            kind = attachment.Kind,
                            path = attachment.Path,
                            name = attachment.Name
                        }).ToArray()
                    }));

                var firstUserMessage = _currentConversation.FirstOrDefault(message => message.Role == "user");
                shouldGenerateConversationTitle = !_appSettings.UltraLiteMode &&
                    !_currentConversationTitleGenerated &&
                    !_conversationTitleRequestPending &&
                    firstUserMessage != null &&
                    File.Exists(_currentConversationFile);
                var firstTitleContext = firstUserMessage?.Content ?? text;
                if (firstUserMessage?.Attachments is { Count: > 0 } titleAttachments)
                    firstTitleContext += " [Fichiers joints : " +
                        string.Join(", ", titleAttachments.Select(attachment => attachment.Name)) + "]";
                object[] titleMessages = shouldGenerateConversationTitle
                    ? new object[] { new { role = "user", content = LimitTitleContext(firstTitleContext) } }
                    : Array.Empty<object>();
                if (shouldGenerateConversationTitle)
                {
                    _pendingConversationTitleFiles[conversationId] = _currentConversationFile;
                    _conversationTitleRequestPending = true;
                }

                var request = new 
                { 
                    type = "generate", 
                    model_path = _selectedModelPath,
                    conversation_id = shouldGenerateConversationTitle ? conversationId : null,
                    generate_title = shouldGenerateConversationTitle,
                    title_messages = titleMessages,
                    conversation_title_prompt = _appSettings.UltraLiteMode ? null : _appSettings.ConversationTitlePrompt,
                    conversation_title_model_path = !_appSettings.UltraLiteMode && _appSettings.UseDedicatedConversationTitleModel
                        ? _appSettings.ConversationTitleModelPath : "",
                    prompt = text, 
                    goal_mode = goalMode,
                    history = history.ToArray(),
                    attachments = attachments.Select(attachment => new
                    {
                        kind = attachment.Kind,
                        path = attachment.Path,
                        name = attachment.Name
                    }).ToArray(),
                    stream = true,
                    temperature = _appSettings.Temperature,
                    top_p = _appSettings.TopP,
                    top_k = _appSettings.TopK,
                    min_p = _appSettings.MinP,
                    max_tokens = _appSettings.MaxTokens,
                    repeat_penalty = _appSettings.RepeatPenalty,
                    repeat_last_n = _appSettings.RepeatLastN,
                    presence_penalty = _appSettings.PresencePenalty,
                    frequency_penalty = _appSettings.FrequencyPenalty,
                    seed = _appSettings.Seed,
                    stop = (_appSettings.StopSequences ?? "")
                        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => value.Trim().Replace("\\n", "\n", StringComparison.Ordinal))
                        .Where(value => value.Length > 0)
                        .ToArray(),
                    context_size = _appSettings.ContextSize,
                    ngl = _appSettings.Ngl,
                    threads = _appSettings.Threads > 0 ? _appSettings.Threads : Environment.ProcessorCount,
                    use_mmap = _appSettings.UseMmapForModel,
                    reasoning_effort = _appSettings.ReasoningEffort,
                    ultra_lite = _appSettings.UltraLiteMode,
                    enable_web_access = !_appSettings.UltraLiteMode && _appSettings.EnableWebAccess,
                    enable_code_execution = !_appSettings.UltraLiteMode && _appSettings.EnableCodeExecution,
                    enable_system_commands = !_appSettings.UltraLiteMode && _appSettings.EnableSystemCommands,
                    enable_forum = !_appSettings.UltraLiteMode && _appSettings.EnableForum,
                    enable_subagents = !_appSettings.UltraLiteMode && _appSettings.EnableSubagents,
                    available_local_models = _appSettings.UltraLiteMode ? Array.Empty<string>() : GetAvailableLocalModelPaths(),
                    available_subagent_models = _appSettings.UltraLiteMode ? Array.Empty<string>() : GetAvailableSubagentModelPaths(),
                    code_workspace = _appSettings.UltraLiteMode ? "" : _appSettings.CodeWorkspace,
                    default_image_model_path = _appSettings.UltraLiteMode ? "" : _appSettings.DefaultImageModelPath,
                    default_omni_model_path = _appSettings.UltraLiteMode ? "" : _appSettings.DefaultOmniModelPath,
                    keep_chat_model_loaded_for_omni = !_appSettings.UltraLiteMode && _appSettings.KeepChatModelLoadedForOmni,
                    use_disk_for_image_memory = !_appSettings.UltraLiteMode && _appSettings.UseDiskForImageMemory,
                    image_engine_memory_limit_gb = _appSettings.ImageEngineMemoryLimitGb,
                    image_minimum_free_disk_gb = _appSettings.ImageMinimumFreeDiskGb,
                    generation_timeout_seconds = _appSettings.GenerationTimeoutSeconds,
                    code_execution_timeout_seconds = _appSettings.CodeExecutionTimeoutSeconds,
                    generated_images_directory = ImageGeneratorForm.GetConfiguredOutputDirectory()
                };
                var json = JsonConvert.SerializeObject(request);
                System.Diagnostics.Debug.WriteLine($"[C# Send] {json}");
                
                // Preserve request order so an immediate cancellation follows
                // the generation request instead of reaching Python first.
                QueuePythonBackendWrite(json, ex =>
                {
                    System.Diagnostics.Debug.WriteLine($"[C# Send Task ERROR] {ex.Message}\n{ex.StackTrace}");
                    if (IsDisposed || Disposing || !IsHandleCreated)
                        return;
                    BeginInvoke(new Action(() =>
                    {
                        _generationWatchdog?.Dispose();
                        _isGenerating = false;
                        _isStreamingResponse = false;
                        _isCancellingGeneration = false;
                        if (shouldGenerateConversationTitle)
                            ForgetPendingConversationTitle(conversationId);
                        _inputPanel.SetGenerating(false);
                        _statusBar.SetStatus("Erreur envoi: " + ex.Message, StatusType.Error);
                    }));
                });
            }
            catch (Exception ex)
            {
                _generationWatchdog?.Dispose();
                _isGenerating = false;
                _isStreamingResponse = false;
                _isCancellingGeneration = false;
                if (shouldGenerateConversationTitle)
                    ForgetPendingConversationTitle(conversationId);
                _inputPanel.SetGenerating(false);
                _statusBar.SetStatus("Erreur: " + ex.Message, StatusType.Error);
            }
        }

        private void OnCancelGeneration(object sender, EventArgs e)
        {
            CancelGeneration(false);
        }

        private void CancelGeneration(bool timedOut)
        {
            if (!_isGenerating || _isCancellingGeneration)
                return;

            _isCancellingGeneration = true;
            _generationTimedOut = timedOut;
            _inputPanel.SetCancelling(true);
            _statusBar.SetStatus(timedOut ? "Delai depasse, arret en cours..." : "Arret de la generation...", StatusType.Warning);

            QueuePythonBackendWrite("{\"type\":\"cancel\"}", ex =>
            {
                System.Diagnostics.Debug.WriteLine("[Cancel generation] " + ex.Message);
            });

            _ = ForceStopBackendIfCancellationStallsAsync();
        }

        private async Task ForceStopBackendIfCancellationStallsAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (IsDisposed || !IsHandleCreated || !_isGenerating || !_isCancellingGeneration)
                return;

            try
            {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                    _pythonProcess.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Force stop backend] " + ex.Message);
            }

            BeginInvoke(new Action(() =>
            {
                if (!_isGenerating || !_isCancellingGeneration)
                    return;

                _generationWatchdog?.Dispose();
                _isGenerating = false;
                _isStreamingResponse = false;
                _isCancellingGeneration = false;
                _inputPanel.SetGenerating(false);
                _inputPanel.SetReady(false);
                _isPythonReady = false;
                ClearPendingConversationTitleRequests();
                _statusBar.SetStatus("Backend redemarre apres l'arret force", StatusType.Warning);
                try
                {
                    _chatPanel.EndStreamingMessage("");
                }
                catch { }
                _pythonInput = null;
                _pythonProcess = null;
                StartPythonBackend();
            }));
        }

        private void OnConversationSelected(object sender, string filename)
        {
            if (_isGenerating)
            {
                _chatPanel.AddSystemMessage("Annulez la generation avant de changer de conversation.", MessageType.Info);
                return;
            }
            var file = Path.Combine(_conversationsDir, filename);
            if (_currentConversation.Count > 0 &&
                !string.Equals(Path.GetFullPath(file), Path.GetFullPath(_currentConversationFile), StringComparison.OrdinalIgnoreCase))
                SaveConversation();
            CancelMessageEdit(restoreDraft: true);
            LoadConversation(file);
        }

        private void OnNewConversation(object sender, EventArgs e)
        {
            if (_isGenerating)
            {
                _chatPanel.AddSystemMessage("Annulez la generation avant de commencer une nouvelle conversation.", MessageType.Info);
                return;
            }
            if (_currentConversation.Count > 0)
                SaveConversation();
            CancelMessageEdit(restoreDraft: true);
            _currentConversation.Clear();
            ResetCurrentConversationTitle();
            _currentConversationFile = CreateNewConversationPath();
            _chatPanel.Clear();
            _statusBar.SetStatus("Nouvelle conversation", StatusType.Info);
            _sidebarPanel.SelectConversation(null);
        }

        private void OnDeleteConversation(object sender, string filename)
        {
            var file = Path.Combine(_conversationsDir, filename);
            if (MessageBox.Show(this, "Supprimer \"" + filename + "\" ?", "Confirmer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try
                {
                    File.Delete(file);
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(_currentConversationFile), StringComparison.OrdinalIgnoreCase))
                    {
                        CancelMessageEdit(restoreDraft: true);
                        _currentConversation.Clear();
                        ResetCurrentConversationTitle();
                        _currentConversationFile = CreateNewConversationPath();
                        _chatPanel.Clear();
                        _sidebarPanel.SelectConversation(null);
                    }
                    LoadConversationList();
                }
                catch (Exception ex)
                {
                    _statusBar.SetStatus("Erreur suppression: " + ex.Message, StatusType.Error);
                }
            }
        }

        private void OnOpenConversationsFolder(object sender, EventArgs e)
        {
            try { Process.Start("explorer.exe", _conversationsDir); }
            catch { }
        }

        private void OnRestartBackend(object sender, EventArgs e)
        {
            if (_pythonProcess != null && !_pythonProcess.HasExited)
            {
                try { _pythonProcess.Kill(entireProcessTree: true); }
                catch { }
            }
            _pythonInput = null;
            _pythonProcess = null;
            _generationWatchdog?.Dispose();
            _isGenerating = false;
            _isStreamingResponse = false;
            _isCancellingGeneration = false;
            _generationTimedOut = false;
            _inputPanel.SetGenerating(false);
            _isPythonReady = false;
            ClearPendingConversationTitleRequests();
            _inputPanel.SetReady(false);
            StartPythonBackend();
        }

        private void OnSettings(object sender, EventArgs e)
        {
            using (var settingsForm = new SettingsForm(_appSettings))
            {
                if (settingsForm.ShowDialog(this) == DialogResult.OK)
                {
                    var themeChanged = _appSettings.DarkMode != settingsForm.Settings.DarkMode;
                    var searchModeChanged =
                        _appSettings.SearchAllDrivesForModels != settingsForm.Settings.SearchAllDrivesForModels ||
                        !string.Equals(_appSettings.DefaultTextModelPath, settingsForm.Settings.DefaultTextModelPath, StringComparison.OrdinalIgnoreCase);
                    _appSettings = settingsForm.Settings;
                    _inputPanel.SetGoalModeEnabled(!_appSettings.UltraLiteMode);
                    _inputPanel.SetShowGenerationSpeed(_appSettings.ShowGenerationSpeed);
                    SaveAppSettings();
                    if (themeChanged)
                    {
                        ThemeColors.SetDarkMode(_appSettings.DarkMode);
                        UpdateTheme();
                    }
                    _statusBar.SetStatus("Parametres sauvegardes", StatusType.Success);
                    if (!string.IsNullOrWhiteSpace(_selectedModelPath))
                        RequestModelCapabilities(_selectedModelPath);
                    if (searchModeChanged)
                        ScanModelsFolder();
                }
            }
        }

        public bool PreFilterMessage(ref System.Windows.Forms.Message message)
        {
            if (message.Msg is not (0x0100 or 0x0104) || !_chatPanel.ContainsFocus)
                return false;
            if ((message.LParam.ToInt64() & (1L << 30)) != 0)
                return false;

            var keyData = (Keys)message.WParam.ToInt32() |
                          (Control.ModifierKeys & Keys.Modifiers);
            var item = FindShortcutItem(_menuStrip.Items, keyData);
            if (item == null || !item.Enabled)
                return false;

            try
            {
                BeginInvoke(new Action(item.PerformClick));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static ToolStripMenuItem? FindShortcutItem(ToolStripItemCollection items, Keys keyData)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem menuItem)
                    continue;
                if (menuItem.ShortcutKeys == keyData)
                    return menuItem;

                var nested = FindShortcutItem(menuItem.DropDownItems, keyData);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private async void OnCheckForUpdates(object sender, EventArgs e)
        {
            await CheckForUpdatesAsync(userInitiated: true);
        }

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (_updateCheckInProgress || _updateHelperLaunched || !string.IsNullOrWhiteSpace(_pendingUpdatePackagePath))
                return;

            _updateCheckInProgress = true;
            try
            {
                _statusBar.SetStatus("Recherche de mises à jour sur GitHub...", StatusType.Info);
                var currentVersion = GitHubUpdater.GetCurrentVersion();
                var release = await GitHubUpdater.CheckLatestAsync(currentVersion);
                if (release == null)
                {
                    if (userInitiated)
                        MessageBox.Show(this, $"Local IA Runner {currentVersion} est à jour.", "Mises à jour",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _statusBar.SetStatus("Application à jour", StatusType.Success);
                    return;
                }

                var updateChoice = ShowUpdateAvailableDialog(release);
                if (updateChoice == DialogResult.Ignore)
                {
                    _appSettings.AutoUpdateEnabled = false;
                    SaveAppSettings();
                    _statusBar.SetStatus(
                        "Les propositions automatiques de mises à jour sont désactivées. Tu peux les réactiver dans les paramètres.",
                        StatusType.Info);
                    return;
                }
                if (updateChoice != DialogResult.Yes)
                {
                    _statusBar.SetStatus($"Mise à jour {release.Version} reportée.", StatusType.Info);
                    return;
                }

                var packageKind = GitHubUpdater.GetPackageKind();
                var asset = release.GetAsset(packageKind);
                _statusBar.SetStatus($"Téléchargement de la mise à jour {release.Version}...", StatusType.Info);
                var progress = new Progress<double>(percent =>
                {
                    if (!IsDisposed && _statusBar != null)
                        _statusBar.SetStatus($"Téléchargement de la mise à jour : {percent:0}%", StatusType.Info);
                });
                var packagePath = await GitHubUpdater.DownloadAssetAsync(asset, progress);
                _pendingUpdatePackagePath = packagePath;
                var waitingForGeneration = _isGenerating || _isStreamingResponse || _imageGenerationActive;
                _statusBar.SetStatus(waitingForGeneration
                        ? $"Mise à jour {release.Version} téléchargée. Installation après la fin de la génération en cours..."
                        : $"Mise à jour {release.Version} téléchargée. Préparation de l'installation...",
                    StatusType.Success);
                ApplyPendingUpdateWhenIdle();
            }
            catch (Exception ex)
            {
                _statusBar.SetStatus("Vérification des mises à jour impossible", StatusType.Warning);
                if (userInitiated)
                {
                    MessageBox.Show(this,
                        "La mise à jour n'a pas pu être téléchargée ou vérifiée.\n\n" + ex.Message,
                        "Mise à jour impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("Échec de la mise à jour automatique : " + ex);
                }
            }
            finally
            {
                _updateCheckInProgress = false;
            }
        }

        private DialogResult ShowUpdateAvailableDialog(GitHubReleaseInfo release)
        {
            using var dialog = new Form
            {
                Text = "Mise à jour disponible",
                ClientSize = new Size(760, 560),
                MinimumSize = new Size(660, 440),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f),
                AutoScaleMode = AutoScaleMode.Font
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 1,
                RowCount = 5,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));

            layout.Controls.Add(new Label
            {
                Text = $"Local IA Runner {release.Version}",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Background,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "Une nouvelle version est disponible. Le téléchargement démarre seulement après ton accord. L'installation fermera puis redémarrera l'application; si une génération est en cours, elle attendra sa fin. « Ne plus me proposer » désactive les alertes automatiques au démarrage.",
                Dock = DockStyle.Fill,
                ForeColor = ThemeColors.TextSecondary,
                BackColor = ThemeColors.Background,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);

            var notes = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = ThemeColors.Surface,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f),
                Text = string.IsNullOrWhiteSpace(release.Notes)
                    ? "Aucune note de version n'est disponible."
                    : release.Notes.Trim()
            };
            layout.Controls.Add(notes, 0, 2);

            var releaseLink = new LinkLabel
            {
                Text = "Consulter la publication complète sur GitHub",
                AutoSize = true,
                Dock = DockStyle.Left,
                LinkColor = ThemeColors.Primary,
                ActiveLinkColor = ThemeColors.PrimaryHover,
                VisitedLinkColor = ThemeColors.Primary,
                BackColor = ThemeColors.Background,
                Font = new Font("Segoe UI", 9f)
            };
            releaseLink.LinkClicked += (sender, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(release.ReleaseUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogException("Ouverture des notes de mise à jour GitHub", ex);
                }
            };
            layout.Controls.Add(releaseLink, 0, 3);

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                BackColor = ThemeColors.Background,
                Padding = new Padding(0, 8, 0, 0)
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246f));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));

            var laterButton = new ModernButton
            {
                Text = "Plus tard",
                DialogResult = DialogResult.No,
                Width = 100,
                Height = 38,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                TextColor = ThemeColors.TextPrimary,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            var neverButton = new ModernButton
            {
                Text = "Ne plus me proposer les mises à jour",
                DialogResult = DialogResult.Ignore,
                Width = 240,
                Height = 38,
                BackColorNormal = ThemeColors.SurfaceHover,
                BackColorHover = ThemeColors.BorderLight,
                TextColor = ThemeColors.TextPrimary,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            var installButton = new ModernButton
            {
                Text = "Télécharger et installer",
                DialogResult = DialogResult.Yes,
                Width = 194,
                Height = 38,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            buttons.Controls.Add(laterButton, 1, 0);
            buttons.Controls.Add(neverButton, 2, 0);
            buttons.Controls.Add(installButton, 3, 0);
            layout.Controls.Add(buttons, 0, 4);

            dialog.Controls.Add(layout);
            dialog.AcceptButton = installButton;
            dialog.CancelButton = laterButton;
            return dialog.ShowDialog(this);
        }

        private void ApplyPendingUpdateWhenIdle()
        {
            if (IsDisposed || Disposing || _updateHelperLaunched ||
                string.IsNullOrWhiteSpace(_pendingUpdatePackagePath) ||
                _isGenerating || _isStreamingResponse || _imageGenerationActive)
                return;

            var packagePath = _pendingUpdatePackagePath!;
            try
            {
                GitHubUpdater.StartApplyHelper(packagePath);
                _updateHelperLaunched = true;
                _pendingUpdatePackagePath = null;
                _isPythonReady = false;
                _inputPanel.SetReady(false);
                _imageGeneratorForm?.Close();
                _statusBar.SetStatus("Génération terminée. Fermeture pour installer la mise à jour...", StatusType.Info);
                BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                GitHubUpdater.DeleteDownloadedAsset(packagePath);
                _pendingUpdatePackagePath = null;
                _statusBar.SetStatus("Impossible de démarrer l'installateur", StatusType.Error);
                LogException("Démarrage de l'installateur téléchargé", ex);
                MessageBox.Show(this,
                    "L'installateur n'a pas pu démarrer. Le téléchargement a été supprimé; tu peux relancer la vérification des mises à jour.\n\n" + ex.Message,
                    "Mise à jour impossible", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnOpenImageGenerator(object sender, EventArgs e)
        {
            if (!_isPythonReady)
            {
                _statusBar.SetStatus("Le backend IA démarre encore.", StatusType.Info);
                return;
            }

            if (_imageGeneratorForm != null && !_imageGeneratorForm.IsDisposed)
            {
                _imageGeneratorForm.Activate();
                return;
            }

            _imageGeneratorForm = new ImageGeneratorForm(
                _appSettings.DefaultImageModelPath,
                _appSettings.UseDiskForImageMemory,
                _appSettings.ImageEngineMemoryLimitGb,
                _appSettings.ImageMinimumFreeDiskGb);
            _imageGeneratorForm.CatalogRequested += (s, modelDir) =>
                SendImageBackendRequest(new
                {
                    type = "image_catalog",
                    model_dir = modelDir,
                    image_engine_memory_limit_gb = _appSettings.ImageEngineMemoryLimitGb,
                    image_minimum_free_disk_gb = _appSettings.ImageMinimumFreeDiskGb
                });
            _imageGeneratorForm.GenerateRequested += (s, request) => SendImageBackendRequest(request);
            _imageGeneratorForm.CancelRequested += (s, e) => SendImageBackendRequest(new { type = "cancel" });
            _imageGeneratorForm.FormClosed += (s, e) =>
            {
                if (ReferenceEquals(_imageGeneratorForm, s))
                    _imageGeneratorForm = null;
            };
            _imageGeneratorForm.Show(this);
        }

        private void OnOpenForum(object sender, EventArgs e)
        {
            if (_forumForm != null && !_forumForm.IsDisposed)
            {
                _forumForm.Activate();
                return;
            }

            _forumForm = new ForumForm();
            _forumForm.FormClosed += (s, args) =>
            {
                if (ReferenceEquals(_forumForm, s))
                    _forumForm = null;
            };
            _forumForm.Show(this);
        }

        private void OnOpenSubagentActivity(object sender, EventArgs e) =>
            ShowSubagentActivity(activate: true);

        private void ShowSubagentActivity(bool activate)
        {
            if (_subagentActivityForm == null || _subagentActivityForm.IsDisposed)
            {
                _subagentActivityForm = new SubagentActivityForm();
                _subagentActivityForm.FormClosed += (s, e) =>
                {
                    if (ReferenceEquals(_subagentActivityForm, s))
                        _subagentActivityForm = null;
                };
            }

            if (!_subagentActivityForm.Visible)
                _subagentActivityForm.Show(this);
            if (activate)
                _subagentActivityForm.Activate();
        }

        private void SendImageBackendRequest(object request)
        {
            var json = JsonConvert.SerializeObject(request);
            QueuePythonBackendWrite(json, ex =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _imageGenerationActive = false;
                        _imageGeneratorForm?.SetCompleted(new List<string>(), ex.Message, false);
                        ApplyPendingUpdateWhenIdle();
                    }));
                }
            });
        }

        private void QueuePythonBackendWrite(string json, Action<Exception>? onError = null)
        {
            lock (_pythonWriteQueueLock)
            {
                _pythonWriteQueue = _pythonWriteQueue.ContinueWith(_ =>
                {
                    try
                    {
                        lock (_pythonInputLock)
                        {
                            if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                                throw new InvalidOperationException("Le backend Python n'est pas disponible.");
                            _pythonInput.WriteLine(json);
                            _pythonInput.Flush();
                        }
                    }
                    catch (Exception exception)
                    {
                        try { onError?.Invoke(exception); }
                        catch (Exception callbackException)
                        {
                            System.Diagnostics.Debug.WriteLine("[Backend request callback] " + callbackException.Message);
                        }
                    }
                }, System.Threading.CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private static List<ChatAttachment> ImportGeneratedMedia(JArray? media)
        {
            var result = new List<ChatAttachment>();
            if (media == null)
                return result;

            foreach (var item in media)
            {
                try
                {
                    var attachment = item.ToObject<ChatAttachment>();
                    if (attachment == null || string.IsNullOrWhiteSpace(attachment.Path) ||
                        !File.Exists(attachment.Path))
                        continue;
                    if (ChatMediaStorage.GetMediaUrl(attachment.Path) == null)
                        attachment = ChatMediaStorage.ImportGenerated(attachment.Path);
                    result.Add(attachment);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine("Generated media could not be attached to chat: " + exception.Message);
                }
            }
            return result;
        }

        private void ShowImageMemoryError(string error)
        {
            if (string.IsNullOrWhiteSpace(error) || !IsImageMemoryError(error))
                return;

            const int maximumDetailLength = 5000;
            var detail = error.Trim();
            if (detail.Length > maximumDetailLength)
                detail = detail[..maximumDetailLength] + "…";

            var message =
                "Le moteur de génération n’a pas pu réserver assez de mémoire. Aucune image complète n’a été enregistrée pour cette tentative.\n\n" +
                "Détail technique :\n" + detail + "\n\n" +
                "Essaie de réduire la largeur et la hauteur, de diminuer le nombre d’étapes, ou d’activer « Mode CPU sécurisé pour les images » dans les paramètres. " +
                "Fermer d’autres applications peut aussi libérer de la mémoire.";
            MessageBox.Show(
                this,
                message,
                "Mémoire insuffisante pour l’image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static bool IsImageMemoryError(string error)
        {
            var normalized = error.ToLowerInvariant();
            string[] markers =
            {
                "out of memory",
                "not enough memory",
                "insufficient memory",
                "memory allocation failed",
                "failed to allocate",
                "cannot allocate memory",
                "could not allocate memory",
                "bad_alloc",
                "vk_error_out_of_device_memory",
                "vk_error_out_of_host_memory",
                "cuda out of memory",
                "winerror 1455",
                "error 1455",
                "paging file is too small",
                "ram weight capacity exceeded",
                "ram graph capacity exceeded",
                "mémoire insuffisante",
                "manque de mémoire",
                "pas assez de mémoire",
                "allocation mémoire impossible"
            };
            foreach (var marker in markers)
            {
                if (normalized.Contains(marker, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void SaveConversation()
        {
            try
            {
                if (_currentConversation.Count == 0)
                    return;

                var currentFileName = Path.GetFileNameWithoutExtension(_currentConversationFile);
                const string conversationPrefix = "conversation_";
                var isDefaultConversationName = currentFileName.StartsWith(conversationPrefix, StringComparison.OrdinalIgnoreCase)
                    && currentFileName.Length == conversationPrefix.Length + 15;

                var title = _currentConversationTitle;
                if (string.IsNullOrWhiteSpace(title) && !isDefaultConversationName &&
                    currentFileName.StartsWith(conversationPrefix, StringComparison.OrdinalIgnoreCase) &&
                    currentFileName.Length > conversationPrefix.Length + 16)
                {
                    title = currentFileName.Substring(conversationPrefix.Length + 16).Replace('_', ' ').Trim();
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    var firstUserMsg = _currentConversation.FirstOrDefault(m => m.Role == "user");
                    title = firstUserMsg?.Content?.Trim() ?? "";
                    if (title.Length > 50)
                        title = title.Substring(0, 50).Trim() + "...";
                }

                if (string.IsNullOrWhiteSpace(title))
                    title = $"Conversation {DateTime.Now:yyyy-MM-dd HH:mm}";

                var data = new ConversationData
                {
                    ModelPath = _selectedModelPath,
                    Messages = _currentConversation,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    Title = title,
                    TitleGenerated = _currentConversationTitleGenerated
                };

                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(_currentConversationFile, json);

                LoadConversationList();
                _sidebarPanel.SelectConversation(Path.GetFileName(_currentConversationFile));
            }
            catch (Exception ex)
            {
                _statusBar.SetStatus("Erreur sauvegarde: " + ex.Message, StatusType.Error);
            }
        }

        private string GetUniqueConversationPath(string title, string currentPath = null)
        {
            var baseName = $"conversation_{DateTime.Now:yyyyMMdd_HHmmss}_{SanitizeFileName(title)}";
            var candidate = Path.Combine(_conversationsDir, baseName + ".json");
            if (string.Equals(candidate, currentPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                return candidate;

            var suffix = 2;
            while (true)
            {
                candidate = Path.Combine(_conversationsDir, $"{baseName}_{suffix}.json");
                if (!File.Exists(candidate))
                    return candidate;
                suffix++;
            }
        }

        private string CreateNewConversationPath()
        {
            var baseName = $"conversation_{DateTime.Now:yyyyMMdd_HHmmss}";
            var candidate = Path.Combine(_conversationsDir, baseName + ".json");
            if (!File.Exists(candidate))
                return candidate;

            var suffix = 2;
            do
            {
                candidate = Path.Combine(_conversationsDir, $"{baseName}_{suffix}.json");
                suffix++;
            }
            while (File.Exists(candidate));
            return candidate;
        }

        private void ResetCurrentConversationTitle()
        {
            _currentConversationId = Guid.NewGuid().ToString("N");
            _currentConversationTitle = "";
            _chatPanel.SetConversationTitle("Nouvelle conversation");
            _currentConversationTitleGenerated = false;
            _conversationTitleRequestPending = false;
            _streamingConversationTitle.Clear();
            _streamingConversationTitleId = "";
        }

        private void ClearPendingConversationTitleRequests()
        {
            foreach (var file in _pendingConversationTitleFiles.Values)
                _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(file), "");
            _pendingConversationTitleFiles.Clear();
            _conversationTitleRequestPending = false;
            _streamingConversationTitle.Clear();
            _streamingConversationTitleId = "";
        }

        private void ForgetPendingConversationTitle(string conversationId)
        {
            if (!_pendingConversationTitleFiles.TryGetValue(conversationId, out var file))
                return;

            _pendingConversationTitleFiles.Remove(conversationId);
            _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(file), "");
            if (string.Equals(_currentConversationId, conversationId, StringComparison.Ordinal))
            {
                _conversationTitleRequestPending = false;
            }
            if (string.Equals(_streamingConversationTitleId, conversationId, StringComparison.Ordinal))
            {
                _streamingConversationTitle.Clear();
                _streamingConversationTitleId = "";
            }
        }

        private void StartStreamingConversationTitle(PythonMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ConversationId) ||
                !_pendingConversationTitleFiles.TryGetValue(message.ConversationId, out var file))
                return;

            _streamingConversationTitle.Clear();
            _streamingConversationTitleId = message.ConversationId;
            _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(file), "…");
            if (string.Equals(_currentConversationId, message.ConversationId, StringComparison.Ordinal))
                _chatPanel.SetConversationTitle("Nommage en cours…");
        }

        private void AppendStreamingConversationTitle(PythonMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ConversationId) ||
                !_pendingConversationTitleFiles.TryGetValue(message.ConversationId, out var file))
                return;

            if (!string.Equals(_streamingConversationTitleId, message.ConversationId, StringComparison.Ordinal))
                StartStreamingConversationTitle(message);
            _streamingConversationTitle.Append(message.Content ?? "");
            var preview = NormalizeConversationTitle(_streamingConversationTitle.ToString());
            if (!string.IsNullOrWhiteSpace(preview))
            {
                _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(file), preview);
                if (string.Equals(_currentConversationId, message.ConversationId, StringComparison.Ordinal))
                    _chatPanel.SetConversationTitle(preview);
            }
        }

        private static string LimitTitleContext(string content)
        {
            content ??= "";
            const int maxMessageLength = 800;
            return content.Length > maxMessageLength
                ? content.Substring(0, maxMessageLength) + "…"
                : content;
        }

        private void ApplyGeneratedConversationTitle(PythonMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.ConversationId))
                return;

            if (!_pendingConversationTitleFiles.TryGetValue(message.ConversationId, out var oldPath))
            {
                if (message.WaitForApply)
                    AcknowledgeConversationTitle(message.ConversationId);
                return;
            }

            _pendingConversationTitleFiles.Remove(message.ConversationId);
            _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(oldPath), "");
            if (string.Equals(_currentConversationId, message.ConversationId, StringComparison.Ordinal))
                _chatPanel.SetConversationTitle(string.IsNullOrWhiteSpace(_currentConversationTitle)
                    ? "Nouvelle conversation"
                    : _currentConversationTitle);
            if (string.Equals(_streamingConversationTitleId, message.ConversationId, StringComparison.Ordinal))
            {
                _streamingConversationTitle.Clear();
                _streamingConversationTitleId = "";
            }
            if (string.Equals(_currentConversationId, message.ConversationId, StringComparison.Ordinal))
                _conversationTitleRequestPending = false;

            var title = message.Cancelled ? "" : NormalizeConversationTitle(message.Content);
            if (string.IsNullOrWhiteSpace(title) || !File.Exists(oldPath))
            {
                if (message.WaitForApply)
                    AcknowledgeConversationTitle(message.ConversationId);
                return;
            }

            try
            {
                var data = JsonConvert.DeserializeObject<ConversationData>(File.ReadAllText(oldPath));
                if (data == null)
                    return;

                data.Title = title;
                data.TitleGenerated = true;
                data.UpdatedAt = DateTime.Now;
                var newPath = GetUniqueConversationPath(title, oldPath);
                File.WriteAllText(newPath, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
                    File.Delete(oldPath);

                var currentConversationMatches =
                    string.Equals(_currentConversationId, message.ConversationId, StringComparison.Ordinal) ||
                    string.Equals(Path.GetFullPath(_currentConversationFile), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);
                if (currentConversationMatches)
                {
                    _currentConversationFile = newPath;
                    _currentConversationTitle = title;
                    _chatPanel.SetConversationTitle(title);
                    _currentConversationTitleGenerated = true;
                }

                LoadConversationList();
                var selectedConversationFile = currentConversationMatches ? newPath : _currentConversationFile;
                _sidebarPanel.SelectConversation(File.Exists(selectedConversationFile)
                    ? Path.GetFileName(selectedConversationFile)
                    : null);
                Log("Titre généré et sauvegardé: " + title);
            }
            catch (Exception ex)
            {
                LogException("Application du titre généré", ex);
            }
            finally
            {
                if (message.WaitForApply)
                    AcknowledgeConversationTitle(message.ConversationId);
            }
        }

        private void AcknowledgeSubagentForumClear(string requestId, bool approved)
        {
            try
            {
                var json = JsonConvert.SerializeObject(new
                {
                    type = "subagent_forum_clear_response",
                    request_id = requestId,
                    approved
                });
                lock (_pythonInputLock)
                {
                    if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                        return;
                    _pythonInput.WriteLine(json);
                    _pythonInput.Flush();
                }
            }
            catch (Exception ex)
            {
                LogException("Confirmation du vidage du forum temporaire", ex);
            }
        }

        private void AcknowledgeConversationTitle(string conversationId)
        {
            try
            {
                var json = JsonConvert.SerializeObject(new
                {
                    type = "title_applied",
                    conversation_id = conversationId
                });
                lock (_pythonInputLock)
                {
                    if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                        return;
                    _pythonInput.WriteLine(json);
                    _pythonInput.Flush();
                }
            }
            catch (Exception ex)
            {
                LogException("Confirmation du titre généré", ex);
            }
        }

        private static string NormalizeConversationTitle(string title)
        {
            title = (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            while (title.Contains("  ", StringComparison.Ordinal))
                title = title.Replace("  ", " ", StringComparison.Ordinal);

            if (title.StartsWith("Titre", StringComparison.OrdinalIgnoreCase))
            {
                var separator = title.IndexOf(':');
                if (separator >= 0 && separator <= 10)
                    title = title.Substring(separator + 1).Trim();
            }
            title = title.Trim(' ', '"', '\'', '`', '“', '”', '‘', '’', '#', '*', ':', ';', ',', '.', '!', '?');
            if (title.Length > 60)
                title = title.Substring(0, 57).TrimEnd() + "...";
            return title;
        }
        
        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
                fileName = fileName.Replace(c, '_');
            return fileName.Length > 50 ? fileName.Substring(0, 50) : fileName;
        }

        private void LoadConversationList()
        {
            var files = Directory.GetFiles(_conversationsDir, "*.json")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Select(f => Path.GetFileName(f))
                .ToArray();
            _sidebarPanel.LoadConversations(files);
        }

        private void LoadConversation(string file)
        {
            try
            {
                CancelMessageEdit(restoreDraft: true);
                var json = File.ReadAllText(file);
                var data = JsonConvert.DeserializeObject<ConversationData>(json);
                if (data != null)
                {
                    ResetCurrentConversationTitle();
                    _currentConversation = data.Messages ?? new List<Message>();
                    _currentConversationTitle = !string.IsNullOrWhiteSpace(data.Title)
                        ? data.Title
                        : Path.GetFileNameWithoutExtension(file).Replace("conversation_", "");
                    // Older conversation files have no title-generation flag; keep those
                    // stable on reload. Retry only when a newer file records a failed title.
                    _currentConversationTitleGenerated = data.TitleGenerated ?? _currentConversation.Count > 0;
                    if (!string.IsNullOrEmpty(data.ModelPath))
                    {
                        _selectedModelPath = data.ModelPath;
                        var modelName = GetModelDisplayName(data.ModelPath);
                        _selectedModelName = modelName;
                        _inputPanel.SetModelSelected(true, modelName);
                        _chatPanel.SetModelName(modelName);
                        RequestModelCapabilities(data.ModelPath);
                    }
                    else
                    {
                        _chatPanel.SetModelName(_selectedModelName);
                    }
                    _currentConversationFile = file;
                    _chatPanel.SetConversationTitle(_currentConversationTitle);

                    _chatPanel.RenderMessages(_currentConversation);
                    _sidebarPanel.SelectConversation(Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _statusBar.SetStatus("Erreur ouverture conversation: " + ex.Message, StatusType.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_updateHelperLaunched &&
                !string.IsNullOrWhiteSpace(_pendingUpdatePackagePath) &&
                e.CloseReason == CloseReason.UserClosing &&
                (_isGenerating || _isStreamingResponse || _imageGenerationActive))
            {
                e.Cancel = true;
                _statusBar.SetStatus(
                    "La mise à jour attend la fin de la génération. Utilise Annuler si tu veux l'interrompre.",
                    StatusType.Warning);
                return;
            }

            if (!_updateHelperLaunched && !string.IsNullOrWhiteSpace(_pendingUpdatePackagePath))
            {
                GitHubUpdater.DeleteDownloadedAsset(_pendingUpdatePackagePath);
                _pendingUpdatePackagePath = null;
            }

            SaveConversation();
            Application.RemoveMessageFilter(this);
            var activeModelScan = System.Threading.Interlocked.Exchange(ref _modelsScanCancellation, null);
            activeModelScan?.Cancel();
            _generationWatchdog?.Dispose();
            _streamRenderTimer.Stop();
            _streamRenderTimer.Dispose();
            if (_pythonProcess != null && !_pythonProcess.HasExited)
            {
                try { _pythonProcess.Kill(entireProcessTree: true); }
                catch { }
            }
            _subagentActivityForm?.CloseForApplicationExit();
            base.OnFormClosing(e);
        }

        private void GenerationWatchdogCallback(object state)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke(new Action(CheckGenerationWatchdog));
            }
            catch (InvalidOperationException)
            {
                // The form may be closing while the one-shot timer fires.
            }
        }

        private void CheckGenerationWatchdog()
        {
            if (!_isGenerating)
                return;

            var elapsed = DateTime.Now - _generationLastProgressTime;
            System.Diagnostics.Debug.WriteLine($"[Watchdog] Check: elapsed={elapsed.TotalSeconds:F1}s, isGenerating={_isGenerating}, receivedFirstToken={_receivedFirstToken}, pythonReady={_isPythonReady}, pythonExited={_pythonProcess?.HasExited}");
            
            if (elapsed.TotalSeconds >= GenerationTimeoutSeconds)
            {
                System.Diagnostics.Debug.WriteLine($"[Watchdog] No progress for {elapsed.TotalSeconds:F1}s - requesting backend cancellation");
                CancelGeneration(true);
                return;
            }

            var remaining = TimeSpan.FromSeconds(GenerationTimeoutSeconds) - elapsed;
            _generationWatchdog?.Change(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan);
        }

        private void MarkGenerationProgress()
        {
            if (!_isGenerating)
                return;

            _generationLastProgressTime = DateTime.Now;
            try
            {
                _generationWatchdog?.Change(
                    TimeSpan.FromSeconds(GenerationTimeoutSeconds),
                    Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // A terminal backend message can dispose the watchdog as the last token arrives.
            }
        }

        private void Log(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                lock (_logLock)
                {
                    File.AppendAllText(_logFile, line + Environment.NewLine);
                }
                System.Diagnostics.Debug.WriteLine(line); // Also to DebugView
            }
            catch { }
        }

        private string AddApplicationLogLocation(string error)
        {
            return $"{error}{Environment.NewLine}{Environment.NewLine}Journal de diagnostic : {_logFile}";
        }

        private void LogException(string context, Exception ex)
        {
            Log($"[EXCEPTION] {context}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ===== DATA MODELS =====
    
    public class AppConfig
    {
        public string ModelsFolder { get; set; }
    }

    public class AppSettings
    {
        public const string DefaultConversationTitlePrompt = "Tu es un générateur de titres de conversation. Produis une étiquette de 2 à 5 mots qui décrit le sujet, dans la langue de la conversation. Ne réponds jamais à la question, ne résous pas la demande et ne poursuis pas la conversation. N'ajoute aucune explication, préambule, ponctuation finale ni guillemets. Ta réponse doit contenir uniquement le titre.";
        public static string DefaultCodeWorkspace => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocalIARunnerWorkspace");
        public string SystemPrompt { get; set; } = "";
        public string ConversationTitlePrompt { get; set; } = DefaultConversationTitlePrompt;
        public string DefaultTextModelPath { get; set; } = "";
        public string DefaultImageModelPath { get; set; } = "";
        public string DefaultOmniModelPath { get; set; } = "";
        public bool KeepChatModelLoadedForOmni { get; set; } = false;
        public bool UseDedicatedConversationTitleModel { get; set; } = false;
        public string ConversationTitleModelPath { get; set; } = "";
        public bool UltraLiteMode { get; set; } = false;
        public bool EnableWebAccess { get; set; } = true;
        public bool EnableCodeExecution { get; set; } = false;
        public bool EnableSystemCommands { get; set; } = false;
        public bool UseDiskForImageMemory { get; set; } = false;
        public int ImageEngineMemoryLimitGb { get; set; } = 4;
        public int ImageMinimumFreeDiskGb { get; set; } = 5;
        public bool EnableForum { get; set; } = false;
        public bool EnableSubagents { get; set; } = false;
        public bool SearchAllDrivesForModels { get; set; } = false;
        public bool AutoUpdateEnabled { get; set; } = true;
        public bool DarkMode { get; set; } = false;
        public bool ShowGenerationSpeed { get; set; } = false;
        public string CodeWorkspace { get; set; } = DefaultCodeWorkspace;
        public float Temperature { get; set; } = 0.7f;
        public float TopP { get; set; } = 0.9f;
        public int TopK { get; set; } = 40;
        public float MinP { get; set; } = 0.05f;
        public int MaxTokens { get; set; } = 2048;
        public float RepeatPenalty { get; set; } = 1.0f;
        public int RepeatLastN { get; set; } = 64;
        public float PresencePenalty { get; set; } = 0.0f;
        public float FrequencyPenalty { get; set; } = 0.0f;
        public long Seed { get; set; } = -1;
        public string StopSequences { get; set; } = "";
        public int ContextSize { get; set; } = 4096;
        public int Ngl { get; set; } = 99; // GPU layers
        public int Threads { get; set; } = 0; // 0 = auto
        public bool UseMmapForModel { get; set; } = false;
        public bool MemoryOptionsInitialized { get; set; } = false;
        public string ReasoningEffort { get; set; } = "default";
        public int GenerationTimeoutSeconds { get; set; } = 900;
        public int CodeExecutionTimeoutSeconds { get; set; } = 30;
    }

    public enum ModelType { Transformers, GGUF, ONNX, Unknown }
    public enum MessageType { Info, Success, Warning, Error, Debug }
    public enum StatusType { Info, Success, Warning, Error }

    public class LocalModelInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public ModelType Type { get; set; }
        public string ModelSubType { get; set; }
        public long Size { get; set; }
        public string Folder { get; set; }
        
        public string SizeFormatted => FormatSize(Size);
        public string TypeIcon => Type == ModelType.Transformers ? "[T]" : Type == ModelType.GGUF ? "[G]" : "[?]";
        public string TypeLabel => Type == ModelType.Transformers ? "Transformers" : Type == ModelType.GGUF ? "GGUF (llama.cpp)" : Type.ToString();
        public string SubTypeLabel => !string.IsNullOrEmpty(ModelSubType) ? ModelSubType : "";
        
        private static string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int i = 0;
            double size = bytes;
            while (size >= 1024 && i < suffixes.Length - 1)
            {
                size /= 1024;
                i++;
            }
            return size.ToString("0.##") + " " + suffixes[i];
        }
    }

    public class Message
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public List<ChatAttachment> Attachments { get; set; } = new();
    }

    public class ConversationData
    {
        public string ModelPath { get; set; }
        public List<Message> Messages { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Title { get; set; }
        public bool? TitleGenerated { get; set; }
    }

    public class PythonMessage
    {
        public string Type { get; set; }
        public string Content { get; set; }
        [JsonProperty("source")]
        public string Source { get; set; }
        [JsonProperty("conversation_id")]
        public string ConversationId { get; set; }
        [JsonProperty("request_id")]
        public string RequestId { get; set; }
        [JsonProperty("wait_for_apply")]
        public bool WaitForApply { get; set; }
        public string Model { get; set; }
        [JsonProperty("activity_session_id")]
        public string ActivitySessionId { get; set; } = "";
        [JsonProperty("activity_id")]
        public string ActivityId { get; set; } = "";
        [JsonProperty("activity_kind")]
        public string ActivityKind { get; set; } = "";
        public bool Cancelled { get; set; }
        public string Error { get; set; }
        public JArray Recipes { get; set; }
        public JArray Files { get; set; }
        public JArray Errors { get; set; }
        public JArray Paths { get; set; }
        [JsonProperty("media")]
        public JArray Media { get; set; }
        [JsonProperty("supports_multimodal")]
        public bool? SupportsMultimodal { get; set; }
        [JsonProperty("can_analyze_images_with_omni")]
        public bool? CanAnalyzeImagesWithOmni { get; set; }
        [JsonProperty("model_path")]
        public string ModelPath { get; set; } = "";
        public string Stage { get; set; }
        public string Detail { get; set; }
        public string Status { get; set; }
        public int? Completed { get; set; }
        public int? Total { get; set; }
        public int? Pass { get; set; }
        public int? Step { get; set; }
        public long? Downloaded { get; set; }
    }

    // ===== THEME =====
    public static class ThemeColors
    {
        private static bool _darkMode;

        private static readonly (Color Light, Color Dark)[] PalettePairs =
        {
            (Color.FromArgb(0xF5, 0xF6, 0xF8), Color.FromArgb(0x1B, 0x1D, 0x24)),
            (Color.FromArgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0x22, 0x25, 0x2D)),
            (Color.FromArgb(0xF8, 0xF9, 0xFC), Color.FromArgb(0x2A, 0x2E, 0x38)),
            (Color.FromArgb(0xE5, 0xE8, 0xEE), Color.FromArgb(0x39, 0x3E, 0x49)),
            (Color.FromArgb(0xD9, 0xDD, 0xE6), Color.FromArgb(0x42, 0x48, 0x55)),
            (Color.FromArgb(0x59, 0x67, 0xDC), Color.FromArgb(0x7C, 0x89, 0xFF)),
            (Color.FromArgb(0x4B, 0x58, 0xCB), Color.FromArgb(0x8C, 0x97, 0xFF)),
            (Color.FromArgb(0x40, 0x4C, 0xB4), Color.FromArgb(0x65, 0x73, 0xEF)),
            (Color.FromArgb(0x23, 0x85, 0x5B), Color.FromArgb(0x3C, 0xB4, 0x85)),
            (Color.FromArgb(0xA2, 0x74, 0x19), Color.FromArgb(0xE2, 0xB2, 0x57)),
            (Color.FromArgb(0xB7, 0x49, 0x43), Color.FromArgb(0xEB, 0x78, 0x74)),
            (Color.FromArgb(0x20, 0x24, 0x31), Color.FromArgb(0xEA, 0xED, 0xF4)),
            (Color.FromArgb(0x55, 0x5D, 0x6D), Color.FromArgb(0xB8, 0xBF, 0xCC)),
            (Color.FromArgb(0x73, 0x79, 0x89), Color.FromArgb(0x91, 0x99, 0xA8)),
            (Color.FromArgb(0x85, 0x8B, 0x99), Color.FromArgb(0x9B, 0xA3, 0xB2)),
            (Color.FromArgb(0xF1, 0xF3, 0xF7), Color.FromArgb(0x2B, 0x30, 0x3B)),
            (Color.FromArgb(0xF8, 0xF9, 0xFB), Color.FromArgb(0x19, 0x1C, 0x23)),
            (Color.FromArgb(0xD5, 0xD9, 0xE1), Color.FromArgb(0x39, 0x3E, 0x49)),
            (Color.FromArgb(0xB9, 0xBF, 0xCA), Color.FromArgb(0x52, 0x59, 0x67))
        };

        public static bool IsDarkMode => _darkMode;
        public static Color Background => _darkMode ? Color.FromArgb(0x1B, 0x1D, 0x24) : Color.FromArgb(0xF5, 0xF6, 0xF8);
        public static Color Surface => _darkMode ? Color.FromArgb(0x22, 0x25, 0x2D) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static Color SurfaceHover => _darkMode ? Color.FromArgb(0x2A, 0x2E, 0x38) : Color.FromArgb(0xF8, 0xF9, 0xFC);
        public static Color Border => _darkMode ? Color.FromArgb(0x39, 0x3E, 0x49) : Color.FromArgb(0xE5, 0xE8, 0xEE);
        public static Color BorderLight => _darkMode ? Color.FromArgb(0x42, 0x48, 0x55) : Color.FromArgb(0xD9, 0xDD, 0xE6);
        public static Color Primary => _darkMode ? Color.FromArgb(0x7C, 0x89, 0xFF) : Color.FromArgb(0x59, 0x67, 0xDC);
        public static Color PrimaryHover => _darkMode ? Color.FromArgb(0x8C, 0x97, 0xFF) : Color.FromArgb(0x4B, 0x58, 0xCB);
        public static Color PrimaryPressed => _darkMode ? Color.FromArgb(0x65, 0x73, 0xEF) : Color.FromArgb(0x40, 0x4C, 0xB4);
        public static Color Success => _darkMode ? Color.FromArgb(0x3C, 0xB4, 0x85) : Color.FromArgb(0x23, 0x85, 0x5B);
        public static Color Warning => _darkMode ? Color.FromArgb(0xE2, 0xB2, 0x57) : Color.FromArgb(0xA2, 0x74, 0x19);
        public static Color Error => _darkMode ? Color.FromArgb(0xEB, 0x78, 0x74) : Color.FromArgb(0xB7, 0x49, 0x43);
        public static Color TextPrimary => _darkMode ? Color.FromArgb(0xEA, 0xED, 0xF4) : Color.FromArgb(0x20, 0x24, 0x31);
        public static Color TextSecondary => _darkMode ? Color.FromArgb(0xB8, 0xBF, 0xCC) : Color.FromArgb(0x55, 0x5D, 0x6D);
        public static Color TextMuted => _darkMode ? Color.FromArgb(0x91, 0x99, 0xA8) : Color.FromArgb(0x73, 0x79, 0x89);
        public static Color Thinking => _darkMode ? Color.FromArgb(0x9B, 0xA3, 0xB2) : Color.FromArgb(0x85, 0x8B, 0x99);
        public static Color UserBubble => _darkMode ? Color.FromArgb(0x2B, 0x30, 0x3B) : Color.FromArgb(0xF1, 0xF3, 0xF7);
        public static Color AssistantBubble => Surface;
        public static Color CodeBackground => _darkMode ? Color.FromArgb(0x19, 0x1C, 0x23) : Color.FromArgb(0xF8, 0xF9, 0xFB);
        public static Color ScrollBar => _darkMode ? Color.FromArgb(0x39, 0x3E, 0x49) : Color.FromArgb(0xD5, 0xD9, 0xE1);
        public static Color ScrollBarHover => _darkMode ? Color.FromArgb(0x52, 0x59, 0x67) : Color.FromArgb(0xB9, 0xBF, 0xCA);

        public static void SetDarkMode(bool enabled) => _darkMode = enabled;

        private static Color MapPaletteColor(Color color)
        {
            foreach (var pair in PalettePairs)
            {
                var source = _darkMode ? pair.Light : pair.Dark;
                if (color.ToArgb() == source.ToArgb())
                    return _darkMode ? pair.Dark : pair.Light;
            }
            return color;
        }

        private static Color MapBackColor(Color color)
        {
            if (color.IsEmpty || color.A == 0)
                return color;
            var mapped = MapPaletteColor(color);
            if (mapped.ToArgb() != color.ToArgb())
                return mapped;
            if (_darkMode)
            {
                if (color.ToArgb() == SystemColors.Control.ToArgb()) return Background;
                if (color.ToArgb() == SystemColors.Window.ToArgb()) return CodeBackground;
                if (color.ToArgb() == Color.Black.ToArgb()) return Background;
            }
            return color;
        }

        private static Color MapForeColor(Color color)
        {
            if (color.IsEmpty || color.A == 0 || color.ToArgb() == Color.White.ToArgb())
                return color;
            var mapped = MapPaletteColor(color);
            if (mapped.ToArgb() != color.ToArgb())
                return mapped;
            if (_darkMode)
            {
                if (color.ToArgb() == SystemColors.ControlText.ToArgb()
                    || color.ToArgb() == SystemColors.WindowText.ToArgb()
                    || color.ToArgb() == Color.Black.ToArgb()) return TextPrimary;
                if (color.ToArgb() == SystemColors.GrayText.ToArgb()) return TextMuted;
            }
            return color;
        }

        public static void ApplyToControlTree(Control root)
        {
            if (root == null)
                return;
            ApplyControl(root);
        }

        private static void ApplyControl(Control control)
        {
            control.BackColor = MapBackColor(control.BackColor);
            control.ForeColor = MapForeColor(control.ForeColor);
            if (control is RoundedPanel rounded)
                rounded.BorderColor = MapPaletteColor(rounded.BorderColor);
            if (control is ModernButton button)
            {
                button.BackColorNormal = MapPaletteColor(button.BackColorNormal);
                button.BackColorHover = MapPaletteColor(button.BackColorHover);
                button.BackColorPressed = MapPaletteColor(button.BackColorPressed);
                if (!button.TextColor.IsEmpty)
                    button.TextColor = MapForeColor(button.TextColor);
            }
            if (control is ToolStrip strip)
                ApplyToolStripItems(strip.Items);
            foreach (Control child in control.Controls)
                ApplyControl(child);
            control.Invalidate();
        }

        private static void ApplyToolStripItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = MapBackColor(item.BackColor);
                item.ForeColor = MapForeColor(item.ForeColor);
                if (item is ToolStripDropDownItem dropDown)
                    ApplyToolStripItems(dropDown.DropDownItems);
            }
        }
    }

    // ===== BASE CONTROLS WITH DOUBLE BUFFERING =====

    public class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            ForeColor = ThemeColors.TextPrimary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.UserPaint | 
                     ControlStyles.DoubleBuffer | 
                     ControlStyles.ResizeRedraw | 
                     ControlStyles.OptimizedDoubleBuffer | 
                     ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
        }
    }

    public class DoubleBufferedListBox : ListBox
    {
        public DoubleBufferedListBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.DoubleBuffer | 
                     ControlStyles.ResizeRedraw | 
                     ControlStyles.OptimizedDoubleBuffer, true);
            this.DrawMode = DrawMode.OwnerDrawFixed;
            this.BorderStyle = BorderStyle.None;
            this.IntegralHeight = false;
        }
    }

    public class RoundedPanel : DoubleBufferedPanel
    {
        public int CornerRadius = 12;
        public Color BorderColor = ThemeColors.Border;
        public int BorderWidth = 1;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            rect.Inflate(-BorderWidth / 2, -BorderWidth / 2);
            
            using (var path = GetRoundedRectPath(rect, CornerRadius))
            using (var pen = new Pen(BorderColor, BorderWidth))
            {
                g.DrawPath(pen, path);
            }
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class ModernButton : Button
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BackColorNormal { get; set; } = ThemeColors.Primary;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BackColorHover { get; set; } = ThemeColors.PrimaryHover;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BackColorPressed { get; set; } = ThemeColors.PrimaryPressed;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color TextColor { get; set; } = Color.Empty;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CornerRadius { get; set; } = 0;
        private bool _hovered, _pressed;

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.UserPaint | 
                     ControlStyles.ResizeRedraw | 
                     ControlStyles.OptimizedDoubleBuffer | 
                     ControlStyles.SupportsTransparentBackColor | 
                     ControlStyles.Selectable, true);
            
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            FlatAppearance.CheckedBackColor = Color.Transparent;
            Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            ForeColor = ThemeColors.TextPrimary;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = 36;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            _pressed = Capture && (Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) == System.Windows.Forms.MouseButtons.Left;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == System.Windows.Forms.MouseButtons.Left && Enabled)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pressed = Enabled && Capture && (Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) == System.Windows.Forms.MouseButtons.Left
                          && ClientRectangle.Contains(e.Location);
            if (_pressed != pressed)
            {
                _pressed = pressed;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == System.Windows.Forms.MouseButtons.Left && _pressed)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _pressed)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                _hovered = false;
                _pressed = false;
            }
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && !_pressed)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && _pressed)
            {
                _pressed = false;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var graphicsState = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            rect.Inflate(-1, -1);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                g.Restore(graphicsState);
                return;
            }

            var color = !Enabled
                ? ThemeColors.BorderLight
                : _pressed ? BackColorPressed : _hovered ? BackColorHover : BackColorNormal;
            using (var path = GetRoundedRectPath(rect, CornerRadius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
                if (Enabled && GetReadableTextColor(color) == ThemeColors.TextPrimary)
                {
                    using var borderPen = new Pen(ThemeColors.Border, 1f);
                    g.DrawPath(borderPen, path);
                }
            }

            var textRect = Rectangle.Inflate(rect, -Math.Min(4, rect.Width / 2), -Math.Min(1, rect.Height / 2));
            var textColor = !Enabled
                ? ThemeColors.TextMuted
                : !TextColor.IsEmpty
                    ? TextColor
                    : GetReadableTextColor(color);
            if (textRect.Width > 0 && textRect.Height > 0)
            {
                TextRenderer.DrawText(g, Text ?? "", Font, textRect, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (Focused && ShowFocusCues)
            {
                var focusRect = Rectangle.Inflate(rect, -2, -2);
                if (focusRect.Width > 0 && focusRect.Height > 0)
                {
                    using var focusPath = GetRoundedRectPath(focusRect, Math.Max(0, CornerRadius - 2));
                    using var focusPen = new Pen(ThemeColors.Primary, 1.5f);
                    g.DrawPath(focusPen, focusPath);
                }
            }

            g.Restore(graphicsState);
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
                return path;

            var d = Math.Max(1, Math.Min(Math.Max(0, radius) * 2, Math.Min(rect.Width, rect.Height)));
            if (d <= 1)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color GetReadableTextColor(Color background)
        {
            var luminance = (0.2126f * background.R + 0.7152f * background.G + 0.0722f * background.B) / 255f;
            return luminance >= 0.62f ? ThemeColors.TextPrimary : Color.White;
        }
    }

    public class ModernTextBox : TextBox
    {
        private string _placeholderText = "";
        private bool _showPlaceholder = true;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new string PlaceholderText 
        { 
            get => _placeholderText; 
            set { _placeholderText = value; Invalidate(); } 
        }

        public ModernTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                     ControlStyles.DoubleBuffer | 
                     ControlStyles.ResizeRedraw | 
                     ControlStyles.OptimizedDoubleBuffer, true);
            
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = ThemeColors.CodeBackground;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 10f);
            Padding = new Padding(12, 10, 12, 10);
            Multiline = true;
            ScrollBars = ScrollBars.Vertical;
            
            this.Enter += (s, e) => { _showPlaceholder = false; Invalidate(); };
            this.Leave += (s, e) => { _showPlaceholder = string.IsNullOrEmpty(Text); Invalidate(); };
            this.TextChanged += (s, e) => { _showPlaceholder = string.IsNullOrEmpty(Text); Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            
            if (_showPlaceholder && !string.IsNullOrEmpty(_placeholderText))
            {
                var textRect = new Rectangle(rect.X + 14, rect.Y + 10, rect.Width - 28, rect.Height - 20);
                TextRenderer.DrawText(g, _placeholderText, Font, textRect, ThemeColors.TextMuted, 
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            using (var pen = new Pen(Focused ? ThemeColors.Primary : ThemeColors.Border, Focused ? 2 : 1))
            {
                var borderRect = rect;
                borderRect.Width--; borderRect.Height--;
                g.DrawRectangle(pen, borderRect);
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Invalidate();
        }
    }

    // ===== PANELS =====

    public class InputPanel : DoubleBufferedPanel
    {
        public event EventHandler? SendClicked;
        public event EventHandler? CancelClicked;
        public event EventHandler? MessageEditCancelled;

        private ModernTextBox _inputBox;
        private ModernButton _sendBtn;
        private ModernButton _attachBtn;
        private ModernButton _cancelEditBtn;
        private Label _modelLabel;
        private Label _attachmentsLabel;
        private string _normalModelLabel = "Aucun modèle sélectionné";
        private bool _goalModeEnabled = true;
        private bool _goalModePrefix;
        private bool _showGenerationSpeed;
        private double? _generationSpeed;
        private string _draftBeforeMessageEdit = "";
        private readonly List<ChatAttachment> _attachments = new();
        private List<ChatAttachment> _draftAttachmentsBeforeMessageEdit = new();
        private readonly ToolTip _attachmentToolTip = new();
        private bool _ready = false;
        private bool _modelSelected = false;
        private bool _isGenerating = false;
        private bool _isCancelling = false;
        private bool _isEditingMessage;
        private bool? _supportsMultimodal;
        private bool _canAnalyzeImagesWithOmni;

        public InputPanel()
        {
            Height = 210;
            BackColor = ThemeColors.Surface;
            Padding = new Padding(16, 9, 16, 9);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94F));

            _inputBox = new ModernTextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Font = new Font("Segoe UI", 10f),
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Écrivez votre message… (Entrée pour envoyer, Maj+Entrée pour une nouvelle ligne)"
            };
            _inputBox.TextChanged += (s, e) =>
            {
                _goalModePrefix = HasGoalModePrefix(_inputBox.Text);
                UpdateModelLabel();
            };
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    if (_ready && _modelSelected && !_isGenerating) SendClicked?.Invoke(this, EventArgs.Empty);
                }
            };
            layout.Controls.Add(_inputBox, 0, 0);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Padding = Padding.Empty
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 21F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 21F));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _attachmentsLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Aucune pièce jointe",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            _attachmentsLabel.Click += (s, e) => ClearAttachments();
            _attachmentsLabel.MouseEnter += (s, e) =>
            {
                if (_attachments.Count > 0)
                    _attachmentsLabel.ForeColor = ThemeColors.Primary;
            };
            _attachmentsLabel.MouseLeave += (s, e) => UpdateAttachmentLabel();
            footer.Controls.Add(_attachmentsLabel, 0, 0);
            footer.SetColumnSpan(_attachmentsLabel, 3);

            _modelLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Aucun modèle sélectionné",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                AutoEllipsis = true
            };
            footer.Controls.Add(_modelLabel, 0, 1);
            footer.SetColumnSpan(_modelLabel, 3);

            _attachBtn = new ModernButton
            {
                Text = "+ Joindre",
                Dock = DockStyle.Fill,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                BackColorPressed = ThemeColors.Border,
                Enabled = false,
                Margin = new Padding(0, 2, 6, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _attachBtn.Click += (s, e) => ChooseAttachments();
            footer.Controls.Add(_attachBtn, 0, 2);
            _attachmentToolTip.SetToolTip(_attachBtn,
                "Joindre une image, un audio ou une vidéo. Le modèle doit avoir un projecteur multimodal compatible.");

            _sendBtn = new ModernButton
            {
                Text = "Envoyer",
                Dock = DockStyle.Fill,
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
                Enabled = false,
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Height = 46
            };
            footer.Controls.Add(_sendBtn, 1, 2);
            footer.SetColumnSpan(_sendBtn, 2);
            _cancelEditBtn = new ModernButton
            {
                Text = "Annuler",
                Dock = DockStyle.Fill,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                BackColorPressed = ThemeColors.Border,
                Enabled = true,
                Visible = false,
                Margin = new Padding(6, 2, 0, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Height = 46
            };
            footer.Controls.Add(_cancelEditBtn, 2, 2);
            layout.Controls.Add(footer, 0, 1);
            _sendBtn.Click += (s, e) =>
            {
                if (_isGenerating)
                    CancelClicked?.Invoke(this, EventArgs.Empty);
                else
                    SendClicked?.Invoke(this, EventArgs.Empty);
            };
            _cancelEditBtn.Click += (s, e) => CancelMessageEdit();
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && _isEditingMessage)
                {
                    e.SuppressKeyPress = true;
                    CancelMessageEdit();
                }
            };

            Controls.Add(layout);
        }

        public void SetReady(bool ready)
        {
            _ready = ready;
            UpdateInteractionState();
        }

        public void SetGoalModeEnabled(bool enabled)
        {
            _goalModeEnabled = enabled;
            _goalModePrefix = HasGoalModePrefix(_inputBox.Text);
            UpdateModelLabel();
        }

        public void SetShowGenerationSpeed(bool show)
        {
            _showGenerationSpeed = show;
            if (!show)
                _generationSpeed = null;
            UpdateModelLabel();
        }

        public void SetGenerationSpeed(double tokensPerSecond)
        {
            if (!_showGenerationSpeed || !_isGenerating || !double.IsFinite(tokensPerSecond))
                return;
            _generationSpeed = tokensPerSecond;
            UpdateModelLabel();
        }

        public static bool HasGoalModePrefix(string text)
        {
            var candidate = text ?? "";
            var index = 0;
            while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
                index++;
            if (candidate.Length - index < 4 ||
                string.Compare(candidate, index, "goal", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            index += 4;
            while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
                index++;
            return index < candidate.Length && candidate[index] == ':';
        }

        public static bool TryStripGoalModePrefix(ref string text)
        {
            var candidate = text ?? "";
            var index = 0;
            while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
                index++;
            if (candidate.Length - index < 4 ||
                string.Compare(candidate, index, "goal", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            index += 4;
            while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
                index++;
            if (index >= candidate.Length || candidate[index] != ':')
                return false;
            index++;
            while (index < candidate.Length && char.IsWhiteSpace(candidate[index]))
                index++;
            text = candidate.Substring(index);
            return true;
        }

        public void SetModelSelected(bool selected, string modelName = "")
        {
            _modelSelected = selected;
            _supportsMultimodal = null;
            _canAnalyzeImagesWithOmni = false;
            if (selected)
            {
                _normalModelLabel = "[MODELE] " + modelName;
            }
            else
            {
                _normalModelLabel = "Aucun modele selectionne";
            }
            UpdateModelLabel();
            UpdateAttachmentLabel();
            UpdateInteractionState();
        }

        public string GetInputText() => _inputBox.Text.Trim();
        public List<ChatAttachment> GetAttachments() => _attachments.ToList();

        public void ClearInput()
        {
            _inputBox.Clear();
            ClearAttachments();
        }

        public void SetMultimodalSupport(bool? supported, bool? canAnalyzeImagesWithOmni = null)
        {
            _supportsMultimodal = supported;
            if (canAnalyzeImagesWithOmni.HasValue)
                _canAnalyzeImagesWithOmni = canAnalyzeImagesWithOmni.Value;
            UpdateAttachmentLabel();
            UpdateInteractionState();
        }

        public void BeginMessageEdit(string content, IEnumerable<ChatAttachment>? attachments = null)
        {
            if (!_isEditingMessage)
            {
                _draftBeforeMessageEdit = _inputBox.Text;
                _draftAttachmentsBeforeMessageEdit = _attachments.ToList();
            }
            _isEditingMessage = true;
            _inputBox.Text = content ?? "";
            _attachments.Clear();
            _attachments.AddRange(attachments ?? Enumerable.Empty<ChatAttachment>());
            UpdateAttachmentLabel();
            _inputBox.SelectionStart = _inputBox.TextLength;
            _inputBox.Focus();
            _cancelEditBtn.Visible = true;
            if (_sendBtn.Parent is TableLayoutPanel footer)
            {
                footer.SetColumnSpan(_sendBtn, 1);
                footer.PerformLayout();
            }
            UpdateModelLabel();
            UpdateInteractionState();
        }

        public void EndMessageEdit(bool restoreDraft)
        {
            if (!_isEditingMessage)
                return;
            if (restoreDraft)
            {
                _inputBox.Text = _draftBeforeMessageEdit;
                _attachments.Clear();
                _attachments.AddRange(_draftAttachmentsBeforeMessageEdit);
            }
            _draftBeforeMessageEdit = "";
            _draftAttachmentsBeforeMessageEdit.Clear();
            _isEditingMessage = false;
            UpdateAttachmentLabel();
            _cancelEditBtn.Visible = false;
            if (_sendBtn.Parent is TableLayoutPanel footer)
                footer.SetColumnSpan(_sendBtn, 2);
            UpdateModelLabel();
            UpdateInteractionState();
        }

        private void ChooseAttachments()
        {
            var imageOnly = _supportsMultimodal == false && _canAnalyzeImagesWithOmni;
            using var dialog = new OpenFileDialog
            {
                Title = imageOnly ? "Joindre une image pour l'analyse par le modèle Omni" : "Joindre des images, fichiers audio ou vidéos",
                Filter = imageOnly
                    ? "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Tous les fichiers|*.*"
                    : "Médias pris en charge|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.mp3;*.wav;*.flac;*.mp4;*.webm;*.mov;*.mkv;*.avi;*.mpeg;*.mpg|Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Audio|*.mp3;*.wav;*.flac|Vidéos|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.mpeg;*.mpg|Tous les fichiers|*.*",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                return;

            var totalBytes = _attachments.Sum(attachment =>
            {
                try { return new FileInfo(attachment.Path).Length; }
                catch { return 0L; }
            });
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    if (_attachments.Count >= ChatMediaStorage.MaximumAttachments)
                        throw new InvalidDataException($"Vous pouvez joindre au maximum {ChatMediaStorage.MaximumAttachments} fichiers.");
                    var size = new FileInfo(file).Length;
                    if (totalBytes + size > ChatMediaStorage.MaximumTotalBytes)
                        throw new InvalidDataException("La taille totale des pièces jointes ne peut pas dépasser 512 Mo.");
                    var attachment = ChatMediaStorage.Import(file);
                    _attachments.Add(attachment);
                    totalBytes += size;
                }
                catch (Exception exception)
                {
                    MessageBox.Show(FindForm(), exception.Message, "Pièce jointe refusée",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            UpdateAttachmentLabel();
        }

        private void ClearAttachments()
        {
            if (_attachments.Count == 0)
                return;
            _attachments.Clear();
            UpdateAttachmentLabel();
        }

        private void UpdateAttachmentLabel()
        {
            if (_attachments.Count == 0)
            {
                _attachmentsLabel.Text = _supportsMultimodal == false
                    ? _canAnalyzeImagesWithOmni
                        ? "Les images seront analysées par le modèle Omni défini dans les paramètres"
                        : "Ce modèle n’a pas de projecteur multimodal"
                    : "Aucune pièce jointe · images, audio et vidéo selon le modèle";
                _attachmentsLabel.ForeColor = _supportsMultimodal == false && !_canAnalyzeImagesWithOmni
                    ? ThemeColors.Warning
                    : ThemeColors.TextMuted;
                _attachmentsLabel.Cursor = Cursors.Default;
                return;
            }

            var names = string.Join(", ", _attachments.Select(item => item.Name));
            _attachmentsLabel.Text = $"{_attachments.Count} fichier(s) joint(s) : {names} · cliquer pour tout retirer";
            _attachmentsLabel.ForeColor = ThemeColors.TextSecondary;
            _attachmentsLabel.Cursor = Cursors.Hand;
        }

        private void CancelMessageEdit()
        {
            if (!_isEditingMessage)
                return;
            EndMessageEdit(restoreDraft: true);
            MessageEditCancelled?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateModelLabel()
        {
            string label;
            Color color;
            if (_goalModePrefix)
            {
                label = _goalModeEnabled
                    ? "Goal mode"
                    : "Goal mode indisponible en mode ultra léger";
                color = _goalModeEnabled ? ThemeColors.Primary : ThemeColors.Warning;
            }
            else
            {
                label = _isEditingMessage
                    ? "Modification d’un message — la suite sera remplacée · Échap pour annuler"
                    : _normalModelLabel;
                color = _isEditingMessage
                    ? ThemeColors.Primary
                    : _modelSelected ? ThemeColors.Success : ThemeColors.TextMuted;
            }

            if (_showGenerationSpeed && _isGenerating && _generationSpeed is double speed)
                label = _goalModePrefix
                    ? $"Goal mode · {speed:0.0} tokens/s"
                    : $"{speed:0.0} tokens/s · {label}";

            _modelLabel.Text = label;
            _modelLabel.ForeColor = color;
        }

        public void SetGenerating(bool generating)
        {
            _isGenerating = generating;
            if (!generating)
            {
                _isCancelling = false;
                _generationSpeed = null;
            }
            UpdateModelLabel();
            UpdateInteractionState();
        }

        public void SetCancelling(bool cancelling)
        {
            _isCancelling = cancelling;
            UpdateInteractionState();
        }

        private void UpdateInteractionState()
        {
            _sendBtn.Enabled = _ready && _modelSelected && !_isCancelling;
            _attachBtn.Enabled = _ready && _modelSelected && !_isGenerating && !_isCancelling &&
                (_supportsMultimodal != false || _canAnalyzeImagesWithOmni);
            _inputBox.Enabled = _ready && !_isGenerating;
            _sendBtn.Text = _isCancelling ? "Arrêt…" : _isGenerating ? "Annuler" : _isEditingMessage ? "Renvoyer" : "Envoyer";
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            _inputBox.BackColor = ThemeColors.CodeBackground;
            _inputBox.ForeColor = ThemeColors.TextPrimary;
            _attachmentsLabel.BackColor = ThemeColors.Surface;
            _modelLabel.BackColor = ThemeColors.Surface;
        }
    }

    public class SidebarPanel : RoundedPanel
    {
        public event EventHandler<string> ModelsFolderChanged;
        public event EventHandler<string> ModelSelected;
        public event EventHandler<string> ConversationSelected;
        public event EventHandler NewConversationRequested;
        public event EventHandler<string> DeleteConversationRequested;

        private TextBox _folderTextBox;
        private ModernButton _browseBtn;
        private ModernButton _newConvBtn;
        private DoubleBufferedListBox _modelsListBox;
        private DoubleBufferedListBox _conversationsListBox;
        private Label _modelsTitle;
        private Label _convsTitle;
        private Panel _modelsLoadingPanel;
        private string _conversationsDirectory;
        private string _selectedModelPath;
        private string? _selectedConvFile;
        private bool _suppressModelSelection = false;
        private readonly Dictionary<string, string> _conversationTitlePreviews = new(StringComparer.OrdinalIgnoreCase);

        public SidebarPanel()
        {
            CornerRadius = 12;
            BorderColor = ThemeColors.Border;
            BorderWidth = 1;
            BackColor = ThemeColors.Surface;
            Padding = new Padding(0);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            // Folder selector
            var folderPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 8) };
            var folderLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));

            _folderTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(8, 6, 8, 6),
                ReadOnly = true
            };
            folderLayout.Controls.Add(_folderTextBox, 0, 0);

            _browseBtn = new ModernButton
            {
                Text = "Parcourir",
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 4, 0),
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                BackColorPressed = ThemeColors.Border,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _browseBtn.Click += (s, e) => BrowseFolder();
            folderLayout.Controls.Add(_browseBtn, 1, 0);

            folderPanel.Controls.Add(folderLayout);
            layout.Controls.Add(folderPanel, 0, 0);

            _modelsTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "MODELES",
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4),
                AutoSize = false
            };
            layout.Controls.Add(_modelsTitle, 0, 1);

            var modelsContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 0) };
            var modelsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            modelsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            modelsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));

            _modelsListBox = new DoubleBufferedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.One,
                ItemHeight = 72,
                Margin = new Padding(0)
            };
            _modelsListBox.DrawItem += DrawModelItem;
            _modelsListBox.SelectedIndexChanged += (s, e) =>
            {
                if (!_suppressModelSelection && _modelsListBox.SelectedItem is LocalModelInfo model)
                {
                    _selectedModelPath = model.Path;
                    ModelSelected?.Invoke(this, model.Path);
                }
            };
            modelsLayout.Controls.Add(_modelsListBox, 0, 0);

            _modelsLoadingPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Background,
                Visible = false
            };
            var loadingLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Scan en cours...",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _modelsLoadingPanel.Controls.Add(loadingLabel);
            modelsLayout.Controls.Add(_modelsLoadingPanel, 0, 0);
            modelsLayout.SetRowSpan(_modelsLoadingPanel, 2);

            modelsContainer.Controls.Add(modelsLayout);
            layout.Controls.Add(modelsContainer, 0, 2);

            _convsTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "CONVERSATIONS",
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4),
                AutoSize = false
            };
            layout.Controls.Add(_convsTitle, 0, 3);

            _conversationsListBox = new DoubleBufferedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.One,
                ItemHeight = 82,
                Margin = new Padding(4, 0, 4, 0)
            };
            _conversationsListBox.DrawItem += DrawConversationItem;
            _conversationsListBox.MouseDoubleClick += (s, e) =>
            {
                var idx = _conversationsListBox.IndexFromPoint(e.Location);
                if (idx >= 0 && _conversationsListBox.Items[idx] is string filename)
                {
                    _selectedConvFile = filename;
                    ConversationSelected?.Invoke(this, filename);
                }
            };
            _conversationsListBox.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var idx = _conversationsListBox.IndexFromPoint(e.Location);
                    if (idx >= 0)
                    {
                        _conversationsListBox.SelectedIndex = idx;
                        ShowConvContextMenu(e.Location);
                    }
                }
            };
            layout.Controls.Add(_conversationsListBox, 0, 4);

            _newConvBtn = new ModernButton
            {
                Text = "+ Nouvelle conversation",
                Dock = DockStyle.Fill,
                Margin = new Padding(4),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
            };
            _newConvBtn.Click += (s, e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_newConvBtn, 0, 5);

            Controls.Add(layout);
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Selectionnez le dossier contenant vos modeles IA",
                ShowNewFolderButton = true,
                SelectedPath = _folderTextBox.Text,
                UseDescriptionForTitle = true
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _folderTextBox.Text = dialog.SelectedPath;
                    ModelsFolderChanged?.Invoke(this, dialog.SelectedPath);
                }
            }
        }

        public void SetModelsFolder(string folder)
        {
            _folderTextBox.Text = folder ?? "";
        }

        public void SetModelsLoading(bool loading)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetModelsLoading(loading)));
                return;
            }
            _modelsLoadingPanel.Visible = loading;
            _modelsListBox.Visible = !loading;
            if (loading) _modelsLoadingPanel.BringToFront();
            else _modelsListBox.BringToFront();
        }

        public void LoadModels(List<LocalModelInfo> models, string? preferredModelPath = null)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => LoadModels(models, preferredModelPath)));
                return;
            }

            _modelsListBox.BeginUpdate();
            try
            {
                _suppressModelSelection = true;
                _modelsListBox.Items.Clear();
                if (models.Count > 0)
                    _modelsListBox.Items.AddRange(models.Cast<object>().ToArray());

                var selectedModel = models.FirstOrDefault(model =>
                    model.Type == ModelType.GGUF &&
                    !string.IsNullOrWhiteSpace(preferredModelPath) &&
                    string.Equals(model.Path, preferredModelPath, StringComparison.OrdinalIgnoreCase));
                if (selectedModel == null)
                    _selectedModelPath = "";
                else
                {
                    _modelsListBox.SelectedItem = selectedModel;
                    _selectedModelPath = selectedModel.Path;
                }
            }
            finally
            {
                _suppressModelSelection = false;
                _modelsListBox.EndUpdate();
            }

            _modelsListBox.Invalidate();
            if (_modelsListBox.SelectedItem is LocalModelInfo selected)
                ModelSelected?.Invoke(this, selected.Path);
        }

        public void SelectModel(string modelPath)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SelectModel(modelPath)));
                return;
            }

            for (var i = 0; i < _modelsListBox.Items.Count; i++)
            {
                if (_modelsListBox.Items[i] is not LocalModelInfo model ||
                    !string.Equals(model.Path, modelPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                _suppressModelSelection = true;
                try
                {
                    _modelsListBox.SelectedIndex = i;
                    _selectedModelPath = model.Path;
                }
                finally
                {
                    _suppressModelSelection = false;
                }
                return;
            }
        }

        public void SetConversationsDirectory(string directory)
        {
            _conversationsDirectory = directory;
            _conversationsListBox?.Invalidate();
        }

        public void LoadConversations(string[] files)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => LoadConversations(files)));
                return;
            }
            _conversationsListBox.BeginUpdate();
            _conversationsListBox.Items.Clear();
            if (files.Length > 0)
                _conversationsListBox.Items.AddRange(files.Cast<object>().ToArray());
            _conversationsListBox.EndUpdate();
            _conversationsListBox.Invalidate(); // Force OwnerDraw repaint
        }

        public void SetConversationTitlePreview(string filename, string title)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetConversationTitlePreview(filename, title)));
                return;
            }

            if (string.IsNullOrWhiteSpace(filename))
                return;

            if (string.IsNullOrWhiteSpace(title))
                _conversationTitlePreviews.Remove(filename);
            else
                _conversationTitlePreviews[filename] = title;
            _conversationsListBox.Invalidate();
        }

        public void SelectConversation(string? filename)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SelectConversation(filename)));
                return;
            }
            _selectedConvFile = filename;
            if (filename == null)
            {
                _conversationsListBox.ClearSelected();
            }
            else
            {
                for (int i = 0; i < _conversationsListBox.Items.Count; i++)
                {
                    if (_conversationsListBox.Items[i].ToString() == filename)
                    {
                        _conversationsListBox.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void DrawModelItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var model = _modelsListBox.Items[e.Index] as LocalModelInfo;
            if (model == null) return;
            
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = e.Bounds;
            rect.Inflate(-4, -2);
            
            var backColor = selected ? ThemeColors.Primary : ThemeColors.Surface;
            using (var path = GetRoundedRectPath(rect, 8))
            using (var brush = new SolidBrush(backColor))
            using (var pen = new Pen(selected ? ThemeColors.Primary : ThemeColors.Border, selected ? 1.5f : 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var metaFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var pathFont = new Font("Segoe UI", 9.5f);
            var typeText = model.TypeLabel + "  *  " + model.SizeFormatted;
            if (!string.IsNullOrEmpty(model.SubTypeLabel))
                typeText = model.SubTypeLabel + "  *  " + model.SizeFormatted;
            
            TextRenderer.DrawText(g, model.TypeIcon + "  " + model.Name, titleFont,
                new Rectangle(rect.X + 12, rect.Y + 4, rect.Width - 24, 22),
                selected ? Color.White : ThemeColors.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, typeText, metaFont,
                new Rectangle(rect.X + 12, rect.Y + 26, rect.Width - 24, 18),
                selected ? Color.White : ThemeColors.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            var pathText = model.Folder ?? model.Path;
            if (pathText.Length > 60) pathText = "..." + pathText.Substring(pathText.Length - 57);
            
            TextRenderer.DrawText(g, pathText, pathFont,
                new Rectangle(rect.X + 12, rect.Y + 44, rect.Width - 24, 18),
                selected ? Color.White : ThemeColors.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // Draw focus rectangle
            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus && (e.State & DrawItemState.NoFocusRect) != DrawItemState.NoFocusRect)
            {
                using (var pen = new Pen(selected ? Color.White : ThemeColors.Primary, 1))
                {
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
            }
        }

        private void DrawConversationItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var filename = _conversationsListBox.Items[e.Index].ToString();
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = e.Bounds;
            rect.Inflate(-4, -2);
            
            var backColor = selected ? ThemeColors.Primary : ThemeColors.Surface;
            using (var path = GetRoundedRectPath(rect, 8))
            using (var brush = new SolidBrush(backColor))
            using (var pen = new Pen(selected ? ThemeColors.Primary : ThemeColors.Border, selected ? 1.5f : 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            var convPath = Path.Combine(_conversationsDirectory ?? "", filename);
            
            string preview = "";
            string timeStr = "";
            ConversationData data = null;
            try
            {
                if (File.Exists(convPath))
                {
                    var json = File.ReadAllText(convPath);
                    data = JsonConvert.DeserializeObject<ConversationData>(json);
                    if (data?.Messages != null && data.Messages.Count > 0)
                    {
                        preview = GetConversationPreview(data.Messages.LastOrDefault()?.Content ?? "");
                        if (preview.Length > 100) preview = preview.Substring(0, 97).TrimEnd() + "...";
                    }
                    timeStr = File.GetLastWriteTime(convPath).ToString("dd/MM HH:mm");
                }
            }
            catch { }

            var name = _conversationTitlePreviews.TryGetValue(filename, out var titlePreview)
                ? titlePreview
                : data?.Title ?? Path.GetFileNameWithoutExtension(filename).Replace("conversation_", "");
            using var titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            using var previewFont = new Font("Segoe UI", 9.5f);
            using var timeFont = new Font("Segoe UI", 9.5f);
            var contentX = rect.X + 12;
            var contentWidth = rect.Width - 24;
            var titleY = rect.Y + 5;
            const int titleHeight = 22;
            const int lineGap = 3;
            const int timeHeight = 16;
            var timeY = rect.Bottom - timeHeight - 4;
            var previewY = titleY + titleHeight + lineGap;
            var previewHeight = Math.Max(16, timeY - previewY - lineGap);
            var singleLine = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                             TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, name, titleFont,
                new Rectangle(contentX, titleY, contentWidth, titleHeight),
                selected ? Color.White : ThemeColors.TextPrimary, singleLine);

            if (!string.IsNullOrEmpty(preview))
            {
                TextRenderer.DrawText(g, preview, previewFont,
                    new Rectangle(contentX, previewY, contentWidth, previewHeight),
                    selected ? Color.White : ThemeColors.TextSecondary,
                    singleLine);
            }

            if (!string.IsNullOrEmpty(timeStr))
            {
                TextRenderer.DrawText(g, timeStr, timeFont,
                    new Rectangle(contentX, timeY, contentWidth, timeHeight),
                    selected ? Color.White : ThemeColors.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            // Draw focus rectangle
            if ((e.State & DrawItemState.Focus) == DrawItemState.Focus && (e.State & DrawItemState.NoFocusRect) != DrawItemState.NoFocusRect)
            {
                using (var pen = new Pen(selected ? Color.White : ThemeColors.Primary, 1))
                {
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                }
            }
        }

        private static string GetConversationPreview(string content)
        {
            content ??= "";
            var preview = new StringBuilder();
            var position = 0;
            while (position < content.Length)
            {
                var thinkingStart = content.IndexOf("<think>", position, StringComparison.Ordinal);
                if (thinkingStart < 0)
                {
                    preview.Append(content, position, content.Length - position);
                    break;
                }

                preview.Append(content, position, thinkingStart - position);
                var thinkingEnd = content.IndexOf("</think>", thinkingStart + 7, StringComparison.Ordinal);
                if (thinkingEnd < 0)
                    break;
                position = thinkingEnd + 8;
            }

            var text = preview.ToString()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Replace("```", "");
            while (text.Contains("  ", StringComparison.Ordinal))
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            return text.Trim();
        }

        private void ShowConvContextMenu(Point location)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = ThemeColors.Surface,
                ForeColor = ThemeColors.TextPrimary,
                Renderer = new ModernContextMenuRenderer()
            };
            
            var loadItem = new ToolStripMenuItem("Charger", null, (s, e) => 
            {
                if (_conversationsListBox.SelectedItem is string f) ConversationSelected?.Invoke(this, f);
            });
            loadItem.ForeColor = ThemeColors.TextPrimary;
            
            var deleteItem = new ToolStripMenuItem("Supprimer", null, (s, e) => 
            {
                if (_conversationsListBox.SelectedItem is string f) DeleteConversationRequested?.Invoke(this, f);
            });
            deleteItem.ForeColor = ThemeColors.Error;

            menu.Items.Add(loadItem);
            menu.Items.Add(new ToolStripSeparator { ForeColor = ThemeColors.Border });
            menu.Items.Add(deleteItem);
            
            menu.Show(_conversationsListBox, location);
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            BorderColor = ThemeColors.Border;
            _folderTextBox.BackColor = ThemeColors.Background;
            _folderTextBox.ForeColor = ThemeColors.TextPrimary;
            _modelsListBox.BackColor = ThemeColors.Background;
            _modelsListBox.ForeColor = ThemeColors.TextPrimary;
            _conversationsListBox.BackColor = ThemeColors.Background;
            _conversationsListBox.ForeColor = ThemeColors.TextPrimary;
            _modelsTitle.ForeColor = ThemeColors.TextPrimary;
            _convsTitle.ForeColor = ThemeColors.TextPrimary;
            _modelsLoadingPanel.BackColor = ThemeColors.Background;
            Invalidate();
        }
    }

    public class ModernMenuStrip : MenuStrip
    {
        public event EventHandler NewConversationClicked;
        public event EventHandler OpenFolderClicked;
        public event EventHandler SetModelsFolderClicked;
        public event EventHandler? RefreshModelsClicked;
        public event EventHandler SearchKnownModelsClicked;
        public event EventHandler SearchAllModelsClicked;
        public event EventHandler CancelModelSearchClicked;
        public event EventHandler RestartBackendClicked;
        public event EventHandler ExitClicked;
        public event EventHandler SettingsClicked;
        public event EventHandler ImageGeneratorClicked;
        public event EventHandler? ForumClicked;
        public event EventHandler? SubagentActivityClicked;
        public event EventHandler CheckForUpdatesClicked;

        public ModernMenuStrip()
        {
            BackColor = ThemeColors.Surface;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 9.5f);
            Padding = new Padding(8, 4, 8, 4);
            Renderer = new ModernMenuRenderer();

            var fileMenu = new ToolStripMenuItem("Fichier") { ForeColor = ThemeColors.TextPrimary };
            fileMenu.DropDownItems.Add(CreateItem("Nouvelle conversation", "Ctrl+N", (s, e) => NewConversationClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Ouvrir dossier conversations", null, (s, e) => OpenFolderClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Definir dossier modeles...", null, (s, e) => SetModelsFolderClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Actualiser les modèles", null, (s, e) => RefreshModelsClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Rechercher les modèles (rapide)", null, (s, e) => SearchKnownModelsClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Rechercher les modèles sur tout le PC…", null, (s, e) => SearchAllModelsClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(CreateItem("Annuler la recherche des modèles", null, (s, e) => CancelModelSearchClicked?.Invoke(this, e)));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(CreateItem("Quitter", "Alt+F4", (s, e) => ExitClicked?.Invoke(this, e)));

            var toolsMenu = new ToolStripMenuItem("Outils") { ForeColor = ThemeColors.TextPrimary };
            toolsMenu.DropDownItems.Add(CreateItem("Redemarrer le backend IA", null, (s, e) => RestartBackendClicked?.Invoke(this, e)));
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(CreateItem("Générer une image…", null, (s, e) => ImageGeneratorClicked?.Invoke(this, e)));
            toolsMenu.DropDownItems.Add(CreateItem("Forum des IA…", null, (s, e) => ForumClicked?.Invoke(this, e)));
            toolsMenu.DropDownItems.Add(CreateItem("Suivi des tâches IA…", null, (s, e) => SubagentActivityClicked?.Invoke(this, e)));
            toolsMenu.DropDownItems.Add(CreateItem("Parametres...", "Ctrl+P", (s, e) => SettingsClicked?.Invoke(this, e)));
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(CreateItem("Vérifier les mises à jour…", null, (s, e) => CheckForUpdatesClicked?.Invoke(this, e)));

            Items.Add(fileMenu);
            Items.Add(toolsMenu);
        }

        private ToolStripMenuItem CreateItem(string text, string shortcut, EventHandler handler)
        {
            var item = new ToolStripMenuItem(text) { ForeColor = ThemeColors.TextPrimary };
            if (!string.IsNullOrEmpty(shortcut)) item.ShortcutKeys = ParseShortcut(shortcut);
            item.Click += handler;
            return item;
        }

        private Keys ParseShortcut(string shortcut)
        {
            var parts = shortcut.Split('+');
            Keys keys = Keys.None;
            foreach (var p in parts)
            {
                if (p == "Ctrl") keys |= Keys.Control;
                else if (p == "Alt") keys |= Keys.Alt;
                else if (p == "Shift") keys |= Keys.Shift;
                else keys |= (Keys)Enum.Parse(typeof(Keys), p);
            }
            return keys;
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            ForeColor = ThemeColors.TextPrimary;
            foreach (ToolStripItem item in Items)
                item.ForeColor = ThemeColors.TextPrimary;
        }
    }

    public class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernMenuRenderer() : base(new ModernColorTable()) { }
    }

    public class ModernColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => ThemeColors.Surface;
        public override Color MenuStripGradientEnd => ThemeColors.Surface;
        public override Color ToolStripDropDownBackground => ThemeColors.Surface;
        public override Color ImageMarginGradientBegin => ThemeColors.Background;
        public override Color ImageMarginGradientMiddle => ThemeColors.Background;
        public override Color ImageMarginGradientEnd => ThemeColors.Background;
        public override Color MenuItemSelected => ThemeColors.Primary;
        public override Color MenuItemSelectedGradientBegin => ThemeColors.Primary;
        public override Color MenuItemSelectedGradientEnd => ThemeColors.Primary;
        public override Color MenuItemBorder => ThemeColors.Border;
        public override Color SeparatorDark => ThemeColors.Border;
    }

    public class ModernContextMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernContextMenuRenderer() : base(new ModernColorTable()) { }
    }

    public class StatusBar : StatusStrip
    {
        private ToolStripStatusLabel _statusLabel;
        private ToolStripStatusLabel _modelLabel;
        private ToolStripProgressBar _progressBar;

        public StatusBar()
        {
            BackColor = ThemeColors.Surface;
            ForeColor = ThemeColors.TextSecondary;
            Font = new Font("Segoe UI", 9.5f);
            SizingGrip = false;
            Renderer = new ModernStatusRenderer();

            _statusLabel = new ToolStripStatusLabel
            {
                Text = "Initialisation...",
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeColors.TextSecondary
            };

            _modelLabel = new ToolStripStatusLabel
            {
                Text = "Aucun modele",
                ForeColor = ThemeColors.TextMuted,
                AutoSize = true
            };

            _progressBar = new ToolStripProgressBar
            {
                Visible = false,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Size = new Size(100, 16)
            };

            Items.Add(_statusLabel);
            Items.Add(_progressBar);
            Items.Add(_modelLabel);
        }

        public void SetStatus(string text, StatusType type)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = type switch
            {
                StatusType.Success => ThemeColors.Success,
                StatusType.Warning => ThemeColors.Warning,
                StatusType.Error => ThemeColors.Error,
                _ => ThemeColors.TextSecondary
            };
        }

        public void SetModel(string model)
        {
            _modelLabel.Text = model;
        }

        public void ShowProgress(bool show)
        {
            _progressBar.Visible = show;
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            ForeColor = ThemeColors.TextSecondary;
            _statusLabel.ForeColor = ThemeColors.TextSecondary;
            _modelLabel.ForeColor = ThemeColors.TextMuted;
        }
    }

    public class ModernStatusRenderer : ToolStripProfessionalRenderer
    {
        public ModernStatusRenderer() : base(new ModernColorTable()) { }
    }

    // ===== SETTINGS FORM =====
    public class SettingsForm : Form
    {
        public AppSettings Settings { get; private set; }
        private CheckBox _ultraLiteModeCheckBox = null!;
        private ModernTextBox _systemPromptTextBox;
        private ModernTextBox _conversationTitlePromptTextBox = null!;
        private ModernTextBox _defaultTextModelPathTextBox = null!;
        private ModernTextBox _defaultImageModelPathTextBox = null!;
        private ModernTextBox _defaultOmniModelPathTextBox = null!;
        private CheckBox _keepChatModelLoadedForOmniCheckBox = null!;
        private CheckBox _useDedicatedConversationTitleModelCheckBox = null!;
        private ModernTextBox _conversationTitleModelPathTextBox = null!;
        private Control _conversationTitleModelPicker = null!;
        private CheckBox _webAccessCheckBox = null!;
        private CheckBox _codeExecutionCheckBox = null!;
        private CheckBox _systemCommandCheckBox = null!;
        private CheckBox _forumEnabledCheckBox = null!;
        private CheckBox _subagentsEnabledCheckBox = null!;
        private CheckBox _autoUpdateCheckBox = null!;
        private CheckBox _searchAllModelDrivesCheckBox = null!;
        private CheckBox _darkModeCheckBox = null!;
        private CheckBox _showGenerationSpeedCheckBox = null!;
        private CheckBox _ssdModelMemoryCheckBox = null!;
        private CheckBox _imageDiskMemoryCheckBox = null!;
        private ModernNumericUpDown _imageMemoryLimitUpDown = null!;
        private ModernNumericUpDown _imageMinimumFreeDiskUpDown = null!;
        private ModernTextBox _codeWorkspaceTextBox = null!;
        private ComboBox _reasoningEffortComboBox = null!;
        private ModernNumericUpDown _temperatureUpDown;
        private ModernNumericUpDown _topPUpDown;
        private ModernNumericUpDown _topKUpDown;
        private ModernNumericUpDown _minPUpDown;
        private ModernNumericUpDown _maxTokensUpDown;
        private ModernNumericUpDown _repeatPenaltyUpDown;
        private ModernNumericUpDown _repeatLastNUpDown;
        private ModernNumericUpDown _presencePenaltyUpDown;
        private ModernNumericUpDown _frequencyPenaltyUpDown;
        private ModernNumericUpDown _seedUpDown;
        private ModernNumericUpDown _contextSizeUpDown;
        private ModernNumericUpDown _nglUpDown;
        private ModernNumericUpDown _threadsUpDown;
        private ModernNumericUpDown _generationTimeoutUpDown = null!;
        private ModernNumericUpDown _codeExecutionTimeoutUpDown = null!;
        private ModernTextBox _stopSequencesTextBox;
        private ModernButton _btnSave;
        private ModernButton _btnCancel;
        private ModernButton _btnDefaults = null!;

        public SettingsForm(AppSettings currentSettings)
        {
            Settings = new AppSettings
            {
                SystemPrompt = currentSettings.SystemPrompt ?? "",
                ConversationTitlePrompt = currentSettings.ConversationTitlePrompt ?? AppSettings.DefaultConversationTitlePrompt,
                DefaultTextModelPath = currentSettings.DefaultTextModelPath ?? "",
                DefaultImageModelPath = currentSettings.DefaultImageModelPath ?? "",
                DefaultOmniModelPath = currentSettings.DefaultOmniModelPath ?? "",
                KeepChatModelLoadedForOmni = currentSettings.KeepChatModelLoadedForOmni,
                UseDedicatedConversationTitleModel = currentSettings.UseDedicatedConversationTitleModel,
                ConversationTitleModelPath = currentSettings.ConversationTitleModelPath ?? "",
                UltraLiteMode = currentSettings.UltraLiteMode,
                SearchAllDrivesForModels = currentSettings.SearchAllDrivesForModels,
                EnableWebAccess = currentSettings.EnableWebAccess,
                EnableCodeExecution = currentSettings.EnableCodeExecution,
                EnableSystemCommands = currentSettings.EnableSystemCommands,
                UseDiskForImageMemory = currentSettings.UseDiskForImageMemory,
                ImageEngineMemoryLimitGb = currentSettings.ImageEngineMemoryLimitGb,
                ImageMinimumFreeDiskGb = currentSettings.ImageMinimumFreeDiskGb,
                EnableForum = currentSettings.EnableForum,
                EnableSubagents = currentSettings.EnableSubagents,
                AutoUpdateEnabled = currentSettings.AutoUpdateEnabled,
                DarkMode = currentSettings.DarkMode,
                ShowGenerationSpeed = currentSettings.ShowGenerationSpeed,
                CodeWorkspace = string.IsNullOrWhiteSpace(currentSettings.CodeWorkspace)
                    ? AppSettings.DefaultCodeWorkspace
                    : currentSettings.CodeWorkspace,
                Temperature = currentSettings.Temperature,
                TopP = currentSettings.TopP,
                TopK = currentSettings.TopK,
                MinP = currentSettings.MinP,
                MaxTokens = currentSettings.MaxTokens,
                RepeatPenalty = currentSettings.RepeatPenalty,
                RepeatLastN = currentSettings.RepeatLastN,
                PresencePenalty = currentSettings.PresencePenalty,
                FrequencyPenalty = currentSettings.FrequencyPenalty,
                Seed = currentSettings.Seed,
                StopSequences = currentSettings.StopSequences ?? "",
                ContextSize = currentSettings.ContextSize,
                Ngl = currentSettings.Ngl,
                Threads = currentSettings.Threads,
                UseMmapForModel = currentSettings.UseMmapForModel,
                MemoryOptionsInitialized = currentSettings.MemoryOptionsInitialized,
                ReasoningEffort = currentSettings.ReasoningEffort ?? "default",
                GenerationTimeoutSeconds = Math.Clamp(currentSettings.GenerationTimeoutSeconds, 30, 7200),
                CodeExecutionTimeoutSeconds = Math.Clamp(currentSettings.CodeExecutionTimeoutSeconds, 1, 7200)
            };

            InitializeComponent();
            ThemeColors.ApplyToControlTree(this);
            LoadSettings();
        }

        private void InitializeComponent()
        {
            this.Text = "Parametres";
            this.ClientSize = new Size(680, 680);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimumSize = new Size(620, 600);
            this.BackColor = ThemeColors.Background;
            this.ForeColor = ThemeColors.TextPrimary;
            this.Font = new Font("Segoe UI", 10f);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66f));

            var scrollPanel = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(14),
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            var content = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Width = 590,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };

            AddSection(content, "APPARENCE");
            _darkModeCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mode sombre",
                "Utilise une palette sombre dans le chat, les panneaux et les fenêtres de l’application.",
                _darkModeCheckBox));

            AddSection(content, "MODÈLES PAR DÉFAUT");
            _defaultTextModelPathTextBox = CreateModelPathTextBox();
            content.Controls.Add(CreateSettingCard(
                "Modèle de texte par défaut",
                "Modèle GGUF sélectionné au démarrage. Si le champ est vide ou que le fichier manque, l'application choisit le premier GGUF détecté.",
                CreateModelPathPicker(_defaultTextModelPathTextBox, BrowseDefaultTextModel),
                editorBelowTitle: true,
                height: 112));

            _useDedicatedConversationTitleModelCheckBox = new CheckBox
            {
                Text = "Activer le modèle dédié",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Modèle de nommage des conversations",
                "Désactivé par défaut : le modèle du chat crée le titre. Activé : le modèle choisi ci-dessous crée le titre avant la réponse, puis le modèle du chat est rechargé pour répondre.",
                _useDedicatedConversationTitleModelCheckBox,
                editorBelowTitle: true,
                height: 108));

            _conversationTitleModelPathTextBox = CreateModelPathTextBox();
            _conversationTitleModelPicker = CreateModelPathPicker(
                _conversationTitleModelPathTextBox, BrowseConversationTitleModel);
            _conversationTitleModelPicker.Enabled = false;
            _useDedicatedConversationTitleModelCheckBox.CheckedChanged += (_, _) =>
                _conversationTitleModelPicker.Enabled = _useDedicatedConversationTitleModelCheckBox.Checked;
            content.Controls.Add(CreateSettingCard(
                "Modèle GGUF dédié",
                "Choisis un modèle local pour générer les titres. Si le fichier est indisponible, l'application reprend le modèle du chat.",
                _conversationTitleModelPicker,
                editorBelowTitle: true,
                height: 112));

            _defaultImageModelPathTextBox = CreateModelPathTextBox();
            content.Controls.Add(CreateSettingCard(
                "Modèle d'image par défaut",
                "Fichier principal .safetensors, .gguf ou .ckpt. Place-le sous checkpoints ou diffusion_models; le profil compatible sera choisi à l'ouverture.",
                CreateModelPathPicker(_defaultImageModelPathTextBox, BrowseDefaultImageModel),
                editorBelowTitle: true,
                height: 112));

            _defaultOmniModelPathTextBox = CreateModelPathTextBox();
            content.Controls.Add(CreateSettingCard(
                "Modèle Omni pour analyser les images",
                "Modèle GGUF vision avec un fichier mmproj compatible dans le même dossier. Les modèles de chat sans mmproj, y compris ceux orientés code, peuvent appeler analyze_image.",
                CreateModelPathPicker(_defaultOmniModelPathTextBox, BrowseDefaultOmniModel),
                editorBelowTitle: true,
                height: 128));

            _keepChatModelLoadedForOmniCheckBox = new CheckBox
            {
                Text = "Garder les deux modèles chargés en même temps",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mémoire pendant l'analyse Omni",
                "Désactivé : le modèle du chat est déchargé puis rechargé après l'analyse. Activé : le modèle Omni est chargé sur un second serveur, ce qui garde les deux modèles en mémoire et demande davantage de RAM/VRAM.",
                _keepChatModelLoadedForOmniCheckBox,
                editorBelowTitle: true,
                height: 104));

            AddSection(content, "RECHERCHE DES MODÈLES");
            _searchAllModelDrivesCheckBox = new CheckBox
            {
                Text = "Recherche complète",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Parcourir tous les lecteurs au démarrage",
                "Désactivé par défaut : le scan rapide consulte le dossier choisi ainsi que les emplacements courants de LM Studio, Jan, GPT4All et d'autres outils. Active cette option pour lancer tout de suite puis au démarrage une recherche depuis la racine des lecteurs fixes; cela peut prendre plusieurs minutes. Le scan s'effectue en arrière-plan et peut être annulé dans Fichier.",
                _searchAllModelDrivesCheckBox));

            AddSection(content, "MISES À JOUR");
            _autoUpdateCheckBox = new CheckBox
            {
                Text = "Au démarrage",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Vérifier les mises à jour automatiquement",
                "Vérifie au démarrage et affiche les notes avant tout téléchargement. « Ne plus me proposer » coupe les alertes; tu peux les réactiver ici. La recherche manuelle reste disponible dans Outils.",
                _autoUpdateCheckBox,
                height: 88));

            AddSection(content, "INSTRUCTIONS ET MÉMOIRE");
            _ultraLiteModeCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mode ultra léger",
                "Désactivé par défaut. Pour les contextes très courts (par exemple 128 tokens), envoie au modèle uniquement les messages de la conversation et la demande actuelle. Le prompt système, le prompt de nommage et les consignes d’outils ne sont pas envoyés; les outils et le nommage automatique sont désactivés pendant la requête. Tous ces réglages restent enregistrés et reprennent effet lorsque ce mode est désactivé.",
                _ultraLiteModeCheckBox));

            _conversationTitlePromptTextBox = new ModernTextBox
            {
                Multiline = true,
                Height = 80,
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Consignes pour générer le nom de la conversation..."
            };
            content.Controls.Add(CreateSettingCard(
                "Prompt de nommage des conversations",
                "Consignes utilisées pour créer un titre court. Ce texte sert uniquement au titre, pas aux réponses du chat.",
                _conversationTitlePromptTextBox,
                editorBelowTitle: true,
                height: 148));

            _systemPromptTextBox = new ModernTextBox
            {
                Multiline = true,
                Height = 90,
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Laisser vide pour ne pas ajouter de consigne système..."
            };
            content.Controls.Add(CreateSettingCard(
                "Prompt système",
                "Consignes fixes données au modèle : rôle, ton, langue et règles à suivre.",
                _systemPromptTextBox,
                editorBelowTitle: true,
                height: 158));

            AddSection(content, "OUTILS ET ACCÈS");
            _webAccessCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Recherche DuckDuckGo et pages web",
                "Autorise le modèle à chercher sur DuckDuckGo et à lire des pages publiques quand il en a besoin.",
                _webAccessCheckBox));

            _codeExecutionCheckBox = new CheckBox
            {
                Text = "Autoriser",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Écriture de fichiers et exécution Python",
                "Autorise les outils de fichiers dans le dossier choisi et Python avec les droits de votre session Windows. Un script Python peut lui-même lancer des commandes système.",
                _codeExecutionCheckBox));

            _systemCommandCheckBox = new CheckBox
            {
                Text = "Autoriser",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Commandes système directes",
                "Désactivé par défaut. Ajoute l’outil run_system_command. Une commande s’exécute avec les droits de votre session Windows et peut lire ou modifier le système. Le dossier de départ reste limité au dossier de travail. Si Python est activé, un script Python peut aussi lancer des commandes.",
                _systemCommandCheckBox));

            _forumEnabledCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Forum partagé des IA",
                "Désactivé par défaut. Autorise tous les modèles à rechercher et publier des informations dans le forum local partagé. Les publications restent visibles et supprimables dans Outils > Forum des IA.",
                _forumEnabledCheckBox));

            _subagentsEnabledCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Autoriser les sous-agents",
                "Désactivé par défaut. Le modèle peut consulter la liste des modèles locaux. Si cette option est activée, il peut déléguer une tâche en choisissant le modèle, son prompt et ses outils. Le mode goal est indépendant : utilisez goal: au début du message, ou le modèle y passera après le lancement d'un sous-agent. Avant le premier lancement, l'application demande confirmation avant de vider le forum temporaire des rapports.",
                _subagentsEnabledCheckBox));

            _codeWorkspaceTextBox = new ModernTextBox
            {
                Height = 32,
                PlaceholderText = "Dossier de travail du modèle"
            };
            var workspacePicker = new Panel
            {
                Height = 34,
                BackColor = Color.Transparent
            };
            workspacePicker.Enabled = _codeExecutionCheckBox.Checked;
            _codeExecutionCheckBox.CheckedChanged += (s, e) =>
                workspacePicker.Enabled = _codeExecutionCheckBox.Checked;
            var browseWorkspaceButton = new ModernButton
            {
                Text = "Choisir...",
                Width = 104,
                Dock = DockStyle.Right,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                BackColorPressed = ThemeColors.Border
            };
            _codeWorkspaceTextBox.Dock = DockStyle.Fill;
            _codeWorkspaceTextBox.Margin = new Padding(0, 0, 8, 0);
            browseWorkspaceButton.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Choisissez le dossier de travail du modèle",
                    SelectedPath = Directory.Exists(_codeWorkspaceTextBox.Text)
                        ? _codeWorkspaceTextBox.Text
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    ShowNewFolderButton = true
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _codeWorkspaceTextBox.Text = dialog.SelectedPath;
            };
            workspacePicker.Controls.Add(_codeWorkspaceTextBox);
            workspacePicker.Controls.Add(browseWorkspaceButton);
            content.Controls.Add(CreateSettingCard(
                "Dossier de travail du code",
                "Les outils de fichiers ne peuvent lire ni écrire en dehors de ce dossier.",
                workspacePicker,
                editorBelowTitle: true,
                height: 110));

            AddSection(content, "AFFICHAGE PENDANT LA GÉNÉRATION");
            _showGenerationSpeedCheckBox = new CheckBox
            {
                Text = "Afficher",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Débit de génération (tokens/s)",
                "Désactivé par défaut. Affiche la vitesse moyenne de génération dans la ligne du modèle pendant que l’IA réfléchit ou répond. La valeur s’actualise deux fois par seconde.",
                _showGenerationSpeedCheckBox));

            AddSection(content, "DÉLAIS DES OUTILS");
            _generationTimeoutUpDown = CreateNumericControl(30, 7200, 10, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Délai d’inactivité de génération (secondes)",
                "Annule une génération si aucun token ni avancement d’outil n’arrive. Valeur par défaut : 900 secondes (15 minutes). Réglable de 30 secondes à 2 heures.",
                _generationTimeoutUpDown));

            _codeExecutionTimeoutUpDown = CreateNumericControl(1, 7200, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Délai maximal des scripts et commandes (secondes)",
                "Plafond du temps accordé à chaque script Python ou commande système. Le modèle peut demander moins, mais ne peut pas dépasser cette valeur. Valeur par défaut : 30 secondes.",
                _codeExecutionTimeoutUpDown));

            _contextSizeUpDown = CreateNumericControl(128, 131072, 128, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Taille du contexte",
                "Nombre maximal de tokens que le modèle peut prendre en compte. Les très petites valeurs sont prévues pour le mode ultra léger; une grande valeur utilise plus de mémoire.",
                _contextSizeUpDown));

            AddSection(content, "CHOIX DES MOTS");
            _temperatureUpDown = CreateNumericControl(0, 2, 0.1m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Température",
                "Contrôle la variété : bas pour des réponses plus prévisibles, haut pour plus de créativité.",
                _temperatureUpDown));

            _topPUpDown = CreateNumericControl(0, 1, 0.05m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Top P",
                "Limite les choix aux tokens les plus probables cumulés. Bas rend souvent la réponse plus ciblée.",
                _topPUpDown));

            _topKUpDown = CreateNumericControl(0, 1000, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Top K",
                "Limite le choix aux K tokens les plus probables. 0 désactive cette limite.",
                _topKUpDown));

            _minPUpDown = CreateNumericControl(0, 1, 0.01m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Min P",
                "Écarte les tokens trop improbables par rapport au meilleur choix. 0 désactive ce filtre.",
                _minPUpDown));

            AddSection(content, "LONGUEUR ET ARRÊT");
            _maxTokensUpDown = CreateNumericControl(1, 65536, 64, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Tokens maximum",
                "Longueur maximale d'une réponse. Le modèle peut s'arrêter plus tôt s'il a fini.",
                _maxTokensUpDown));

            _stopSequencesTextBox = new ModernTextBox
            {
                Multiline = true,
                Height = 55,
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Une séquence par ligne"
            };
            content.Controls.Add(CreateSettingCard(
                "Séquences d'arrêt",
                "Le modèle interrompt sa réponse dès qu'il produit l'une de ces séquences exactes.",
                _stopSequencesTextBox,
                editorBelowTitle: true,
                height: 124));

            _seedUpDown = CreateNumericControl(-1, 4294967295m, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Graine aléatoire",
                "-1 choisit une réponse aléatoire. Une valeur fixe aide à reproduire les mêmes réponses avec les mêmes réglages.",
                _seedUpDown));

            AddSection(content, "RÉPÉTITIONS");
            _repeatPenaltyUpDown = CreateNumericControl(0.5m, 2, 0.05m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Pénalité de répétition",
                "Réduit les répétitions de tokens déjà utilisés. 1.0 désactive cette pénalité.",
                _repeatPenaltyUpDown));

            _repeatLastNUpDown = CreateNumericControl(0, 4096, 16, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Fenêtre de répétition",
                "Nombre de tokens récents pris en compte par la pénalité. 0 désactive cette pénalité.",
                _repeatLastNUpDown));

            _presencePenaltyUpDown = CreateNumericControl(-2, 2, 0.05m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Pénalité de présence",
                "Pénalise une fois les tokens déjà présents pour encourager de nouveaux éléments.",
                _presencePenaltyUpDown));

            _frequencyPenaltyUpDown = CreateNumericControl(-2, 2, 0.05m, 2, 150);
            content.Controls.Add(CreateSettingCard(
                "Pénalité de fréquence",
                "Pénalise davantage les tokens qui apparaissent souvent dans la réponse.",
                _frequencyPenaltyUpDown));

            AddSection(content, "PERFORMANCES");
            _reasoningEffortComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeColors.CodeBackground,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f),
                Items = { "Par défaut", "Désactivé", "Minimal", "Faible", "Moyen", "Élevé", "Très élevé", "Maximum" }
            };
            content.Controls.Add(CreateSettingCard(
                "Effort de réflexion",
                "Règle l'effort de raisonnement demandé aux modèles compatibles. « Par défaut » laisse le modèle choisir.",
                _reasoningEffortComboBox));

            _ssdModelMemoryCheckBox = new CheckBox
            {
                Text = "Activé",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mapper les poids du modèle texte depuis le SSD (mmap)",
                "Désactivé par défaut. Le moteur lit les poids GGUF depuis le fichier au lieu de les charger tous immédiatement en RAM. Windows peut paginer les données; cela peut ralentir le modèle. Appliqué au prochain chargement.",
                _ssdModelMemoryCheckBox));

            _imageDiskMemoryCheckBox = new CheckBox
            {
                Text = "Forcer le CPU",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mode CPU sécurisé pour les images",
                "Désactivé par défaut. Force le moteur sur le processeur pour éviter qu’un GPU intégré réserve trop de mémoire partagée. En cas de manque de mémoire, l’application essaie des résolutions plus petites. Le checkpoint reste dans son dossier; aucune copie complète n’est faite dans AppData.",
                _imageDiskMemoryCheckBox));

            _imageMemoryLimitUpDown = CreateNumericControl(1, 64, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Limite mémoire du moteur d’image (Gio)",
                "Plafond dur RAM + mémoire virtuelle du processus du moteur. Valeur par défaut : 4 Gio. Si le moteur atteint cette limite, il s’arrête et l’application réessaie à une résolution plus petite au lieu de laisser le fichier d’échange grossir sans borne.",
                _imageMemoryLimitUpDown));

            _imageMinimumFreeDiskUpDown = CreateNumericControl(1, 100, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Espace disque libre à préserver (Gio)",
                "La génération est refusée ou arrêtée si le disque système ou le dossier de sortie passe sous ce seuil. Réglage par défaut : 5 Gio.",
                _imageMinimumFreeDiskUpDown));

            _nglUpDown = CreateNumericControl(-1, 999, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Couches sur le GPU",
                "Nombre de couches placées sur la carte graphique. -1 demande toutes les couches ; cela utilise plus de VRAM.",
                _nglUpDown));

            _threadsUpDown = CreateNumericControl(0, 256, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Threads CPU",
                "Nombre de fils de calcul CPU. 0 laisse le backend choisir automatiquement.",
                _threadsUpDown));

            scrollPanel.Controls.Add(content);

            var buttonPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(12, 10, 16, 8),
                BackColor = ThemeColors.Surface
            };
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            buttonPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130f));

            _btnDefaults = new ModernButton
            {
                Text = "Rétablir les défauts",
                Width = 145,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border
            };
            _btnDefaults.Click += (s, e) =>
            {
                Settings = new AppSettings();
                LoadSettings();
            };
            _btnCancel = new ModernButton { Text = "Annuler", Width = 100, BackColorNormal = ThemeColors.BorderLight, BackColorHover = ThemeColors.Border };
            _btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            _btnSave = new ModernButton { Text = "Sauvegarder", Width = 120 };
            _btnSave.Click += (s, e) => { SaveSettings(); this.DialogResult = DialogResult.OK; this.Close(); };
            buttonPanel.Controls.Add(_btnDefaults, 0, 0);
            buttonPanel.Controls.Add(_btnCancel, 1, 0);
            buttonPanel.Controls.Add(_btnSave, 2, 0);

            root.Controls.Add(scrollPanel, 0, 0);
            root.Controls.Add(buttonPanel, 0, 1);
            this.Controls.Add(root);
            this.AcceptButton = _btnSave;
            this.CancelButton = _btnCancel;
        }

        private void AddSection(FlowLayoutPanel content, string title)
        {
            content.Controls.Add(new Label
            {
                Text = title,
                ForeColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Width = 580,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 8, 0, 2),
                BackColor = ThemeColors.Background
            });
        }

        private Control CreateSettingCard(string title, string description, Control editor,
                                          bool editorBelowTitle = false, int height = 76)
        {
            const int cardWidth = 580;
            var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var descriptionFont = new Font("Segoe UI", 9.5f);
            var editorBelow = editorBelowTitle ||
                (editor is CheckBox checkBox &&
                 TextRenderer.MeasureText(checkBox.Text, checkBox.Font).Width > 170);
            var descriptionHeight = Math.Max(22, TextRenderer.MeasureText(
                description ?? "",
                descriptionFont,
                new Size(cardWidth - 24, 1000),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 2);
            var editorTop = editorBelow ? Math.Max(65, 30 + descriptionHeight + 4) : 4;
            var editorHeight = editorBelow ? Math.Max(30, height - 74) : editor.Height;
            var requiredHeight = editorBelow
                ? editorTop + editorHeight + 8
                : 30 + descriptionHeight + 8;
            var cardHeight = Math.Max(height, requiredHeight);
            var card = new Panel
            {
                Width = cardWidth,
                Height = cardHeight,
                BackColor = ThemeColors.Surface,
                Margin = new Padding(0, 0, 0, 8)
            };
            var titleLabel = new Label
            {
                Text = title,
                Location = new Point(12, 6),
                Width = editorBelow ? cardWidth - 24 : cardWidth - 190,
                Height = 22,
                ForeColor = ThemeColors.TextPrimary,
                Font = titleFont,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            };
            var descriptionLabel = new Label
            {
                Text = description,
                Location = new Point(12, 30),
                Width = cardWidth - 24,
                Height = descriptionHeight,
                ForeColor = ThemeColors.TextSecondary,
                Font = descriptionFont,
                TextAlign = ContentAlignment.TopLeft,
                AutoSize = false
            };
            card.Controls.Add(titleLabel);
            card.Controls.Add(descriptionLabel);

            if (editorBelow)
            {
                editor.Location = new Point(12, editorTop);
                editor.Width = cardWidth - 24;
                editor.Height = editorHeight;
            }
            else
            {
                editor.Width = 150;
                editor.Location = new Point(cardWidth - editor.Width - 12, 4);
                editor.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            }
            card.Controls.Add(editor);
            return card;
        }

        private static ModernNumericUpDown CreateNumericControl(decimal minimum, decimal maximum,
                                                                decimal increment, int decimalPlaces, int width)
        {
            return new ModernNumericUpDown
            {
                Minimum = minimum,
                Maximum = maximum,
                Increment = increment,
                DecimalPlaces = decimalPlaces,
                Width = width
            };
        }

        private static ModernTextBox CreateModelPathTextBox()
        {
            return new ModernTextBox
            {
                Multiline = false,
                ScrollBars = ScrollBars.None,
                Height = 36,
                PlaceholderText = "Aucun modèle sélectionné"
            };
        }

        private Control CreateModelPathPicker(ModernTextBox pathTextBox, EventHandler browseHandler)
        {
            var picker = new Panel
            {
                Height = 36,
                BackColor = ThemeColors.Surface
            };
            pathTextBox.Dock = DockStyle.Fill;
            var browseButton = new ModernButton
            {
                Text = "Parcourir…",
                Width = 112,
                Dock = DockStyle.Right,
                BackColorNormal = ThemeColors.BorderLight,
                BackColorHover = ThemeColors.Border,
                BackColorPressed = ThemeColors.Border
            };
            browseButton.Click += browseHandler;
            picker.Controls.Add(pathTextBox);
            picker.Controls.Add(browseButton);
            return picker;
        }

        private void BrowseDefaultTextModel(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choisir le modèle de texte par défaut",
                Filter = "Modèles GGUF (*.gguf)|*.gguf|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                FileName = File.Exists(_defaultTextModelPathTextBox.Text) ? _defaultTextModelPathTextBox.Text : ""
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _defaultTextModelPathTextBox.Text = dialog.FileName;
        }

        private void BrowseConversationTitleModel(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choisir le modèle GGUF de nommage des conversations",
                Filter = "Modèles GGUF (*.gguf)|*.gguf|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                FileName = File.Exists(_conversationTitleModelPathTextBox.Text)
                    ? _conversationTitleModelPathTextBox.Text : ""
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _conversationTitleModelPathTextBox.Text = dialog.FileName;
        }

        private void BrowseDefaultImageModel(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choisir le modèle d'image par défaut",
                Filter = "Modèles d'image (*.safetensors;*.gguf;*.sft;*.ckpt;*.pt;*.pth;*.bin)|*.safetensors;*.gguf;*.sft;*.ckpt;*.pt;*.pth;*.bin|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                FileName = File.Exists(_defaultImageModelPathTextBox.Text) ? _defaultImageModelPathTextBox.Text : ""
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _defaultImageModelPathTextBox.Text = dialog.FileName;
        }

        private void BrowseDefaultOmniModel(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choisir le modèle Omni vision par défaut",
                Filter = "Modèles GGUF (*.gguf)|*.gguf|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                FileName = File.Exists(_defaultOmniModelPathTextBox.Text) ? _defaultOmniModelPathTextBox.Text : ""
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _defaultOmniModelPathTextBox.Text = dialog.FileName;
        }

        private static void SetNumericValue(ModernNumericUpDown control, decimal value)
        {
            control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
        }

        private void LoadSettings()
        {
            _ultraLiteModeCheckBox.Checked = Settings.UltraLiteMode;
            _conversationTitlePromptTextBox.Text = Settings.ConversationTitlePrompt ?? AppSettings.DefaultConversationTitlePrompt;
            _systemPromptTextBox.Text = Settings.SystemPrompt ?? "";
            _defaultTextModelPathTextBox.Text = Settings.DefaultTextModelPath ?? "";
            _defaultImageModelPathTextBox.Text = Settings.DefaultImageModelPath ?? "";
            _defaultOmniModelPathTextBox.Text = Settings.DefaultOmniModelPath ?? "";
            _keepChatModelLoadedForOmniCheckBox.Checked = Settings.KeepChatModelLoadedForOmni;
            _useDedicatedConversationTitleModelCheckBox.Checked = Settings.UseDedicatedConversationTitleModel;
            _conversationTitleModelPathTextBox.Text = Settings.ConversationTitleModelPath ?? "";
            _conversationTitleModelPicker.Enabled = Settings.UseDedicatedConversationTitleModel;
            _searchAllModelDrivesCheckBox.Checked = Settings.SearchAllDrivesForModels;
            _webAccessCheckBox.Checked = Settings.EnableWebAccess;
            _codeExecutionCheckBox.Checked = Settings.EnableCodeExecution;
            _systemCommandCheckBox.Checked = Settings.EnableSystemCommands;
            _forumEnabledCheckBox.Checked = Settings.EnableForum;
            _subagentsEnabledCheckBox.Checked = Settings.EnableSubagents;
            _autoUpdateCheckBox.Checked = Settings.AutoUpdateEnabled;
            _darkModeCheckBox.Checked = Settings.DarkMode;
            _showGenerationSpeedCheckBox.Checked = Settings.ShowGenerationSpeed;
            _codeWorkspaceTextBox.Text = Settings.CodeWorkspace ?? AppSettings.DefaultCodeWorkspace;
            _stopSequencesTextBox.Text = Settings.StopSequences ?? "";
            SetNumericValue(_temperatureUpDown, (decimal)Settings.Temperature);
            SetNumericValue(_topPUpDown, (decimal)Settings.TopP);
            SetNumericValue(_topKUpDown, Settings.TopK);
            SetNumericValue(_minPUpDown, (decimal)Settings.MinP);
            SetNumericValue(_maxTokensUpDown, Settings.MaxTokens);
            SetNumericValue(_repeatPenaltyUpDown, (decimal)Settings.RepeatPenalty);
            SetNumericValue(_repeatLastNUpDown, Settings.RepeatLastN);
            SetNumericValue(_presencePenaltyUpDown, (decimal)Settings.PresencePenalty);
            SetNumericValue(_frequencyPenaltyUpDown, (decimal)Settings.FrequencyPenalty);
            SetNumericValue(_seedUpDown, Settings.Seed);
            SetNumericValue(_contextSizeUpDown, Settings.ContextSize);
            SetNumericValue(_nglUpDown, Settings.Ngl);
            SetNumericValue(_threadsUpDown, Settings.Threads);
            SetNumericValue(_generationTimeoutUpDown, Settings.GenerationTimeoutSeconds);
            SetNumericValue(_codeExecutionTimeoutUpDown, Settings.CodeExecutionTimeoutSeconds);
            _ssdModelMemoryCheckBox.Checked = Settings.UseMmapForModel;
            _imageDiskMemoryCheckBox.Checked = Settings.UseDiskForImageMemory;
            SetNumericValue(_imageMemoryLimitUpDown, Settings.ImageEngineMemoryLimitGb);
            SetNumericValue(_imageMinimumFreeDiskUpDown, Settings.ImageMinimumFreeDiskGb);
            var reasoningEffortValues = new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh", "max" };
            var selectedEffort = Array.IndexOf(reasoningEffortValues,
                (Settings.ReasoningEffort ?? "default").Trim().ToLowerInvariant());
            _reasoningEffortComboBox.SelectedIndex = selectedEffort >= 0 ? selectedEffort : 0;
        }

        private void SaveSettings()
        {
            Settings.UltraLiteMode = _ultraLiteModeCheckBox.Checked;
            Settings.ConversationTitlePrompt = _conversationTitlePromptTextBox.Text;
            Settings.SystemPrompt = _systemPromptTextBox.Text;
            Settings.DefaultTextModelPath = _defaultTextModelPathTextBox.Text.Trim();
            Settings.DefaultImageModelPath = _defaultImageModelPathTextBox.Text.Trim();
            Settings.DefaultOmniModelPath = _defaultOmniModelPathTextBox.Text.Trim();
            Settings.KeepChatModelLoadedForOmni = _keepChatModelLoadedForOmniCheckBox.Checked;
            Settings.UseDedicatedConversationTitleModel = _useDedicatedConversationTitleModelCheckBox.Checked;
            Settings.ConversationTitleModelPath = _conversationTitleModelPathTextBox.Text.Trim();
            Settings.SearchAllDrivesForModels = _searchAllModelDrivesCheckBox.Checked;
            Settings.EnableWebAccess = _webAccessCheckBox.Checked;
            Settings.EnableCodeExecution = _codeExecutionCheckBox.Checked;
            Settings.EnableSystemCommands = _systemCommandCheckBox.Checked;
            Settings.EnableForum = _forumEnabledCheckBox.Checked;
            Settings.EnableSubagents = _subagentsEnabledCheckBox.Checked;
            Settings.AutoUpdateEnabled = _autoUpdateCheckBox.Checked;
            Settings.DarkMode = _darkModeCheckBox.Checked;
            Settings.ShowGenerationSpeed = _showGenerationSpeedCheckBox.Checked;
            Settings.CodeWorkspace = string.IsNullOrWhiteSpace(_codeWorkspaceTextBox.Text)
                ? AppSettings.DefaultCodeWorkspace
                : _codeWorkspaceTextBox.Text.Trim();
            Settings.StopSequences = _stopSequencesTextBox.Text;
            Settings.Temperature = (float)_temperatureUpDown.Value;
            Settings.TopP = (float)_topPUpDown.Value;
            Settings.TopK = (int)_topKUpDown.Value;
            Settings.MinP = (float)_minPUpDown.Value;
            Settings.MaxTokens = (int)_maxTokensUpDown.Value;
            Settings.RepeatPenalty = (float)_repeatPenaltyUpDown.Value;
            Settings.RepeatLastN = (int)_repeatLastNUpDown.Value;
            Settings.PresencePenalty = (float)_presencePenaltyUpDown.Value;
            Settings.FrequencyPenalty = (float)_frequencyPenaltyUpDown.Value;
            Settings.Seed = (long)_seedUpDown.Value;
            Settings.ContextSize = (int)_contextSizeUpDown.Value;
            Settings.Ngl = (int)_nglUpDown.Value;
            Settings.Threads = (int)_threadsUpDown.Value;
            Settings.GenerationTimeoutSeconds = (int)_generationTimeoutUpDown.Value;
            Settings.CodeExecutionTimeoutSeconds = (int)_codeExecutionTimeoutUpDown.Value;
            Settings.UseMmapForModel = _ssdModelMemoryCheckBox.Checked;
            Settings.UseDiskForImageMemory = _imageDiskMemoryCheckBox.Checked;
            Settings.ImageEngineMemoryLimitGb = (int)_imageMemoryLimitUpDown.Value;
            Settings.ImageMinimumFreeDiskGb = (int)_imageMinimumFreeDiskUpDown.Value;
            Settings.MemoryOptionsInitialized = true;
            var reasoningEffortValues = new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh", "max" };
            var effortIndex = Math.Clamp(_reasoningEffortComboBox.SelectedIndex, 0, reasoningEffortValues.Length - 1);
            Settings.ReasoningEffort = reasoningEffortValues[effortIndex];
        }
    }

    // ===== MODERN NUMERIC UPDOWN =====
    public class ModernNumericUpDown : NumericUpDown
    {
        public ModernNumericUpDown()
        {
            BackColor = ThemeColors.CodeBackground;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 9.5f);
            BorderStyle = BorderStyle.FixedSingle;
            Width = 120;
            TextAlign = HorizontalAlignment.Right;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Focused ? ThemeColors.Primary : ThemeColors.Border, Focused ? 2 : 1))
            {
                var rect = ClientRectangle;
                rect.Width--; rect.Height--;
                e.Graphics.DrawRectangle(pen, rect);
            }
        }
    }
}


