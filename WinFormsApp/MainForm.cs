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
        private string _selectedModelPath = "";
        private string _modelsFolder = "";
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
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(900, 600);
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
            var mainContainer = new Panel { Dock = DockStyle.Fill, BackColor = ThemeColors.Background, Padding = new Padding(8) };
            
            // Sidebar - Fixed width, Dock Left
            _sidebarPanel = new SidebarPanel { Dock = DockStyle.Left, Width = 340, Margin = new Padding(0, 0, 8, 0) };
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
            chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180F)); // header(32) + input(80) + padding(16*2) + border

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
                        ScanModelsFolder();
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
                    ScanModelsFolder();
                }
            }
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

        private void ScanModelsFolder()
        {
            _sidebarPanel.SetModelsLoading(true);
            _statusBar.SetStatus("Scan du dossier modeles...", StatusType.Info);
            
            // Debug - immediate visible feedback
            
            Task.Run(() =>
            {
                try
                {
                    var models = new List<LocalModelInfo>();
                    var skippedMmprojCount = 0;
                    
                    try
                    {
                    var folderExists = Directory.Exists(_modelsFolder);
                    
                    if (!folderExists)
                    {
                        BeginInvoke(new Action(() => _chatPanel.AddSystemMessage("Dossier introuvable: " + _modelsFolder, MessageType.Warning)));
                    }
                    else
                    {
                        // 1. Scan ROOT folder for Transformers models (models directly in root)
                        var rootModel = DetectModel(_modelsFolder);
                        if (rootModel != null)
                        {
                            models.Add(rootModel);
                        }
                        
                        // 2. Scan immediate subdirectories for Transformers models
                        var dirs = Directory.GetDirectories(_modelsFolder);
                        
                        foreach (var dir in dirs)
                        {
                            var modelInfo = DetectModel(dir);
                            if (modelInfo != null)
                            {
                                models.Add(modelInfo);
                            }
                            else
                            {
                                Invoke(() => _chatPanel.AddSystemMessage("Pas de modèle dans: " + Path.GetFileName(dir), MessageType.Info));
                            }
                        }
                        
                        // 2. Scan for GGUF files - only 2 levels deep
                        var ggufFiles = new List<string>();
                        try
                        {
                            // Level 1: directly in models folder
                            var level1 = Directory.GetFiles(_modelsFolder, "*.gguf", SearchOption.TopDirectoryOnly);
                            ggufFiles.AddRange(level1);
                            
                            
                            // Level 2: in immediate subdirectories
                            foreach (var subDir in dirs)
                            {
                                try
                                {
                                    var level2 = Directory.GetFiles(subDir, "*.gguf", SearchOption.TopDirectoryOnly);
                                    ggufFiles.AddRange(level2);
                                    if (level2.Length > 0)
                                    {
                                    }
                                        
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                        }
                        
                        foreach (var file in ggufFiles)
                        {
                            try
                            {
                                if (IsMmprojFile(file))
                                {
                                    skippedMmprojCount++;
                                    continue;
                                }

                                models.Add(new LocalModelInfo
                                {
                                    Name = Path.GetFileNameWithoutExtension(file),
                                    Path = file,
                                    Type = ModelType.GGUF,
                                    Size = new FileInfo(file).Length,
                                    Folder = Path.GetDirectoryName(file)
                                });
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                }


                BeginInvoke(new Action(() => 
                {
                    var defaultTextModelPath = _appSettings.DefaultTextModelPath;
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
                                Path = defaultTextModelPath,
                                Type = ModelType.GGUF,
                                Size = new FileInfo(defaultTextModelPath).Length,
                                Folder = Path.GetDirectoryName(defaultTextModelPath) ?? ""
                            });
                        }
                        catch { }
                    }

                    models = models
                        .GroupBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
                    var preferredModel = models.FirstOrDefault(model =>
                            model.Type == ModelType.GGUF &&
                            string.Equals(model.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase))
                        ?? models.FirstOrDefault(model =>
                            model.Type == ModelType.GGUF &&
                            string.Equals(model.Path, defaultTextModelPath, StringComparison.OrdinalIgnoreCase))
                        ?? models.FirstOrDefault(model => model.Type == ModelType.GGUF);
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
                    _statusBar.SetStatus(models.Count + " modele(s) trouve(s)", models.Count > 0 ? StatusType.Success : StatusType.Warning);

                    if (skippedMmprojCount > 0)
                    {
                        _chatPanel.AddSystemMessage(
                            $"{skippedMmprojCount} fichier(s) mmproj ignoré(s) : ce sont des projecteurs multimodaux, pas des modèles principaux. Le backend actuel ne prend pas encore en charge leur chargement.",
                            MessageType.Info);
                    }
                    
                }));
            }
            catch (Exception taskEx)
            {
            }
        });
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
                            break;
                        case "generation_start":
                            MarkGenerationProgress();
                            _isStreamingResponse = true;
                            break;
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
                            break;
                        case "image_error":
                            _imageGenerationActive = false;
                            _imageGeneratorForm?.SetCompleted(new List<string>(), msg.Content ?? "Erreur inconnue", false);
                            _statusBar.SetStatus("Erreur génération image", StatusType.Error);
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
                    _appSettings = settingsForm.Settings;
                    SaveAppSettings();
                    _statusBar.SetStatus("Parametres sauvegardes", StatusType.Success);
                }
            }
        }

        private async void OnCheckForUpdates(object sender, EventArgs e)
        {
            await CheckForUpdatesAsync(userInitiated: true);
        }

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            if (_updateCheckInProgress || _updateHelperLaunched)
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
                GitHubUpdater.StartApplyHelper(packagePath);
                _updateHelperLaunched = true;
                _statusBar.SetStatus(
                    $"Mise à jour {release.Version} téléchargée. Elle sera appliquée à la fermeture de l'application.",
                    StatusType.Success);

                if (userInitiated)
                {
                    MessageBox.Show(this,
                        $"La mise à jour {release.Version} est téléchargée. Elle sera installée automatiquement quand tu fermeras l'application.",
                        "Mise à jour prête", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
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
                _sidebarPanel.SetConversationTitlePreview(Path.GetFileName(file), preview);
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
                        _inputPanel.SetModelSelected(true, GetModelDisplayName(data.ModelPath));
                    }
                    _currentConversationFile = file;

                    _chatPanel.Clear();
                    foreach (var msg in _currentConversation)
                    {
                        _chatPanel.AddMessage(msg.Role == "user" ? "Vous :" : "Assistant :", msg.Content, msg.Role == "user");
                    }
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
            SaveConversation();
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

    // ===== THEME - EXACT MATCH FROM HTML =====
    public static class ThemeColors
    {
        public static readonly Color Background = Color.FromArgb(0x12, 0x12, 0x14);      // #121214
        public static readonly Color Surface = Color.FromArgb(0x1C, 0x1C, 0x1E);        // #1C1C1E
        public static readonly Color SurfaceHover = Color.FromArgb(0x26, 0x26, 0x2A);   // #26262A
        public static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x3E);         // #3A3A3E
        public static readonly Color BorderLight = Color.FromArgb(0x4E, 0x4E, 0x52);    // #4E4E52
        public static readonly Color Primary = Color.FromArgb(0x00, 0x7A, 0xFF);        // #007AFF
        public static readonly Color PrimaryHover = Color.FromArgb(0x14, 0x87, 0xFF);   // #1487FF
        public static readonly Color PrimaryPressed = Color.FromArgb(0x00, 0x64, 0xD2); // #0064D2
        public static readonly Color Success = Color.FromArgb(0x28, 0xB4, 0x63);        // #28B463
        public static readonly Color Warning = Color.FromArgb(0xFF, 0x9F, 0x0A);        // #FF9F0A
        public static readonly Color Error = Color.FromArgb(0xFF, 0x45, 0x3A);          // #FF453A
        public static readonly Color TextPrimary = Color.FromArgb(0xFF, 0xFF, 0xFF);    // #FFFFFF
        public static readonly Color TextSecondary = Color.FromArgb(0xAE, 0xAE, 0xB2);  // #AEAEB2
        public static readonly Color TextMuted = Color.FromArgb(0xA8, 0xA8, 0xAD);      // #A8A8AD
        public static readonly Color Thinking = Color.FromArgb(0xA0, 0xA0, 0xA4);       // #A0A0A4 - Visible gray for thinking
        public static readonly Color UserBubble = Color.FromArgb(0x00, 0x7A, 0xFF);     // #007AFF
        public static readonly Color AssistantBubble = Color.FromArgb(0x30, 0x30, 0x34);// #303034
        public static readonly Color CodeBackground = Color.FromArgb(0x14, 0x14, 0x16); // #141416
        public static readonly Color ScrollBar = Color.FromArgb(0x30, 0x30, 0x34);      // #303034
        public static readonly Color ScrollBarHover = Color.FromArgb(0x44, 0x44, 0x48); // #444448
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

    public class SmoothScrollRichTextBox : RichTextBox
    {
        public bool UserAtBottom { get; private set; } = true;

        public SmoothScrollRichTextBox()
        {
            this.VScroll += OnVScroll;
        }

        private void OnVScroll(object sender, EventArgs e)
        {
            // Check if scrolled to bottom
            var pos = GetPositionFromCharIndex(TextLength);
            UserAtBottom = pos.Y >= ClientRectangle.Height - Font.Height;
        }

        // Override OnVScroll to track thumb tracking
        protected override void OnVScroll(EventArgs e)
        {
            base.OnVScroll(e);
            // When user starts dragging thumb, they're not at bottom
            UserAtBottom = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // Check if at bottom after wheel scroll
            var pos = GetPositionFromCharIndex(TextLength);
            UserAtBottom = pos.Y >= ClientRectangle.Height - Font.Height;
        }
    }

    // ===== CUSTOM CONTROLS - PIXEL PERFECT =====

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

    public class ChatPanel : DoubleBufferedPanel
    {
        private SmoothScrollRichTextBox _chatDisplay;
        private TableLayoutPanel _chatLayout;
        private RoundedPanel _toolActivityPanel;
        private Label _toolActivityIcon;
        private Label _toolActivityText;
        private readonly StringBuilder _streamedThinkingPreview = new();
        private bool _showingThinkingPreview;
        private bool _isThinking = false;
        private readonly StringBuilder _pendingStreamLineBreaks = new();
        private string _pendingThinkingMarkup = "";
        private readonly Dictionary<(bool IsEmoji, float Size, FontStyle Style), Font> _messageFonts = new();
        private readonly List<CodeActionRange> _codeActionRanges = new();
        private readonly List<LinkActionRange> _linkActionRanges = new();
        private readonly List<ThinkingBlockRange> _thinkingBlockRanges = new();
        private ThinkingBlockRange? _activeThinkingBlock;
        private readonly Font _codeFont = new("Consolas", 9f);

        public ChatPanel()
        {
            BackColor = ThemeColors.Surface;
            Padding = new Padding(16);
            
            _chatDisplay = new SmoothScrollRichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = ThemeColors.Surface,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI", 10f),
                BorderStyle = BorderStyle.None,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                HideSelection = true
            };
            _chatDisplay.LinkClicked += (s, e) => 
            {
                if (!string.IsNullOrEmpty(e.LinkText))
                    try { Process.Start(new ProcessStartInfo(e.LinkText) { UseShellExecute = true }); } catch { }
            };
            _chatDisplay.MouseClick += OnChatDisplayMouseClick;
            _chatDisplay.MouseMove += OnChatDisplayMouseMove;
            _chatDisplay.MouseLeave += (s, e) => _chatDisplay.Cursor = Cursors.IBeam;

            _chatLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _chatLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _chatLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _chatLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            _chatLayout.Controls.Add(_chatDisplay, 0, 0);

            _toolActivityPanel = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                BorderColor = ThemeColors.Border,
                BorderWidth = 1,
                BackColor = ThemeColors.SurfaceHover,
                Padding = new Padding(8, 3, 10, 3),
                Margin = new Padding(0, 6, 0, 0),
                Visible = false
            };
            var activityLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24f));
            activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76f));

            _toolActivityIcon = new Label
            {
                Dock = DockStyle.Fill,
                Text = "●",
                ForeColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _toolActivityText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "",
                ForeColor = ThemeColors.TextSecondary,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            var activityTag = new Label
            {
                Dock = DockStyle.Fill,
                Text = "EN COURS",
                ForeColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight
            };
            activityLayout.Controls.Add(_toolActivityIcon, 0, 0);
            activityLayout.Controls.Add(_toolActivityText, 1, 0);
            activityLayout.Controls.Add(activityTag, 2, 0);
            _toolActivityPanel.Controls.Add(activityLayout);
            _chatLayout.Controls.Add(_toolActivityPanel, 0, 1);

            Controls.Add(_chatLayout);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
                return;

            foreach (var font in _messageFonts.Values)
                font.Dispose();
            _messageFonts.Clear();
            _codeFont.Dispose();
        }

        public void UpdateTheme()
        {
            BackColor = ThemeColors.Surface;
            _chatDisplay.BackColor = ThemeColors.Surface;
            _chatDisplay.ForeColor = ThemeColors.TextPrimary;
            _toolActivityPanel.BackColor = ThemeColors.SurfaceHover;
            _toolActivityPanel.BorderColor = ThemeColors.Border;
            _toolActivityIcon.ForeColor = _showingThinkingPreview ? ThemeColors.Thinking : ThemeColors.Primary;
            _toolActivityText.ForeColor = _showingThinkingPreview ? ThemeColors.Thinking : ThemeColors.TextSecondary;
        }

        public void Clear()
        {
            _chatDisplay.Clear();
            _codeActionRanges.Clear();
            _linkActionRanges.Clear();
            _thinkingBlockRanges.Clear();
            _activeThinkingBlock = null;
            _isStreaming = false;
            _pendingThinkingMarkup = "";
            _isThinking = false;
            _streamedThinkingPreview.Clear();
            ClearToolActivityMessage();
        }

        public void RenderMessages(IEnumerable<Message> messages)
        {
            Clear();
            foreach (var message in messages ?? Enumerable.Empty<Message>())
            {
                AddMessage(
                    message.Role == "user" ? "Vous :" : "Assistant :",
                    message.Content ?? "",
                    message.Role == "user");
            }
            _chatDisplay.ScrollToCaret();
        }

        public void AddMessage(string sender, string content, bool isUser)
        {
            var thinkingPreview = !isUser && !(content ?? "").Contains("<think>", StringComparison.Ordinal)
                ? _streamedThinkingPreview.ToString()
                : "";
            _streamedThinkingPreview.Clear();
            ClearToolActivityMessage();
            var time = DateTime.Now.ToString("HH:mm");
            content = NormalizeMessageText(content ?? "");
            
            AppendStyled("[" + time + "] ", ThemeColors.TextMuted, false);
            var senderColor = isUser ? ThemeColors.Primary : ThemeColors.Success;
            AppendStyled(sender + " ", senderColor, true);
            if (isUser)
                AppendInlineMarkdown(content, ThemeColors.TextPrimary, bold: false, italic: false, fontSize: 10f);
            else
            {
                if (thinkingPreview.Length > 0)
                    AppendCollapsedThinkingContent(thinkingPreview);
                AppendAssistantContent(content);
            }
            AppendStyled("\n\n", ThemeColors.TextPrimary, false);
            
            _chatDisplay.ScrollToCaret();
        }

        public void AddSystemMessage(string content, MessageType type)
        {
            content = NormalizeMessageText(content);
            var color = type switch
            {
                MessageType.Success => ThemeColors.Success,
                MessageType.Warning => ThemeColors.Warning,
                MessageType.Error => ThemeColors.Error,
                MessageType.Info => ThemeColors.TextMuted,
                _ => ThemeColors.TextSecondary
            };
            
            var prefix = type switch
            {
                MessageType.Success => "[OK] ",
                MessageType.Warning => "[ATTENTION] ",
                MessageType.Error => "[ERREUR] ",
                MessageType.Info => "[DEBUG] ",
                _ => "[INFO] "
            };
            
            AppendStyled("[" + DateTime.Now.ToString("HH:mm") + "] " + prefix + content + "\n\n", color, false);
            _chatDisplay.ScrollToCaret();
        }

        public void AddToolStatusMessage(string content)
        {
            content ??= "";
            _showingThinkingPreview = false;
            _toolActivityIcon.ForeColor = ThemeColors.Primary;
            _toolActivityText.ForeColor = ThemeColors.TextSecondary;
            _toolActivityText.Text = NormalizeMessageText(content);
            _toolActivityIcon.Text = content.Contains("Exécution", StringComparison.OrdinalIgnoreCase)
                ? "▶"
                : content.Contains("code", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("fichier", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("dossier", StringComparison.OrdinalIgnoreCase)
                    ? "{}"
                    : "⌕";
            _toolActivityPanel.Visible = true;
            _chatLayout.RowStyles[1].Height = 42f;
            _chatLayout.PerformLayout();
        }

        public void AppendThinkingPreviewToken(string content)
        {
            if (string.IsNullOrEmpty(content))
                return;

            _streamedThinkingPreview.Append(content);
            if (!_isStreaming)
            {
                _isStreaming = true;
                _isThinking = false;
                _pendingStreamLineBreaks.Clear();
                _pendingThinkingMarkup = "";
                AppendStreamingMessageHeader();
            }

            if (_activeThinkingBlock == null || !_activeThinkingBlock.IsPreview)
            {
                FinalizeThinkingBlock();
                BeginThinkingBlock();
                if (_activeThinkingBlock != null)
                    _activeThinkingBlock.IsPreview = true;
            }
            AppendThinkingBlockContent(content);

            ShowThinkingActivity();
        }

        public void ShowThinkingActivity()
        {
            if (_showingThinkingPreview && _toolActivityPanel.Visible &&
                string.Equals(_toolActivityText.Text, "Réflexion en cours…", StringComparison.Ordinal))
                return;

            _showingThinkingPreview = true;
            _toolActivityIcon.Text = "…";
            _toolActivityIcon.ForeColor = ThemeColors.Thinking;
            _toolActivityText.ForeColor = ThemeColors.Thinking;

            _toolActivityText.Text = "Réflexion en cours…";
            _toolActivityPanel.Visible = true;
            _chatLayout.RowStyles[1].Height = 42f;
            _chatLayout.PerformLayout();
            if (_chatDisplay.UserAtBottom)
                _chatDisplay.ScrollToCaret();
        }

        public void ClearThinkingPreview()
        {
            if (_activeThinkingBlock?.IsPreview == true)
                RemoveThinkingBlock(_activeThinkingBlock);
            _streamedThinkingPreview.Clear();
            if (_showingThinkingPreview)
                ClearToolActivityMessage();
            _showingThinkingPreview = false;
        }

        private void ClearToolActivityMessage()
        {
            if (_toolActivityPanel == null || _chatLayout == null)
                return;
            _showingThinkingPreview = false;
            _toolActivityPanel.Visible = false;
            _toolActivityText.Text = "";
            _chatLayout.RowStyles[1].Height = 0f;
            _chatLayout.PerformLayout();
        }

        private static string NormalizeMessageText(string text)
        {
            return (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .TrimEnd('\n');
        }

        private void AppendStyled(string text, Color color, bool bold, bool italic = false,
                                  float fontSize = 10f, bool underline = false,
                                  bool atCurrentPosition = false)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var style = (bold ? FontStyle.Bold : FontStyle.Regular) |
                        (italic ? FontStyle.Italic : FontStyle.Regular) |
                        (underline ? FontStyle.Underline : FontStyle.Regular);

            var containsEmojiFontRune = false;
            foreach (var rune in text.EnumerateRunes())
            {
                if (!IsEmojiFontRune(rune.Value))
                    continue;
                containsEmojiFontRune = true;
                break;
            }

            if (!containsEmojiFontRune)
            {
                AppendStyledRun(text, color, style, fontSize, usesEmojiFont: false,
                    atCurrentPosition: atCurrentPosition);
                return;
            }

            var run = new StringBuilder();
            var runUsesEmojiFont = false;
            var hasRun = false;

            foreach (var rune in text.EnumerateRunes())
            {
                var usesEmojiFont = IsEmojiFontRune(rune.Value);
                if (hasRun && usesEmojiFont != runUsesEmojiFont)
                {
                    AppendStyledRun(run.ToString(), color, style, fontSize, runUsesEmojiFont,
                        atCurrentPosition: atCurrentPosition);
                    run.Clear();
                    hasRun = false;
                }

                if (!hasRun)
                {
                    runUsesEmojiFont = usesEmojiFont;
                    hasRun = true;
                }
                run.Append(rune.ToString());
            }

            if (hasRun)
                AppendStyledRun(run.ToString(), color, style, fontSize, runUsesEmojiFont,
                    atCurrentPosition: atCurrentPosition);
        }

        private void AppendStyledRun(string text, Color color, FontStyle style, float fontSize,
                                     bool usesEmojiFont, bool atCurrentPosition = false)
        {
            if (!atCurrentPosition)
                _chatDisplay.SelectionStart = _chatDisplay.TextLength;
            _chatDisplay.SelectionLength = 0;
            _chatDisplay.SelectionColor = color;
            var fontKey = (usesEmojiFont, fontSize, style);
            if (!_messageFonts.TryGetValue(fontKey, out var font))
            {
                font = new Font(usesEmojiFont ? "Segoe UI Emoji" : "Segoe UI", fontSize, style);
                _messageFonts.Add(fontKey, font);
            }
            _chatDisplay.SelectionFont = font;
            if (atCurrentPosition)
                ReplaceChatSelection(text);
            else
                _chatDisplay.AppendText(text);
        }

        private void ReplaceChatSelection(string text)
        {
            var wasReadOnly = _chatDisplay.ReadOnly;
            if (wasReadOnly)
                _chatDisplay.ReadOnly = false;

            try
            {
                _chatDisplay.SelectedText = text;
            }
            finally
            {
                _chatDisplay.ReadOnly = wasReadOnly;
            }
        }

        private int DeleteChatTextRange(int start, int length)
        {
            start = Math.Clamp(start, 0, _chatDisplay.TextLength);
            var expectedLength = Math.Clamp(length, 0, _chatDisplay.TextLength - start);
            var remainingLength = expectedLength;
            var removedLength = 0;

            // RichTextBox can leave a selected range untouched while it is read-only.
            // Retry from the same start if a line-ending conversion removes fewer
            // characters than requested, and report the actual deletion to the caller.
            for (var attempt = 0; attempt < 3 && remainingLength > 0; attempt++)
            {
                var oldTextLength = _chatDisplay.TextLength;
                _chatDisplay.SelectionStart = start;
                _chatDisplay.SelectionLength = Math.Min(remainingLength, _chatDisplay.TextLength - start);
                ReplaceChatSelection("");

                var removedThisAttempt = oldTextLength - _chatDisplay.TextLength;
                if (removedThisAttempt <= 0)
                    break;

                removedThisAttempt = Math.Min(removedThisAttempt, remainingLength);
                removedLength += removedThisAttempt;
                remainingLength -= removedThisAttempt;
            }

            _chatDisplay.SelectionStart = Math.Min(start, _chatDisplay.TextLength);
            _chatDisplay.SelectionLength = 0;
            return removedLength;
        }

        private static bool IsEmojiFontRune(int value)
        {
            return (value >= 0x1F000 && value <= 0x1FAFF) ||
                   (value >= 0x2600 && value <= 0x27FF) ||
                   (value >= 0x1F1E6 && value <= 0x1F1FF) ||
                   value == 0x200D || value == 0xFE0E || value == 0xFE0F;
        }

        private bool _isStreaming = false;
        private int _streamStartPos = 0;

        public void StartStreamingMessage()
        {
            ClearToolActivityMessage();
            var messageAlreadyStarted = _isStreaming;
            _isStreaming = true;
            _isThinking = false;
            _pendingStreamLineBreaks.Clear();
            _pendingThinkingMarkup = "";

            if (!messageAlreadyStarted)
                AppendStreamingMessageHeader();
            else if (_activeThinkingBlock?.IsPreview != true)
                FinalizeThinkingBlock();

            if (_streamedThinkingPreview.Length > 0)
            {
                if (_thinkingBlockRanges.LastOrDefault()?.IsPreview != true)
                {
                    BeginThinkingBlock();
                    if (_activeThinkingBlock != null)
                        _activeThinkingBlock.IsPreview = true;
                    AppendThinkingBlockContent(_streamedThinkingPreview.ToString());
                    FinalizeThinkingBlock();
                }
                _streamedThinkingPreview.Clear();
            }
            
            _streamStartPos = _chatDisplay.TextLength;
            _chatDisplay.ScrollToCaret();
        }

        private void AppendStreamingMessageHeader()
        {
            var time = DateTime.Now.ToString("HH:mm");
            AppendStyled("[" + time + "] ", ThemeColors.TextMuted, false);
            AppendStyled("Assistant :", ThemeColors.Success, true);
            _streamStartPos = _chatDisplay.TextLength;
        }

        public void AppendStreamingToken(string token)
        {
            if (!_isStreaming) return;
            var text = _pendingStreamLineBreaks.Length == 0
                ? token
                : _pendingStreamLineBreaks.ToString() + token;
            _pendingStreamLineBreaks.Clear();

            var contentLength = text.Length;
            while (contentLength > 0 && (text[contentLength - 1] == '\r' || text[contentLength - 1] == '\n'))
                contentLength--;

            if (contentLength < text.Length)
                _pendingStreamLineBreaks.Append(text, contentLength, text.Length - contentLength);

            if (contentLength == 0)
                return;

            if (contentLength < text.Length)
                text = text.Substring(0, contentLength);

            AppendThinkingStreamText(text);
            
            // Smooth scroll - only auto-scroll if user is at bottom
            if (_chatDisplay.UserAtBottom)
                _chatDisplay.ScrollToCaret();
        }

        public void EndStreamingMessage(string fullContent)
        {
            ClearToolActivityMessage();
            System.Diagnostics.Debug.WriteLine($"[ChatPanel.EndStreamingMessage] START, _isStreaming={_isStreaming}, length={_chatDisplay.TextLength - _streamStartPos}");
            if (!_isStreaming) 
            {
                System.Diagnostics.Debug.WriteLine($"[ChatPanel.EndStreamingMessage] Early return - not streaming");
                return;
            }
            
            _isStreaming = false;
            
            try 
            {
                if (_pendingStreamLineBreaks.Length > 0)
                    AppendThinkingStreamText(_pendingStreamLineBreaks.ToString());
                if (_pendingThinkingMarkup.Length > 0)
                {
                    AppendThinkingSegments(_pendingThinkingMarkup, ref _isThinking);
                    _pendingThinkingMarkup = "";
                }
                FinalizeThinkingBlock();
                AppendStyled("\n\n", ThemeColors.TextPrimary, false);
                System.Diagnostics.Debug.WriteLine($"[ChatPanel.EndStreamingMessage] SUCCESS");
            } 
            catch (Exception ex) 
            {
                System.Diagnostics.Debug.WriteLine($"[ChatPanel.EndStreamingMessage ERROR] {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _isStreaming = false;
                _isThinking = false;
                _pendingThinkingMarkup = "";
                _pendingStreamLineBreaks.Clear();
                _streamedThinkingPreview.Clear();
                _showingThinkingPreview = false;
            }
        }

        private void AppendThinkingStreamText(string text)
        {
            var combined = _pendingThinkingMarkup + text;
            _pendingThinkingMarkup = "";

            var processLength = combined.Length;
            var markers = new[] { "<think>", "</think>" };
            for (var suffixLength = Math.Min(7, combined.Length); suffixLength > 0; suffixLength--)
            {
                var suffix = combined.Substring(combined.Length - suffixLength, suffixLength);
                if (markers.Any(marker => marker.StartsWith(suffix, StringComparison.Ordinal)))
                {
                    processLength = combined.Length - suffixLength;
                    break;
                }
            }

            var incompleteLinkStart = FindIncompleteMarkdownLinkStart(combined);
            if (incompleteLinkStart >= 0)
                processLength = Math.Min(processLength, incompleteLinkStart);

            if (processLength < combined.Length)
                _pendingThinkingMarkup = combined.Substring(processLength);
            AppendThinkingSegments(combined.Substring(0, processLength), ref _isThinking);
        }

        private void AppendThinkingSegments(string text, ref bool isThinking)
        {
            var position = 0;
            while (position < text.Length)
            {
                var openIndex = text.IndexOf("<think>", position, StringComparison.Ordinal);
                var closeIndex = text.IndexOf("</think>", position, StringComparison.Ordinal);
                var isOpening = openIndex >= 0 && (closeIndex < 0 || openIndex < closeIndex);
                var tagIndex = isOpening ? openIndex : closeIndex;
                if (tagIndex < 0)
                {
                    var remaining = text.Substring(position);
                    if (isThinking)
                        AppendThinkingBlockContent(remaining);
                    else
                    {
                        // A streamed response token marks the end of the live reasoning
                        // preview. Finalize it before appending answer text so a collapsed
                        // preview cannot absorb the answer at its hidden body position.
                        if (_activeThinkingBlock?.IsPreview == true)
                            FinalizeThinkingBlock();
                        AppendThinkingStyled(remaining, isThinking: false);
                    }
                    break;
                }

                if (tagIndex > position)
                {
                    var segment = text.Substring(position, tagIndex - position);
                    if (isThinking)
                        AppendThinkingBlockContent(segment);
                    else
                    {
                        if (_activeThinkingBlock?.IsPreview == true)
                            FinalizeThinkingBlock();
                        AppendThinkingStyled(segment, isThinking: false);
                    }
                }

                if (isOpening)
                {
                    FinalizeThinkingBlock();
                    isThinking = true;
                }
                else
                {
                    FinalizeThinkingBlock();
                    isThinking = false;
                }
                position = tagIndex + (isOpening ? "<think>".Length : "</think>".Length);
            }
        }

        private void AppendThinkingStyled(string text, bool isThinking, bool atCurrentPosition = false)
        {
            AppendInlineMarkdown(text,
                isThinking ? ThemeColors.Thinking : ThemeColors.TextPrimary,
                bold: false,
                italic: isThinking,
                fontSize: isThinking ? 9f : 10f,
                atCurrentPosition: atCurrentPosition);
        }

        private void BeginThinkingBlock()
        {
            if (_activeThinkingBlock != null)
                return;

            var textLengthBeforeSeparator = _chatDisplay.TextLength;
            if (_chatDisplay.TextLength > 0 && !char.IsWhiteSpace(_chatDisplay.Text[_chatDisplay.TextLength - 1]))
                AppendStyled(" ", ThemeColors.TextPrimary, false);
            var leadingSpaceLength = _chatDisplay.TextLength - textLengthBeforeSeparator;

            const string header = "▸ Réflexion";
            var block = new ThinkingBlockRange
            {
                Start = _chatDisplay.TextLength,
                HeaderLength = header.Length,
                LeadingSpaceLength = leadingSpaceLength
            };
            AppendStyled(header, ThemeColors.Thinking, bold: false, italic: true, fontSize: 9f);
            AppendStyled("\n", ThemeColors.Thinking, bold: false, italic: true, fontSize: 9f);
            _chatDisplay.SelectionStart = _chatDisplay.TextLength;
            block.BodyStart = _chatDisplay.SelectionStart;
            _thinkingBlockRanges.Add(block);
            _activeThinkingBlock = block;
        }

        private void RemoveThinkingBlock(ThinkingBlockRange block)
        {
            var deleteStart = Math.Max(0, block.Start - block.LeadingSpaceLength);
            var deleteEnd = block.BodyStart + block.BodyLength;
            if (deleteEnd <= deleteStart)
                return;

            var deletedLength = DeleteChatTextRange(deleteStart, deleteEnd - deleteStart);
            if (deletedLength != deleteEnd - deleteStart)
                return;
            _codeActionRanges.RemoveAll(item =>
                item.Start < deleteEnd && item.Start + item.Length > block.BodyStart);
            _linkActionRanges.RemoveAll(item =>
                item.Start < deleteEnd && item.Start + item.Length > block.BodyStart);
            _thinkingBlockRanges.Remove(block);
            ShiftRangesAfterDeletion(deleteEnd, deleteEnd - deleteStart);
            if (_activeThinkingBlock == block)
                _activeThinkingBlock = null;
        }

        private void AppendThinkingBlockContent(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (_activeThinkingBlock == null)
                BeginThinkingBlock();

            var block = _activeThinkingBlock;
            if (block == null)
                return;

            block.Content.Append(text);
            if (!block.IsExpanded)
                return;

            var insertionIndex = block.BodyStart + block.BodyLength;
            var oldTextLength = _chatDisplay.TextLength;
            var existingCodeActionCount = _codeActionRanges.Count;
            var existingLinkActionCount = _linkActionRanges.Count;
            _chatDisplay.SelectionStart = insertionIndex;
            _chatDisplay.SelectionLength = 0;
            AppendThinkingStyled(text, isThinking: true, atCurrentPosition: true);
            var insertedLength = GetInsertedTextLength(insertionIndex, oldTextLength);
            block.BodyLength += insertedLength;
            ShiftRangesAfterInsertion(insertionIndex, insertedLength,
                existingCodeActionCount, existingLinkActionCount);
        }

        private void AppendCollapsedThinkingContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return;

            BeginThinkingBlock();
            AppendThinkingBlockContent(content);
            FinalizeThinkingBlock();
        }

        private void FinalizeThinkingBlock()
        {
            var block = _activeThinkingBlock;
            if (block == null)
                return;

            var separatorLength = GetThinkingSeparatorLength(block.Content.ToString());
            if (block.IsExpanded && separatorLength > 0)
            {
                var insertionIndex = block.BodyStart + block.BodyLength;
                var oldTextLength = _chatDisplay.TextLength;
                var existingCodeActionCount = _codeActionRanges.Count;
                var existingLinkActionCount = _linkActionRanges.Count;
                _chatDisplay.SelectionStart = insertionIndex;
                _chatDisplay.SelectionLength = 0;
                AppendThinkingStyled(new string('\n', separatorLength), isThinking: true, atCurrentPosition: true);
                var insertedLength = GetInsertedTextLength(insertionIndex, oldTextLength);
                block.BodyLength += insertedLength;
                ShiftRangesAfterInsertion(insertionIndex, insertedLength,
                    existingCodeActionCount, existingLinkActionCount);
            }

            _activeThinkingBlock = null;
        }

        private void ToggleThinkingBlock(ThinkingBlockRange block)
        {
            if (block.Content.Length == 0)
                return;

            var bodyStart = GetThinkingBodyStart(block);
            block.BodyStart = bodyStart;
            var visibleBodyLength = GetVisibleThinkingBodyLength(block, bodyStart);
            if (visibleBodyLength.HasValue)
            {
                block.IsExpanded = visibleBodyLength.Value > 0;
                block.BodyLength = visibleBodyLength.Value;
            }

            if (block.IsExpanded)
            {
                var bodyEnd = bodyStart + block.BodyLength;
                var deletedLength = DeleteChatTextRange(bodyStart, block.BodyLength);
                if (deletedLength != block.BodyLength)
                {
                    _chatDisplay.SelectionLength = 0;
                    return;
                }

                _codeActionRanges.RemoveAll(item =>
                    item.Start < bodyEnd && item.Start + item.Length > bodyStart);
                _linkActionRanges.RemoveAll(item =>
                    item.Start < bodyEnd && item.Start + item.Length > bodyStart);
                _thinkingBlockRanges.RemoveAll(item => item != block &&
                    item.Start >= bodyStart && item.Start < bodyEnd);
                ShiftRangesAfterDeletion(bodyEnd, block.BodyLength);
                block.BodyLength = 0;
                block.IsExpanded = false;
            }
            else
            {
                var content = block.Content.ToString();
                _chatDisplay.SelectionStart = bodyStart;
                _chatDisplay.SelectionLength = 0;
                var oldTextLength = _chatDisplay.TextLength;
                var existingCodeActionCount = _codeActionRanges.Count;
                var existingLinkActionCount = _linkActionRanges.Count;
                block.IsExpanded = true;
                AppendThinkingStyled(content, isThinking: true, atCurrentPosition: true);
                var separatorLength = GetThinkingSeparatorLength(content);
                if (_activeThinkingBlock != block && separatorLength > 0)
                    AppendThinkingStyled(new string('\n', separatorLength), isThinking: true, atCurrentPosition: true);
                var insertedLength = GetInsertedTextLength(bodyStart, oldTextLength);
                block.BodyLength = insertedLength;
                ShiftRangesAfterInsertion(bodyStart, insertedLength,
                    existingCodeActionCount, existingLinkActionCount);
            }

            var header = block.IsExpanded ? "▾ Réflexion" : "▸ Réflexion";
            _chatDisplay.SelectionStart = block.Start;
            _chatDisplay.SelectionLength = block.HeaderLength;
            _chatDisplay.SelectionColor = ThemeColors.Thinking;
            var fontKey = (false, 9f, FontStyle.Italic);
            if (!_messageFonts.TryGetValue(fontKey, out var font))
            {
                font = new Font("Segoe UI", 9f, FontStyle.Italic);
                _messageFonts.Add(fontKey, font);
            }
            _chatDisplay.SelectionFont = font;
            ReplaceChatSelection(header);
            block.HeaderLength = header.Length;

            _chatDisplay.SelectionStart = block.IsExpanded
                ? Math.Min(block.BodyStart + block.BodyLength, _chatDisplay.TextLength)
                : Math.Min(block.Start + block.HeaderLength, _chatDisplay.TextLength);
            _chatDisplay.SelectionLength = 0;
        }

        private void ReindexThinkingBlocks()
        {
            if (_thinkingBlockRanges.Count == 0)
                return;

            var text = _chatDisplay.Text;
            var searchStart = 0;
            foreach (var block in _thinkingBlockRanges)
            {
                const string collapsedHeader = "▸ Réflexion";
                const string expandedHeader = "▾ Réflexion";
                var expectedStart = Math.Clamp(block.Start, searchStart, text.Length);

                string? matchedHeader = null;
                var headerStart = -1;
                if (text.AsSpan(expectedStart).StartsWith(collapsedHeader.AsSpan(), StringComparison.Ordinal))
                {
                    matchedHeader = collapsedHeader;
                    headerStart = expectedStart;
                }
                else if (text.AsSpan(expectedStart).StartsWith(expandedHeader.AsSpan(), StringComparison.Ordinal))
                {
                    matchedHeader = expandedHeader;
                    headerStart = expectedStart;
                }
                else
                {
                    var collapsedStart = text.IndexOf(collapsedHeader, searchStart, StringComparison.Ordinal);
                    var expandedStart = text.IndexOf(expandedHeader, searchStart, StringComparison.Ordinal);
                    if (collapsedStart >= 0 && (expandedStart < 0 || collapsedStart < expandedStart))
                    {
                        matchedHeader = collapsedHeader;
                        headerStart = collapsedStart;
                    }
                    else if (expandedStart >= 0)
                    {
                        matchedHeader = expandedHeader;
                        headerStart = expandedStart;
                    }
                }

                if (matchedHeader == null || headerStart < 0)
                    continue;

                block.Start = headerStart;
                block.HeaderLength = matchedHeader.Length;
                block.IsExpanded = matchedHeader == expandedHeader;
                block.BodyStart = GetThinkingBodyStart(block);

                var visibleBodyLength = GetVisibleThinkingBodyLength(block, block.BodyStart);
                if (visibleBodyLength.HasValue)
                {
                    block.BodyLength = visibleBodyLength.Value;
                    block.IsExpanded = visibleBodyLength.Value > 0;
                }

                searchStart = block.BodyStart;
                if (block.IsExpanded)
                    searchStart = Math.Max(searchStart, Math.Min(text.Length, block.BodyStart + block.BodyLength));
            }
        }

        private int GetThinkingBodyStart(ThinkingBlockRange block)
        {
            var text = _chatDisplay.Text;
            var headerEnd = block.Start + block.HeaderLength;
            if (headerEnd >= 0 && headerEnd < text.Length)
            {
                if (text[headerEnd] == '\r')
                    return headerEnd + (headerEnd + 1 < text.Length && text[headerEnd + 1] == '\n' ? 2 : 1);
                if (text[headerEnd] == '\n')
                    return headerEnd + 1;
            }
            return Math.Clamp(block.BodyStart, 0, _chatDisplay.TextLength);
        }

        private int? GetVisibleThinkingBodyLength(ThinkingBlockRange block, int bodyStart)
        {
            if (_activeThinkingBlock == block)
                return block.IsExpanded ? Math.Max(0, block.BodyLength) : 0;

            var content = block.Content.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var text = _chatDisplay.Text;
            if (content.Length == 0)
                return 0;
            if (bodyStart < 0 || bodyStart + content.Length > text.Length)
                return null;

            var visibleLength = 0;
            var copiesFound = 0;
            while (copiesFound < 64 && bodyStart + visibleLength + content.Length <= text.Length &&
                   text.AsSpan(bodyStart + visibleLength, content.Length).SequenceEqual(content.AsSpan()))
            {
                visibleLength += content.Length;
                copiesFound++;
                visibleLength += CountLeadingLineBreakCharacters(
                    text, bodyStart + visibleLength, GetThinkingSeparatorLength(content));
            }

            return copiesFound > 0 ? visibleLength : null;
        }

        private static int CountLeadingLineBreakCharacters(string text, int start, int requiredLineBreaks)
        {
            var characterCount = 0;
            var lineBreakCount = 0;
            while (lineBreakCount < requiredLineBreaks && start + characterCount < text.Length)
            {
                var current = text[start + characterCount];
                if (current == '\r')
                {
                    characterCount++;
                    if (start + characterCount < text.Length && text[start + characterCount] == '\n')
                        characterCount++;
                    lineBreakCount++;
                }
                else if (current == '\n')
                {
                    characterCount++;
                    lineBreakCount++;
                }
                else
                {
                    break;
                }
            }

            return characterCount;
        }

        private static int GetThinkingSeparatorLength(string content)
        {
            var trailingLineBreaks = 0;
            for (var index = content.Length - 1; index >= 0;)
            {
                if (content[index] == '\n')
                {
                    trailingLineBreaks++;
                    index--;
                    if (index >= 0 && content[index] == '\r')
                        index--;
                }
                else if (content[index] == '\r')
                {
                    trailingLineBreaks++;
                    index--;
                }
                else
                {
                    break;
                }
            }

            // Keep one empty line between expanded reasoning and the assistant answer.
            return Math.Max(0, 2 - trailingLineBreaks);
        }

        private void ShiftRangesAfterInsertion(int insertionStart, int insertedLength,
                                               int existingCodeActionCount,
                                               int existingLinkActionCount)
        {
            if (insertedLength <= 0)
                return;

            for (var index = 0; index < Math.Min(existingCodeActionCount, _codeActionRanges.Count); index++)
                if (_codeActionRanges[index].Start >= insertionStart)
                    _codeActionRanges[index].Start += insertedLength;
            for (var index = 0; index < Math.Min(existingLinkActionCount, _linkActionRanges.Count); index++)
                if (_linkActionRanges[index].Start >= insertionStart)
                    _linkActionRanges[index].Start += insertedLength;
            foreach (var item in _thinkingBlockRanges)
                if (item.Start >= insertionStart)
                {
                    item.Start += insertedLength;
                    item.BodyStart += insertedLength;
                }
        }

        private int GetInsertedTextLength(int insertionStart, int oldTextLength)
        {
            var textLengthDelta = _chatDisplay.TextLength - oldTextLength;
            if (textLengthDelta > 0)
                return textLengthDelta;

            return Math.Max(0, _chatDisplay.SelectionStart - insertionStart);
        }

        private void ShiftRangesAfterDeletion(int deletedEnd, int deletedLength)
        {
            if (deletedLength <= 0)
                return;

            foreach (var item in _codeActionRanges)
                if (item.Start >= deletedEnd)
                    item.Start -= deletedLength;
            foreach (var item in _linkActionRanges)
                if (item.Start >= deletedEnd)
                    item.Start -= deletedLength;
            foreach (var item in _thinkingBlockRanges)
                if (item.Start >= deletedEnd)
                {
                    item.Start -= deletedLength;
                    item.BodyStart -= deletedLength;
                }
        }

        private bool IsThinkingBlockHit(ThinkingBlockRange block, int textIndex)
        {
            var textLength = _chatDisplay.TextLength;
            var safeIndex = Math.Clamp(textIndex, 0, textLength);
            var startLine = _chatDisplay.GetLineFromCharIndex(block.Start);
            var lastBlockIndex = block.IsExpanded
                ? block.BodyStart + block.BodyLength - 1
                : block.Start + block.HeaderLength - 1;
            lastBlockIndex = Math.Clamp(lastBlockIndex, block.Start, textLength);
            var endLine = _chatDisplay.GetLineFromCharIndex(lastBlockIndex);
            var clickLine = _chatDisplay.GetLineFromCharIndex(safeIndex);
            return clickLine >= startLine && clickLine <= endLine;
        }

        private bool IsThinkingHeaderHit(ThinkingBlockRange block, Point location)
        {
            if (block.Start < 0 || block.Start + block.HeaderLength > _chatDisplay.TextLength)
                return false;

            var label = _chatDisplay.Text.Substring(block.Start, block.HeaderLength);
            if (label != "▸ Réflexion" && label != "▾ Réflexion")
                return false;

            var position = _chatDisplay.GetPositionFromCharIndex(block.Start);
            var width = TextRenderer.MeasureText(label, _chatDisplay.Font).Width + 10;
            var height = Math.Max(18, _chatDisplay.Font.Height + 4);
            var hitBox = new Rectangle(
                Math.Max(0, position.X - 4),
                Math.Max(0, position.Y - 2),
                width,
                height);
            return hitBox.Contains(location);
        }

        private void AppendInlineMarkdown(string text, Color color, bool bold, bool italic,
                                          float fontSize, string? linkUrl = null,
                                          bool atCurrentPosition = false)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var segment = new StringBuilder();
            var boldMarkerOpen = false;
            var italicMarkerOpen = false;
            var currentBold = bold;
            var currentItalic = italic;

            void FlushSegment()
            {
                if (segment.Length == 0)
                    return;

                var segmentColor = linkUrl == null
                    ? color
                    : color == ThemeColors.Thinking ? color : ThemeColors.PrimaryHover;
                AppendStyled(segment.ToString(), segmentColor, currentBold, currentItalic,
                    fontSize, underline: linkUrl != null, atCurrentPosition: atCurrentPosition);
                segment.Clear();
            }

            for (var index = 0; index < text.Length;)
            {
                if (linkUrl == null && text[index] == '[' &&
                    TryParseMarkdownLink(text, index, out var nextIndex, out var label, out var destination))
                {
                    FlushSegment();
                    var linkStart = atCurrentPosition ? _chatDisplay.SelectionStart : _chatDisplay.TextLength;
                    AppendInlineMarkdown(label, color, currentBold, currentItalic, fontSize, destination,
                        atCurrentPosition);
                    var linkLength = atCurrentPosition
                        ? _chatDisplay.SelectionStart - linkStart
                        : _chatDisplay.TextLength - linkStart;
                    if (linkLength > 0)
                    {
                        _linkActionRanges.Add(new LinkActionRange
                        {
                            Start = linkStart,
                            Length = linkLength,
                            Url = destination
                        });
                    }
                    index = nextIndex;
                    continue;
                }

                if (text[index] == '\\' && index + 1 < text.Length &&
                    (char.IsPunctuation(text[index + 1]) || char.IsSymbol(text[index + 1])))
                {
                    segment.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
                {
                    if (boldMarkerOpen || text.IndexOf("**", index + 2, StringComparison.Ordinal) >= 0)
                    {
                        FlushSegment();
                        currentBold = !currentBold;
                        boldMarkerOpen = !boldMarkerOpen;
                        index += 2;
                        continue;
                    }
                }
                else if (text[index] == '*' &&
                         (italicMarkerOpen || text.IndexOf('*', index + 1) >= 0))
                {
                    FlushSegment();
                    currentItalic = !currentItalic;
                    italicMarkerOpen = !italicMarkerOpen;
                    index++;
                    continue;
                }

                segment.Append(text[index]);
                index++;
            }

            FlushSegment();
        }

        private static bool TryParseMarkdownLink(string text, int start, out int nextIndex,
                                                 out string label, out string destination)
        {
            nextIndex = start + 1;
            label = "";
            destination = "";
            if (start < 0 || start >= text.Length || text[start] != '[')
                return false;

            var closeBracket = FindMarkdownClosingBracket(text, start);
            if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
                return false;

            var closeParenthesis = FindMarkdownClosingParenthesis(text, closeBracket + 1);
            if (closeParenthesis < 0)
                return false;

            label = text.Substring(start + 1, closeBracket - start - 1);
            var rawDestination = text.Substring(closeBracket + 2, closeParenthesis - closeBracket - 2).Trim();
            if (rawDestination.StartsWith("<", StringComparison.Ordinal))
            {
                var angleEnd = rawDestination.IndexOf('>');
                if (angleEnd <= 1)
                    return false;
                rawDestination = rawDestination.Substring(1, angleEnd - 1);
            }
            else
            {
                var titleStart = FindMarkdownWhitespace(rawDestination);
                if (titleStart >= 0)
                    rawDestination = rawDestination.Substring(0, titleStart);
            }

            destination = UnescapeMarkdown(rawDestination.Trim());
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri))
                return false;

            var isWebOrEmailLink = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                                   uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
            var isGeneratedImageLink = false;
            if (uri.IsFile && !uri.IsUnc)
            {
                try
                {
                    var generatedImagesDirectory = Path.GetFullPath(
                        ImageGeneratorForm.GetConfiguredOutputDirectory());
                    var generatedImagesPrefix = Path.EndsInDirectorySeparator(generatedImagesDirectory)
                        ? generatedImagesDirectory
                        : generatedImagesDirectory + Path.DirectorySeparatorChar;
                    var linkedFile = Path.GetFullPath(uri.LocalPath);
                    isGeneratedImageLink = linkedFile.StartsWith(generatedImagesPrefix, StringComparison.OrdinalIgnoreCase) &&
                                           Path.GetExtension(linkedFile).Equals(".png", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    isGeneratedImageLink = false;
                }
            }

            if (!isWebOrEmailLink && !isGeneratedImageLink)
                return false;

            destination = uri.AbsoluteUri;
            nextIndex = closeParenthesis + 1;
            return label.Length > 0;
        }

        private static int FindMarkdownClosingBracket(string text, int openingBracket)
        {
            var nestedBrackets = 0;
            for (var index = openingBracket + 1; index < text.Length; index++)
            {
                if (text[index] == '\\')
                {
                    index++;
                    continue;
                }
                if (text[index] == '[')
                    nestedBrackets++;
                else if (text[index] == ']')
                {
                    if (nestedBrackets == 0)
                        return index;
                    nestedBrackets--;
                }
            }
            return -1;
        }

        private static int FindMarkdownClosingParenthesis(string text, int openingParenthesis)
        {
            var depth = 1;
            for (var index = openingParenthesis + 1; index < text.Length; index++)
            {
                if (text[index] == '\\')
                {
                    index++;
                    continue;
                }
                if (text[index] == '(')
                    depth++;
                else if (text[index] == ')' && --depth == 0)
                    return index;
            }
            return -1;
        }

        private static int FindMarkdownWhitespace(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\\')
                {
                    index++;
                    continue;
                }
                if (char.IsWhiteSpace(text[index]))
                    return index;
            }
            return -1;
        }

        private static string UnescapeMarkdown(string text)
        {
            var result = new StringBuilder(text.Length);
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\\' && index + 1 < text.Length &&
                    (char.IsPunctuation(text[index + 1]) || char.IsSymbol(text[index + 1])))
                    index++;
                result.Append(text[index]);
            }
            return result.ToString();
        }

        private static int FindIncompleteMarkdownLinkStart(string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '[' || IsMarkdownEscaped(text, index))
                    continue;

                var closeBracket = FindMarkdownClosingBracket(text, index);
                if (closeBracket < 0 || closeBracket + 1 == text.Length)
                    return index;
                if (text[closeBracket + 1] != '(')
                {
                    index = closeBracket;
                    continue;
                }

                var closeParenthesis = FindMarkdownClosingParenthesis(text, closeBracket + 1);
                if (closeParenthesis < 0)
                    return index;
                index = closeParenthesis;
            }
            return -1;
        }

        private static bool IsMarkdownEscaped(string text, int index)
        {
            var slashes = 0;
            for (var previous = index - 1; previous >= 0 && text[previous] == '\\'; previous--)
                slashes++;
            return slashes % 2 != 0;
        }

        private void AppendAssistantContent(string content)
        {
            var parts = ParseCodeBlocks(content);
            var isThinking = false;
            foreach (var part in parts)
            {
                if (part.IsCode)
                {
                    if (isThinking)
                        AppendThinkingBlockContent($"```{part.Language}\n{part.Content}\n```");
                    else
                        RenderCodeBlock(part.Language, part.Content);
                }
                else
                    AppendThinkingSegments(part.Content, ref isThinking);
            }
            FinalizeThinkingBlock();
        }

        private List<CodeBlockPart> ParseCodeBlocks(string text)
        {
            var parts = new List<CodeBlockPart>();
            var position = 0;
            while (position < text.Length)
            {
                var codeStart = text.IndexOf("```", position, StringComparison.Ordinal);
                if (codeStart < 0)
                {
                    if (position < text.Length)
                        parts.Add(new CodeBlockPart { IsCode = false, Content = text.Substring(position) });
                    break;
                }

                if (codeStart > position)
                    parts.Add(new CodeBlockPart { IsCode = false, Content = text.Substring(position, codeStart - position) });

                var languageStart = codeStart + 3;
                var languageEnd = text.IndexOf('\n', languageStart);
                if (languageEnd < 0)
                {
                    parts.Add(new CodeBlockPart { IsCode = false, Content = text.Substring(codeStart) });
                    break;
                }

                var language = text.Substring(languageStart, languageEnd - languageStart).Trim();
                if (string.IsNullOrEmpty(language)) language = "text";

                var codeContentStart = languageEnd + 1;
                var codeEnd = text.IndexOf("```", codeContentStart, StringComparison.Ordinal);
                var hasClosingFence = codeEnd >= 0;
                if (!hasClosingFence)
                    codeEnd = text.Length;

                var code = text.Substring(codeContentStart, codeEnd - codeContentStart).Trim('\r', '\n');
                parts.Add(new CodeBlockPart { IsCode = true, Language = language, Content = code });
                if (!hasClosingFence)
                {
                    break;
                }

                position = codeEnd + 3;
            }

            return parts;
        }

        private void RenderCodeBlock(string language, string code)
        {
            var displayLanguage = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim();
            _chatDisplay.SelectionStart = _chatDisplay.TextLength;
            _chatDisplay.SelectionLength = 0;
            _chatDisplay.SelectionBackColor = ThemeColors.CodeBackground;
            AppendStyled($"\n  {displayLanguage}\n", ThemeColors.TextSecondary, true, fontSize: 8.5f);

            _chatDisplay.SelectionStart = _chatDisplay.TextLength;
            _chatDisplay.SelectionLength = 0;
            _chatDisplay.SelectionColor = ThemeColors.TextPrimary;
            _chatDisplay.SelectionFont = _codeFont;
            _chatDisplay.SelectionBackColor = ThemeColors.CodeBackground;
            _chatDisplay.AppendText("  " + code + "\n");

            AppendStyled("  ", ThemeColors.TextSecondary, false);
            AddCodeAction("[ Copier ]", code, displayLanguage, download: false);
            AppendStyled("    ", ThemeColors.TextSecondary, false);
            AddCodeAction("[ Télécharger ]", code, displayLanguage, download: true);
            AppendStyled("\n", ThemeColors.TextPrimary, false);
            _chatDisplay.SelectionBackColor = ThemeColors.Surface;
            AppendStyled("\n", ThemeColors.TextPrimary, false);
        }

        private void AddCodeAction(string label, string code, string language, bool download)
        {
            var start = _chatDisplay.TextLength;
            AppendStyled(label, ThemeColors.PrimaryHover, bold: true, underline: true);
            _codeActionRanges.Add(new CodeActionRange
            {
                Start = start,
                Length = label.Length,
                Code = code,
                Language = language,
                Download = download
            });
        }

        private void OnChatDisplayMouseMove(object? sender, MouseEventArgs e)
        {
            var index = _chatDisplay.GetCharIndexFromPosition(e.Location);
            var overLink = _linkActionRanges.Any(link =>
                index >= link.Start && index < link.Start + link.Length);
            var overCodeAction = _codeActionRanges.Any(action =>
                index >= action.Start && index < action.Start + action.Length);
            var overThinkingBlock = _thinkingBlockRanges.Any(block => IsThinkingBlockHit(block, index));
            _chatDisplay.Cursor = overLink || overCodeAction || overThinkingBlock
                ? Cursors.Hand
                : Cursors.IBeam;
        }

        private void OnChatDisplayMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            var index = _chatDisplay.GetCharIndexFromPosition(e.Location);
            ReindexThinkingBlocks();

            var thinkingHeader = _thinkingBlockRanges.FirstOrDefault(block => IsThinkingHeaderHit(block, e.Location));
            if (thinkingHeader != null)
            {
                ToggleThinkingBlock(thinkingHeader);
                return;
            }

            var link = _linkActionRanges.FirstOrDefault(item =>
                index >= item.Start && index < item.Start + item.Length);
            if (link != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "Impossible d'ouvrir le lien : " + ex.Message,
                        "Ouverture du lien", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            var thinkingBlock = _thinkingBlockRanges.FirstOrDefault(block => IsThinkingBlockHit(block, index));
            if (thinkingBlock != null)
            {
                ToggleThinkingBlock(thinkingBlock);
                return;
            }

            var action = _codeActionRanges.FirstOrDefault(item =>
                index >= item.Start && index < item.Start + item.Length);
            if (action == null)
                return;

            if (!action.Download)
            {
                try { Clipboard.SetText(action.Code); }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "Impossible de copier le code : " + ex.Message,
                        "Copie du code", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            var extension = GetCodeFileExtension(action.Language);
            using var dialog = new SaveFileDialog
            {
                FileName = "script" + extension,
                Filter = $"Fichier {extension} (*{extension})|*{extension}|Tous les fichiers (*.*)|*.*",
                AddExtension = true,
                DefaultExt = extension.TrimStart('.')
            };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, action.Code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "Impossible d'enregistrer le code : " + ex.Message,
                    "Téléchargement du code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string GetCodeFileExtension(string language)
        {
            return language.Trim().ToLowerInvariant() switch
            {
                "python" or "py" => ".py",
                "csharp" or "c#" or "cs" => ".cs",
                "javascript" or "js" => ".js",
                "typescript" or "ts" => ".ts",
                "html" => ".html",
                "css" => ".css",
                "json" => ".json",
                "bash" or "sh" or "shell" => ".sh",
                "powershell" or "ps1" or "pwsh" => ".ps1",
                "sql" => ".sql",
                "java" => ".java",
                "c" => ".c",
                "cpp" or "c++" => ".cpp",
                "rust" or "rs" => ".rs",
                "go" => ".go",
                "ruby" or "rb" => ".rb",
                "php" => ".php",
                "xml" => ".xml",
                "yaml" or "yml" => ".yml",
                "markdown" or "md" => ".md",
                "text" or "txt" => ".txt",
                _ => ".txt"
            };
        }

        private sealed class CodeActionRange
        {
            public int Start { get; set; }
            public int Length { get; init; }
            public string Code { get; init; } = "";
            public string Language { get; init; } = "text";
            public bool Download { get; init; }
        }

        private sealed class LinkActionRange
        {
            public int Start { get; set; }
            public int Length { get; init; }
            public string Url { get; init; } = "";
        }

        private sealed class ThinkingBlockRange
        {
            public int Start { get; set; }
            public int BodyStart { get; set; }
            public int HeaderLength { get; set; }
            public int LeadingSpaceLength { get; set; }
            public StringBuilder Content { get; } = new();
            public bool IsExpanded { get; set; }
            public bool IsPreview { get; set; }
            public int BodyLength { get; set; }
        }
        
        private class CodeBlockPart
        {
            public bool IsCode { get; set; }
            public string Language { get; set; }
            public string Content { get; set; }
        }
    }

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
            Height = 180;
            BackColor = ThemeColors.Surface;
            Padding = new Padding(16, 12, 16, 12);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));  // Header row (model + char count)
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // Input box
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));  // Send button

            // Header row with model label + char count
            var headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

            _modelLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Aucun modele selectionne",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                AutoSize = false
            };
            headerLayout.Controls.Add(_modelLabel, 0, 0);

            _charCount = new Label
            {
                Dock = DockStyle.Fill,
                Text = "0 / 4000",
                ForeColor = ThemeColors.TextMuted,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleRight,
                AutoSize = false
            };
            headerLayout.Controls.Add(_charCount, 1, 0);

            layout.Controls.Add(headerLayout, 0, 0);

            // Input box - fills available space
            _inputBox = new ModernTextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                Font = new Font("Segoe UI", 10f),
                ScrollBars = ScrollBars.Vertical,
                PlaceholderText = "Ecrivez votre message... (Entree pour envoyer, Maj+Entree pour nouvelle ligne)"
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
            layout.Controls.Add(_inputBox, 0, 1);

            // Send button - full width, below input box
            _sendBtn = new ModernButton
            {
                Text = "Envoyer",
                Dock = DockStyle.Fill,
                BackColorNormal = ThemeColors.Primary,
                BackColorHover = ThemeColors.PrimaryHover,
                BackColorPressed = ThemeColors.PrimaryPressed,
                Enabled = false,
                Margin = new Padding(0, 8, 0, 8),
                Height = 40
            };
            layout.Controls.Add(_sendBtn, 0, 2);
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
                RowCount = 6,
                BackColor = Color.Transparent,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));

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
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

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
                ItemHeight = 80,
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
                BackColorNormal = ThemeColors.Success,
                BackColorHover = Color.FromArgb(50, 200, 110),
                BackColorPressed = Color.FromArgb(35, 160, 85),
                CornerRadius = 10
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
            
            var backColor = selected ? Color.FromArgb(0, 122, 255) : Color.FromArgb(45, 45, 48);
            using (var path = GetRoundedRectPath(rect, 8))
            using (var brush = new SolidBrush(backColor))
            using (var pen = new Pen(selected ? ThemeColors.Primary : Color.FromArgb(45, 45, 48), selected ? 1.5f : 1f))
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
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, typeText, metaFont,
                new Rectangle(rect.X + 12, rect.Y + 26, rect.Width - 24, 18),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var pathText = model.Folder ?? model.Path;
            if (pathText.Length > 60) pathText = "..." + pathText.Substring(pathText.Length - 57);
            
            TextRenderer.DrawText(g, pathText, pathFont,
                new Rectangle(rect.X + 12, rect.Y + 44, rect.Width - 24, 18),
                Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

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
            
            var backColor = selected ? Color.FromArgb(0, 122, 255) : Color.FromArgb(45, 45, 48);
            using (var path = GetRoundedRectPath(rect, 8))
            using (var brush = new SolidBrush(backColor))
            using (var pen = new Pen(selected ? ThemeColors.Primary : Color.FromArgb(45, 45, 48), selected ? 1.5f : 1f))
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
        private CheckBox _autoUpdateCheckBox = null!;
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
                EnableWebAccess = currentSettings.EnableWebAccess,
                EnableCodeExecution = currentSettings.EnableCodeExecution,
                EnableForum = currentSettings.EnableForum,
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
                "Au démarrage, télécharge la dernière release et vérifie son SHA-256. L'installation s'applique à la fermeture; modèles et données restent en place.",
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
            _webAccessCheckBox.Checked = Settings.EnableWebAccess;
            _codeExecutionCheckBox.Checked = Settings.EnableCodeExecution;
            _forumEnabledCheckBox.Checked = Settings.EnableForum;
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
            Settings.EnableWebAccess = _webAccessCheckBox.Checked;
            Settings.EnableCodeExecution = _codeExecutionCheckBox.Checked;
            Settings.EnableForum = _forumEnabledCheckBox.Checked;
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
