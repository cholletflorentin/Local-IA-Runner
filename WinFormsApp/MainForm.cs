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
    public partial class MainForm : Form
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
        private bool _imageGenerationActive = false;
        private string _selectedModelName = "";
        private string _currentConversationId = Guid.NewGuid().ToString("N");
        private string _currentConversationTitle = "";
        private bool _currentConversationTitleGenerated = false;
        private bool _conversationTitleRequestPending = false;
        private readonly Dictionary<string, string> _pendingConversationTitleFiles = new(StringComparer.Ordinal);
        private readonly StringBuilder _streamingConversationTitle = new();
        private string _streamingConversationTitleId = "";
        private AppSettings _appSettings = new AppSettings();
        private readonly object _pythonInputLock = new object();
        
        // Watchdog pour détecter génération bloquée
        private System.Threading.Timer _generationWatchdog;
        private readonly object _streamBufferLock = new();
        private readonly StringBuilder _pendingStreamTokens = new();
        private System.Windows.Forms.Timer _streamRenderTimer;
        private DateTime _generationLastProgressTime;
        private int GenerationTimeoutSeconds => Math.Clamp(_appSettings.GenerationTimeoutSeconds, 30, 7200);
        private bool _receivedFirstToken = false;
        
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

        public MainForm()
        {
            EnableHighDpiAwareness();
            InitializeComponent();
            _streamRenderTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _streamRenderTimer.Tick += (s, e) => FlushPendingStreamingTokens();
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
            _sidebarPanel.RefreshModelsRequested += OnRefreshModels;
            
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
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 154F));

            _chatPanel = new ChatPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            chatLayout.Controls.Add(_chatPanel, 0, 0);

            _inputPanel = new InputPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _inputPanel.SendClicked += OnSendMessage;
            _inputPanel.CancelClicked += OnCancelGeneration;
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
            _menuStrip.SearchKnownModelsClicked += (s, e) => ScanModelsFolder(forceQuickScan: true);
            _menuStrip.SearchAllModelsClicked += (s, e) => ScanModelsFolder(forceFullScan: true);
            _menuStrip.CancelModelSearchClicked += (s, e) => CancelModelScan();
            _menuStrip.RestartBackendClicked += OnRestartBackend;
            _menuStrip.SettingsClicked += OnSettings;
            _menuStrip.ImageGeneratorClicked += OnOpenImageGenerator;
            _menuStrip.ForumClicked += OnOpenForum;
            _menuStrip.CheckForUpdatesClicked += OnCheckForUpdates;
            _menuStrip.ExitClicked += (s, e) => Application.Exit();
            this.Controls.Add(_menuStrip);
            this.MainMenuStrip = _menuStrip;

            // Status bar
            _statusBar = new StatusBar();
            this.Controls.Add(_statusBar);

            this.Load += (s, e) => 
            {
                UpdateTheme();
                LoadAppSettings();
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
            this.BackColor = ThemeColors.Background;
            this.ForeColor = ThemeColors.TextPrimary;
            _menuStrip.UpdateTheme();
            _statusBar.UpdateTheme();
            _chatPanel.UpdateTheme();
            _inputPanel.UpdateTheme();
            _sidebarPanel.UpdateTheme();
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
                        // Upgrade the previous built-in system prompt to the new empty default.
                        if (string.Equals(settings.SystemPrompt,
                            "Tu es un assistant IA utile et amical. Reponds en francais de maniere claire et concise.",
                            StringComparison.Ordinal))
                            settings.SystemPrompt = "";
                        _appSettings = settings;
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
                                    $"{skippedMmprojCount} fichier(s) mmproj ignoré(s) : ce sont des projecteurs multimodaux, pas des modèles principaux. Le backend actuel ne prend pas encore en charge leur chargement.",
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
                _statusBar.SetStatus("Projecteur multimodal non pris en charge", StatusType.Warning);
                _chatPanel.AddSystemMessage(
                    "Ce fichier mmproj est un projecteur multimodal auxiliaire; il ne peut pas être chargé seul comme modèle. Choisissez le fichier GGUF principal.",
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
                            System.Diagnostics.Debug.WriteLine("[Python stderr] " + e.Data);
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
                            break;
                        case "response":
                            _generationWatchdog?.Dispose();
                            _isGenerating = false;
                            _isStreamingResponse = false;
                            _isCancellingGeneration = false;
                            _generationTimedOut = false;
                            _inputPanel.SetGenerating(false);
                            var responseContent = msg.Content ?? "";
                            _chatPanel.AddMessage("Assistant", responseContent, false);
                            _currentConversation.Add(new Message { Role = "assistant", Content = responseContent, Timestamp = DateTime.Now });
                            SaveConversation();
                            _statusBar.SetStatus("Pret", StatusType.Success);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "generation_start":
                            MarkGenerationProgress();
                            _isStreamingResponse = true;
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
                            break;
                        }
                        case "thinking_token":
                            MarkGenerationProgress();
                            if (!string.IsNullOrEmpty(msg.Content))
                            {
                                if (string.Equals(msg.Source, "conversation_title", StringComparison.Ordinal))
                                    _chatPanel.ShowThinkingActivity();
                                else
                                    _chatPanel.AppendThinkingPreviewToken(msg.Content);
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
                                QueueStreamingToken(msg.Content);
                            break;
                        case "stream_end":
                            System.Diagnostics.Debug.WriteLine($"[C# stream_end] Before EndStreamingMessage, isGenerating={_isGenerating}");
                            Log("stream_end received");
                            _generationWatchdog?.Dispose();
                            _streamRenderTimer.Stop();
                            FlushPendingStreamingTokens();
                            Log("stream tokens flushed");
                            _isStreamingResponse = false;
                            var generationTimedOut = _generationTimedOut;
                            try {
                                Log("finalizing streamed message");
                                _chatPanel.EndStreamingMessage(msg.Content ?? "");
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end] EndStreamingMessage done");
                                Log("stream message finalized");
                                if (!string.IsNullOrWhiteSpace(msg.Content))
                                {
                                    _currentConversation.Add(new Message { Role = "assistant", Content = msg.Content, Timestamp = DateTime.Now });
                                    _chatPanel.RenderMessages(_currentConversation);
                                    SaveConversation();
                                }
                                _isGenerating = false;
                                _isCancellingGeneration = false;
                                _inputPanel.SetGenerating(false);
                                Log("send controls restored");
                                if (!string.IsNullOrWhiteSpace(msg.Error))
                                {
                                    _statusBar.SetStatus("Erreur: " + msg.Error, StatusType.Error);
                                    _chatPanel.AddSystemMessage(msg.Error, MessageType.Error);
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
                                _generationTimedOut = false;
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end] Flags reset, isGenerating={_isGenerating}");
                            } catch (Exception ex) {
                                System.Diagnostics.Debug.WriteLine($"[C# stream_end ERROR] {ex.Message}\n{ex.StackTrace}");
                                _isGenerating = false;
                                _isCancellingGeneration = false;
                                _inputPanel.SetGenerating(false);
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
                            if (!_isStreamingResponse)
                            {
                                _generationWatchdog?.Dispose();
                                _isGenerating = false;
                                _isCancellingGeneration = false;
                                _generationTimedOut = false;
                                _inputPanel.SetGenerating(false);
                            }
                            _statusBar.SetStatus("Erreur: " + msg.Content, StatusType.Error);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "model_loading":
                            _statusBar.SetStatus("Chargement: " + msg.Content, StatusType.Info);
                            break;
                        case "model_loaded":
                            _statusBar.SetStatus("Modele charge: " + msg.Content, StatusType.Success);
                            break;
                        case "image_engine_status":
                            MarkGenerationProgress();
                            _imageGeneratorForm?.SetStatus(msg.Content ?? "Préparation du moteur d'images…");
                            break;
                        case "image_engine_progress":
                            _imageGeneratorForm?.SetDownloadProgress(msg.Downloaded ?? 0, msg.Total ?? 0);
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
                            _imageGenerationActive = true;
                            _imageGeneratorForm?.SetGenerating(true);
                            _imageGeneratorForm?.SetStatus(msg.Content ?? "Chargement du modèle d'images…");
                            break;
                        case "image_progress":
                            MarkGenerationProgress();
                            _imageGeneratorForm?.SetProgress(msg.Stage, msg.Detail, msg.Completed, msg.Total, msg.Pass);
                            break;
                        case "image_preview":
                            if (!string.IsNullOrEmpty(msg.Content))
                                _imageGeneratorForm?.SetPreview(msg.Content);
                            break;
                        case "image_complete":
                            _imageGenerationActive = false;
                            _imageGeneratorForm?.SetCompleted(msg.Paths?.ToObject<List<string>>() ?? new List<string>(), msg.Error, msg.Cancelled);
                            _statusBar.SetStatus(msg.Error != null ? "Erreur génération image" : msg.Cancelled ? "Génération image annulée" : "Image générée", msg.Error != null ? StatusType.Error : msg.Cancelled ? StatusType.Warning : StatusType.Success);
                            ApplyPendingUpdateWhenIdle();
                            break;
                        case "image_error":
                            _imageGenerationActive = false;
                            _imageGeneratorForm?.SetCompleted(new List<string>(), msg.Content ?? "Erreur inconnue", false);
                            _statusBar.SetStatus("Erreur génération image", StatusType.Error);
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
            if (string.IsNullOrWhiteSpace(text)) return;

            _chatPanel.AddMessage("Vous :", text, true);
            _currentConversation.Add(new Message { Role = "user", Content = text, Timestamp = DateTime.Now });
            _inputPanel.ClearInput();

            // Create the conversation file as soon as its first prompt is sent.
            // The selected model will replace this provisional name with its
            // generated title before the first answer starts.
            SaveConversation();

            _isGenerating = true;
            _isCancellingGeneration = false;
            _generationTimedOut = false;
            _receivedFirstToken = false;
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
                if (!string.IsNullOrWhiteSpace(_appSettings.SystemPrompt))
                {
                    history.Add(new { role = "system", content = _appSettings.SystemPrompt });
                }
                // Conversation titles are file/sidebar metadata, never chat messages or model history.
                // The current message is also sent as `prompt`; do not include it twice in history.
                history.AddRange(_currentConversation
                    .Take(Math.Max(0, _currentConversation.Count - 1))
                    .Select(m => new { role = m.Role, content = m.Content }));

                var firstUserMessage = _currentConversation.FirstOrDefault(message => message.Role == "user");
                shouldGenerateConversationTitle = !_currentConversationTitleGenerated &&
                    !_conversationTitleRequestPending &&
                    firstUserMessage != null &&
                    File.Exists(_currentConversationFile);
                object[] titleMessages = shouldGenerateConversationTitle
                    ? new object[] { new { role = "user", content = LimitTitleContext(firstUserMessage?.Content ?? text) } }
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
                    conversation_title_prompt = _appSettings.ConversationTitlePrompt,
                    prompt = text, 
                    history = history.ToArray(),
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
                    enable_web_access = _appSettings.EnableWebAccess,
                    enable_code_execution = _appSettings.EnableCodeExecution,
                    enable_forum = _appSettings.EnableForum,
                    enable_subagents = _appSettings.EnableSubagents,
                    available_local_models = GetAvailableLocalModelPaths(),
                    available_subagent_models = GetAvailableSubagentModelPaths(),
                    code_workspace = _appSettings.CodeWorkspace,
                    default_image_model_path = _appSettings.DefaultImageModelPath,
                    generation_timeout_seconds = _appSettings.GenerationTimeoutSeconds,
                    code_execution_timeout_seconds = _appSettings.CodeExecutionTimeoutSeconds,
                    generated_images_directory = ImageGeneratorForm.GetConfiguredOutputDirectory()
                };
                var json = JsonConvert.SerializeObject(request);
                System.Diagnostics.Debug.WriteLine($"[C# Send] {json}");
                
                // Write async to avoid blocking UI thread if pipe is full
                Task.Run(() => 
                {
                    System.Diagnostics.Debug.WriteLine($"[C# Send Task] Starting");
                    try 
                    {
                        lock (_pythonInputLock)
                        {
                            if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                                throw new InvalidOperationException("Le backend Python n'est pas disponible.");
                            _pythonInput.WriteLine(json);
                            _pythonInput.Flush();
                        }
                        System.Diagnostics.Debug.WriteLine($"[C# Send Task] OK - wrote {json.Length} chars");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[C# Send Task ERROR] {ex.Message}\n{ex.StackTrace}");
                        _generationWatchdog?.Dispose();
                        Invoke(new Action(() => 
                        {
                            _isGenerating = false;
                            _isStreamingResponse = false;
                            _isCancellingGeneration = false;
                            if (shouldGenerateConversationTitle)
                                ForgetPendingConversationTitle(conversationId);
                            _inputPanel.SetGenerating(false);
                            _statusBar.SetStatus("Erreur envoi: " + ex.Message, StatusType.Error);
                        }));
                    }
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

            Task.Run(() =>
            {
                try
                {
                    lock (_pythonInputLock)
                    {
                        if (_pythonInput == null || _pythonProcess == null || _pythonProcess.HasExited)
                            throw new InvalidOperationException("Le backend Python ne répond pas.");
                        _pythonInput.WriteLine("{\"type\":\"cancel\"}");
                        _pythonInput.Flush();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Cancel generation] " + ex.Message);
                }
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
                    var searchModeChanged =
                        _appSettings.SearchAllDrivesForModels != settingsForm.Settings.SearchAllDrivesForModels ||
                        !string.Equals(_appSettings.DefaultTextModelPath, settingsForm.Settings.DefaultTextModelPath, StringComparison.OrdinalIgnoreCase);
                    _appSettings = settingsForm.Settings;
                    SaveAppSettings();
                    _statusBar.SetStatus("Parametres sauvegardes", StatusType.Success);
                    if (searchModeChanged)
                        ScanModelsFolder();
                }
            }
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

            _imageGeneratorForm = new ImageGeneratorForm(_appSettings.DefaultImageModelPath);
            _imageGeneratorForm.CatalogRequested += (s, modelDir) =>
                SendImageBackendRequest(new { type = "image_catalog", model_dir = modelDir });
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

        private void SendImageBackendRequest(object request)
        {
            var json = JsonConvert.SerializeObject(request);
            Task.Run(() =>
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
                catch (Exception ex)
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
                }
            });
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
            _generationWatchdog?.Change(
                TimeSpan.FromSeconds(GenerationTimeoutSeconds),
                Timeout.InfiniteTimeSpan);
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
        public const string DefaultConversationTitlePrompt = "Donne un titre très court de 2 à 4 mots, 5 au maximum. Décris uniquement le sujet du message, dans sa langue. Ce titre sert seulement à nommer la conversation : ne réponds pas au message. Réponds uniquement par le titre, sans préambule ni guillemets.";
        public static string DefaultCodeWorkspace => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocalIARunnerWorkspace");
        public string SystemPrompt { get; set; } = "";
        public string ConversationTitlePrompt { get; set; } = DefaultConversationTitlePrompt;
        public string DefaultTextModelPath { get; set; } = "";
        public string DefaultImageModelPath { get; set; } = "";
        public bool EnableWebAccess { get; set; } = true;
        public bool EnableCodeExecution { get; set; } = false;
        public bool EnableForum { get; set; } = false;
        public bool EnableSubagents { get; set; } = false;
        public bool SearchAllDrivesForModels { get; set; } = false;
        public bool AutoUpdateEnabled { get; set; } = true;
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
        public bool UseMmapForModel { get; set; } = true;
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
        public bool Cancelled { get; set; }
        public string Error { get; set; }
        public JArray Recipes { get; set; }
        public JArray Files { get; set; }
        public JArray Errors { get; set; }
        public JArray Paths { get; set; }
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
        public static readonly Color Background = Color.FromArgb(0xF5, 0xF6, 0xF8);
        public static readonly Color Surface = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static readonly Color SurfaceHover = Color.FromArgb(0xF8, 0xF9, 0xFC);
        public static readonly Color Border = Color.FromArgb(0xE5, 0xE8, 0xEE);
        public static readonly Color BorderLight = Color.FromArgb(0xD9, 0xDD, 0xE6);
        public static readonly Color Primary = Color.FromArgb(0x59, 0x67, 0xDC);
        public static readonly Color PrimaryHover = Color.FromArgb(0x4B, 0x58, 0xCB);
        public static readonly Color PrimaryPressed = Color.FromArgb(0x40, 0x4C, 0xB4);
        public static readonly Color Success = Color.FromArgb(0x23, 0x85, 0x5B);
        public static readonly Color Warning = Color.FromArgb(0xA2, 0x74, 0x19);
        public static readonly Color Error = Color.FromArgb(0xB7, 0x49, 0x43);
        public static readonly Color TextPrimary = Color.FromArgb(0x20, 0x24, 0x31);
        public static readonly Color TextSecondary = Color.FromArgb(0x55, 0x5D, 0x6D);
        public static readonly Color TextMuted = Color.FromArgb(0x73, 0x79, 0x89);
        public static readonly Color Thinking = Color.FromArgb(0x85, 0x8B, 0x99);
        public static readonly Color UserBubble = Color.FromArgb(0xF1, 0xF3, 0xF7);
        public static readonly Color AssistantBubble = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static readonly Color CodeBackground = Color.FromArgb(0xF8, 0xF9, 0xFB);
        public static readonly Color ScrollBar = Color.FromArgb(0xD5, 0xD9, 0xE1);
        public static readonly Color ScrollBarHover = Color.FromArgb(0xB9, 0xBF, 0xCA);
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
        public Color TextColor { get; set; } = Color.White;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int CornerRadius { get; set; } = 8;
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
            ForeColor = TextColor;
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
                ? ControlPaint.Dark(BackColorNormal, 0.2f)
                : _pressed ? BackColorPressed : _hovered ? BackColorHover : BackColorNormal;
            using (var path = GetRoundedRectPath(rect, CornerRadius))
            using (var brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }

            var textRect = Rectangle.Inflate(rect, -Math.Min(4, rect.Width / 2), -Math.Min(1, rect.Height / 2));
            var textColor = Enabled ? TextColor : ThemeColors.TextMuted;
            if (textRect.Width > 0 && textRect.Height > 0)
            {
                TextRenderer.DrawText(g, Text ?? "", Font, textRect, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (Focused && ShowFocusCues)
            {
                var focusRect = Rectangle.Inflate(rect, -4, -4);
                if (focusRect.Width > 0 && focusRect.Height > 0)
                    ControlPaint.DrawFocusRectangle(g, focusRect, textColor, color);
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
        public event EventHandler SendClicked;
        public event EventHandler CancelClicked;

        private ModernTextBox _inputBox;
        private ModernButton _sendBtn;
        private Label _charCount;
        private Label _modelLabel;
        private bool _ready = false;
        private bool _modelSelected = false;
        private bool _isGenerating = false;
        private bool _isCancelling = false;

        public InputPanel()
        {
            Height = 154;
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
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            _inputBox = new ModernTextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Font = new Font("Segoe UI", 10f),
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Écrivez votre message… (Entrée pour envoyer, Maj+Entrée pour une nouvelle ligne)"
            };
            _inputBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    if (_ready && _modelSelected && !_isGenerating) SendClicked?.Invoke(this, EventArgs.Empty);
                }
            };
            _inputBox.TextChanged += (s, e) => UpdateCharCount();
            layout.Controls.Add(_inputBox, 0, 0);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Padding = new Padding(0, 4, 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));

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
            footer.Controls.Add(_modelLabel, 0, 0);
            _charCount = new Label
            {
                Dock = DockStyle.Fill,
                Text = "0 / 4000",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false
            };
            footer.Controls.Add(_charCount, 1, 0);

            _sendBtn = new ModernButton
            {
                Text = "Envoyer ↑",
                Dock = DockStyle.Fill,
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
                Enabled = false,
                Margin = new Padding(5, 2, 0, 2),
                Height = 34
            };
            footer.Controls.Add(_sendBtn, 2, 0);
            layout.Controls.Add(footer, 0, 1);
            _sendBtn.Click += (s, e) =>
            {
                if (_isGenerating)
                    CancelClicked?.Invoke(this, EventArgs.Empty);
                else
                    SendClicked?.Invoke(this, EventArgs.Empty);
            };

            Controls.Add(layout);
        }

        public void SetReady(bool ready)
        {
            _ready = ready;
            UpdateInteractionState();
        }

        public void SetModelSelected(bool selected, string modelName = "")
        {
            _modelSelected = selected;
            if (selected)
            {
                _modelLabel.Text = "[MODELE] " + modelName;
                _modelLabel.ForeColor = ThemeColors.Success;
            }
            else
            {
                _modelLabel.Text = "Aucun modele selectionne";
                _modelLabel.ForeColor = ThemeColors.TextMuted;
            }
            UpdateInteractionState();
        }

        public string GetInputText() => _inputBox.Text.Trim();
        public void ClearInput() => _inputBox.Clear();

        public void SetGenerating(bool generating)
        {
            _isGenerating = generating;
            if (!generating)
                _isCancelling = false;
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
            _inputBox.Enabled = _ready && !_isGenerating;
            _sendBtn.Text = _isCancelling ? "Arrêt…" : _isGenerating ? "Annuler" : "Envoyer";
        }

        private void UpdateCharCount()
        {
            var len = _inputBox.Text.Length;
            _charCount.Text = len + " / 4000";
            _charCount.ForeColor = len > 3800 ? ThemeColors.Error : len > 3500 ? ThemeColors.Warning : ThemeColors.TextMuted;
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            _inputBox.BackColor = ThemeColors.CodeBackground;
            _inputBox.ForeColor = ThemeColors.TextPrimary;
        }
    }

    public class SidebarPanel : RoundedPanel
    {
        public event EventHandler<string> ModelsFolderChanged;
        public event EventHandler<string> ModelSelected;
        public event EventHandler<string> ConversationSelected;
        public event EventHandler NewConversationRequested;
        public event EventHandler<string> DeleteConversationRequested;
        public event EventHandler RefreshModelsRequested;

        private TextBox _folderTextBox;
        private ModernButton _browseBtn;
        private ModernButton _refreshBtn;
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
                RowCount = 7,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 54F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            var brandPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 4, 4), BackColor = Color.Transparent };
            var brandMark = new Label
            {
                Dock = DockStyle.Left,
                Width = 38,
                Text = "L",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold)
            };
            var brandText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Local IA Runner\nAssistant local",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Padding = new Padding(10, 0, 0, 0)
            };
            brandPanel.Controls.Add(brandText);
            brandPanel.Controls.Add(brandMark);
            layout.Controls.Add(brandPanel, 0, 0);

            // Folder selector
            var folderPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8) };
            var folderLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42F));

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

            _refreshBtn = new ModernButton
            {
                Text = "⟳",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 4, 0),
                Font = new Font("Segoe UI", 12f),
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
            };
            _refreshBtn.Click += (s, e) => RefreshModelsRequested?.Invoke(this, EventArgs.Empty);
            folderLayout.Controls.Add(_refreshBtn, 2, 0);

            folderPanel.Controls.Add(folderLayout);
            layout.Controls.Add(folderPanel, 0, 1);

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
            layout.Controls.Add(_modelsTitle, 0, 2);

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
            layout.Controls.Add(modelsContainer, 0, 3);

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
            layout.Controls.Add(_convsTitle, 0, 4);

            _conversationsListBox = new DoubleBufferedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9f),
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.One,
                ItemHeight = 72,
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
            layout.Controls.Add(_conversationsListBox, 0, 5);

            _newConvBtn = new ModernButton
            {
                Text = "+ Nouvelle conversation",
                Dock = DockStyle.Fill,
                Margin = new Padding(4),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
                CornerRadius = 10
            };
            _newConvBtn.Click += (s, e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_newConvBtn, 0, 6);

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
            _refreshBtn.Enabled = !loading;
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
                selected ? Color.White : ThemeColors.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, typeText, metaFont,
                new Rectangle(rect.X + 12, rect.Y + 26, rect.Width - 24, 18),
                selected ? Color.White : ThemeColors.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var pathText = model.Folder ?? model.Path;
            if (pathText.Length > 60) pathText = "..." + pathText.Substring(pathText.Length - 57);
            
            TextRenderer.DrawText(g, pathText, pathFont,
                new Rectangle(rect.X + 12, rect.Y + 44, rect.Width - 24, 18),
                selected ? Color.White : ThemeColors.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

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
            TextRenderer.DrawText(g, name, titleFont,
                new Rectangle(rect.X + 12, rect.Y + 5, rect.Width - 24, 24),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (!string.IsNullOrEmpty(preview))
            {
                TextRenderer.DrawText(g, preview, previewFont,
                    new Rectangle(rect.X + 12, rect.Y + 33, rect.Width - 24, 21),
                    selected ? Color.White : ThemeColors.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (!string.IsNullOrEmpty(timeStr))
            {
                TextRenderer.DrawText(g, timeStr, timeFont,
                    new Rectangle(rect.X + 12, rect.Y + 58, rect.Width - 24, 16),
                    selected ? Color.White : ThemeColors.TextSecondary,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
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
        public event EventHandler SearchKnownModelsClicked;
        public event EventHandler SearchAllModelsClicked;
        public event EventHandler CancelModelSearchClicked;
        public event EventHandler RestartBackendClicked;
        public event EventHandler ExitClicked;
        public event EventHandler SettingsClicked;
        public event EventHandler ImageGeneratorClicked;
        public event EventHandler? ForumClicked;
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
        private ModernTextBox _systemPromptTextBox;
        private ModernTextBox _conversationTitlePromptTextBox = null!;
        private ModernTextBox _defaultTextModelPathTextBox = null!;
        private ModernTextBox _defaultImageModelPathTextBox = null!;
        private CheckBox _webAccessCheckBox = null!;
        private CheckBox _codeExecutionCheckBox = null!;
        private CheckBox _forumEnabledCheckBox = null!;
        private CheckBox _subagentsEnabledCheckBox = null!;
        private CheckBox _autoUpdateCheckBox = null!;
        private CheckBox _searchAllModelDrivesCheckBox = null!;
        private CheckBox _ssdModelMemoryCheckBox = null!;
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
                SearchAllDrivesForModels = currentSettings.SearchAllDrivesForModels,
                EnableWebAccess = currentSettings.EnableWebAccess,
                EnableCodeExecution = currentSettings.EnableCodeExecution,
                EnableForum = currentSettings.EnableForum,
                EnableSubagents = currentSettings.EnableSubagents,
                AutoUpdateEnabled = currentSettings.AutoUpdateEnabled,
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
                ReasoningEffort = currentSettings.ReasoningEffort ?? "default",
                GenerationTimeoutSeconds = Math.Clamp(currentSettings.GenerationTimeoutSeconds, 30, 7200),
                CodeExecutionTimeoutSeconds = Math.Clamp(currentSettings.CodeExecutionTimeoutSeconds, 1, 7200)
            };

            InitializeComponent();
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

            AddSection(content, "MODÈLES PAR DÉFAUT");
            _defaultTextModelPathTextBox = CreateModelPathTextBox();
            content.Controls.Add(CreateSettingCard(
                "Modèle de texte par défaut",
                "Modèle GGUF sélectionné au démarrage. Si le champ est vide ou que le fichier manque, l'application choisit le premier GGUF détecté.",
                CreateModelPathPicker(_defaultTextModelPathTextBox, BrowseDefaultTextModel),
                editorBelowTitle: true,
                height: 112));

            _defaultImageModelPathTextBox = CreateModelPathTextBox();
            content.Controls.Add(CreateSettingCard(
                "Modèle d'image par défaut",
                "Fichier principal .safetensors, .gguf ou .ckpt. Place-le sous checkpoints ou diffusion_models; le profil compatible sera choisi à l'ouverture.",
                CreateModelPathPicker(_defaultImageModelPathTextBox, BrowseDefaultImageModel),
                editorBelowTitle: true,
                height: 112));

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
                Text = "Activées",
                AutoSize = true,
                ForeColor = ThemeColors.TextPrimary,
                BackColor = ThemeColors.Surface,
                FlatStyle = FlatStyle.Flat
            };
            content.Controls.Add(CreateSettingCard(
                "Mise à jour automatique depuis GitHub",
                "Télécharge la dernière release et vérifie son SHA-256. Si une génération est en cours, l'application attend sa fin, s'installe puis redémarre. Le fichier téléchargé est supprimé après l'installation.",
                _autoUpdateCheckBox));

            AddSection(content, "INSTRUCTIONS ET MÉMOIRE");
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
                "Autorise le modèle à lire/écrire dans le dossier choisi et à lancer du Python avec les droits de votre session Windows.",
                _codeExecutionCheckBox));

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

            AddSection(content, "DÉLAIS DES OUTILS");
            _generationTimeoutUpDown = CreateNumericControl(30, 7200, 10, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Délai d’inactivité de génération (secondes)",
                "Annule une génération si aucun token ni avancement d’outil n’arrive. Valeur par défaut : 900 secondes (15 minutes). Réglable de 30 secondes à 2 heures.",
                _generationTimeoutUpDown));

            _codeExecutionTimeoutUpDown = CreateNumericControl(1, 7200, 1, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Délai maximal d’exécution Python (secondes)",
                "Plafond du temps accordé à chaque script Python. Le modèle peut demander moins, mais ne peut pas dépasser cette valeur. Valeur par défaut : 30 secondes.",
                _codeExecutionTimeoutUpDown));

            _contextSizeUpDown = CreateNumericControl(512, 131072, 512, 0, 150);
            content.Controls.Add(CreateSettingCard(
                "Taille du contexte",
                "Nombre maximal de tokens que le modèle peut prendre en compte. Une grande valeur utilise plus de mémoire.",
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
                "Charger les poids du modèle depuis le SSD (mmap)",
                "Activé par défaut et recommandé, surtout pour les modèles volumineux. Le mapping GGUF réduit la RAM résidente mais peut ralentir les accès disque; évitez de le désactiver sauf si vous avez assez de RAM. Appliqué au prochain chargement du modèle.",
                _ssdModelMemoryCheckBox));

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
            var card = new Panel
            {
                Width = cardWidth,
                Height = height,
                BackColor = ThemeColors.Surface,
                Margin = new Padding(0, 0, 0, 8)
            };
            var titleLabel = new Label
            {
                Text = title,
                Location = new Point(12, 6),
                Width = editorBelowTitle ? cardWidth - 24 : cardWidth - 190,
                Height = 22,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var descriptionLabel = new Label
            {
                Text = description,
                Location = new Point(12, 30),
                Width = cardWidth - 24,
                Height = editorBelowTitle ? 32 : 38,
                ForeColor = ThemeColors.TextSecondary,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.TopLeft
            };
            card.Controls.Add(titleLabel);
            card.Controls.Add(descriptionLabel);

            if (editorBelowTitle)
            {
                editor.Location = new Point(12, 65);
                editor.Width = cardWidth - 24;
                editor.Height = Math.Max(30, height - 74);
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

        private static void SetNumericValue(ModernNumericUpDown control, decimal value)
        {
            control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
        }

        private void LoadSettings()
        {
            _conversationTitlePromptTextBox.Text = Settings.ConversationTitlePrompt ?? AppSettings.DefaultConversationTitlePrompt;
            _systemPromptTextBox.Text = Settings.SystemPrompt ?? "";
            _defaultTextModelPathTextBox.Text = Settings.DefaultTextModelPath ?? "";
            _defaultImageModelPathTextBox.Text = Settings.DefaultImageModelPath ?? "";
            _searchAllModelDrivesCheckBox.Checked = Settings.SearchAllDrivesForModels;
            _webAccessCheckBox.Checked = Settings.EnableWebAccess;
            _codeExecutionCheckBox.Checked = Settings.EnableCodeExecution;
            _forumEnabledCheckBox.Checked = Settings.EnableForum;
            _subagentsEnabledCheckBox.Checked = Settings.EnableSubagents;
            _autoUpdateCheckBox.Checked = Settings.AutoUpdateEnabled;
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
            var reasoningEffortValues = new[] { "default", "none", "minimal", "low", "medium", "high", "xhigh", "max" };
            var selectedEffort = Array.IndexOf(reasoningEffortValues,
                (Settings.ReasoningEffort ?? "default").Trim().ToLowerInvariant());
            _reasoningEffortComboBox.SelectedIndex = selectedEffort >= 0 ? selectedEffort : 0;
        }

        private void SaveSettings()
        {
            Settings.ConversationTitlePrompt = _conversationTitlePromptTextBox.Text;
            Settings.SystemPrompt = _systemPromptTextBox.Text;
            Settings.DefaultTextModelPath = _defaultTextModelPathTextBox.Text.Trim();
            Settings.DefaultImageModelPath = _defaultImageModelPathTextBox.Text.Trim();
            Settings.SearchAllDrivesForModels = _searchAllModelDrivesCheckBox.Checked;
            Settings.EnableWebAccess = _webAccessCheckBox.Checked;
            Settings.EnableCodeExecution = _codeExecutionCheckBox.Checked;
            Settings.EnableForum = _forumEnabledCheckBox.Checked;
            Settings.EnableSubagents = _subagentsEnabledCheckBox.Checked;
            Settings.AutoUpdateEnabled = _autoUpdateCheckBox.Checked;
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
