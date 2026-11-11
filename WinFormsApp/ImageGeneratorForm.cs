using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAIApp
{
    public sealed class ImageRecipeInfo
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("description")] public string? Description { get; set; }
        [JsonProperty("slots")] public List<ImageModelSlotInfo> Slots { get; set; } = new();
        [JsonProperty("required_any")] public List<List<string>> RequiredAny { get; set; } = new();
        [JsonProperty("defaults")] public JObject? Defaults { get; set; }
        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id ?? "" : Name;
    }

    public sealed class ImageModelSlotInfo
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("label")] public string? Label { get; set; }
        [JsonProperty("required")] public bool Required { get; set; }
        [JsonProperty("category")] public string? Category { get; set; }
        [JsonProperty("categories")] public List<string> Categories { get; set; } = new();
    }

    public sealed class ImageModelFileInfo
    {
        [JsonProperty("id")] public string? Id { get; set; }
        [JsonProperty("name")] public string? Name { get; set; }
        [JsonProperty("category")] public string? Category { get; set; }
        [JsonProperty("root")] public string? Root { get; set; }
        [JsonProperty("relative_path")] public string? RelativePath { get; set; }
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                var path = string.IsNullOrWhiteSpace(RelativePath) ? Name : RelativePath;
                return string.IsNullOrWhiteSpace(path)
                    ? Id ?? ""
                    : path.Replace('/', Path.DirectorySeparatorChar);
            }
        }
        public override string ToString() => DisplayName;
    }

    public sealed class ImageCatalogError
    {
        [JsonProperty("file")] public string? File { get; set; }
        [JsonProperty("message")] public string? Message { get; set; }
    }

    internal sealed class ImageGeneratorFormSettings
    {
        public string? ModelDirectory { get; set; }
        public string? OutputDirectory { get; set; }
    }

    public sealed class ImageGeneratorForm : Form
    {
        public static string GetConfiguredOutputDirectory()
        {
            var defaultDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LocalIARunner");
            var settingsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalAIApp", "image-settings.json");
            try
            {
                if (File.Exists(settingsFile))
                {
                    var settings = JsonConvert.DeserializeObject<ImageGeneratorFormSettings>(
                        File.ReadAllText(settingsFile));
                    if (!string.IsNullOrWhiteSpace(settings?.OutputDirectory))
                        return Path.GetFullPath(settings.OutputDirectory.Trim());
                }
            }
            catch
            {
            }
            return Path.GetFullPath(defaultDirectory);
        }

        public event Action<object, string>? CatalogRequested;
        public event Action<object, object>? GenerateRequested;
        public event Action<object, EventArgs>? CancelRequested;

        private readonly TextBox _modelDirectoryBox;
        private readonly TextBox _outputDirectoryBox;
        private readonly TextBox _promptBox;
        private readonly TextBox _negativePromptBox;
        private readonly ComboBox _recipeBox;
        private readonly FlowLayoutPanel _slotsPanel;
        private readonly NumericUpDown _widthBox;
        private readonly NumericUpDown _heightBox;
        private readonly NumericUpDown _stepsBox;
        private readonly NumericUpDown _cfgBox;
        private readonly NumericUpDown _guidanceBox;
        private readonly NumericUpDown _seedBox;
        private readonly PictureBox _previewBox;
        private readonly Label _statusLabel;
        private readonly Label _outputLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _catalogButton;
        private readonly Button _generateButton;
        private readonly Button _cancelButton;
        private readonly Dictionary<string, ComboBox> _slotSelectors = new(StringComparer.Ordinal);
        private readonly List<ImageModelFileInfo> _modelFiles = new();
        private readonly string _settingsFile;
        private readonly string _defaultModelPath;
        private List<ImageRecipeInfo> _recipes = new();
        private bool _busy;
        private bool _catalogLoading;

        public ImageGeneratorForm(string? defaultModelPath = null)
        {
            _defaultModelPath = defaultModelPath?.Trim() ?? "";
            Text = "Génération d'images";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1200, 820);
            MinimumSize = new Size(900, 650);
            BackColor = ThemeColors.Background;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 9.5f);

            var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalAIApp");
            Directory.CreateDirectory(configDirectory);
            _settingsFile = Path.Combine(configDirectory, "image-settings.json");
            var saved = LoadFormSettings();
            var defaultModelDirectory = TryGetImageModelRoot(_defaultModelPath);
            if (!string.IsNullOrWhiteSpace(defaultModelDirectory))
                saved.ModelDirectory = defaultModelDirectory;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            Controls.Add(split);
            Shown += (s, e) =>
            {
                split.Panel1MinSize = 360;
                split.Panel2MinSize = 360;
                split.SplitterDistance = Math.Min(490, split.Width - split.Panel2MinSize - split.SplitterWidth);
            };

            var leftScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ThemeColors.Background, ForeColor = ThemeColors.TextPrimary, Padding = new Padding(12) };
            split.Panel1.Controls.Add(leftScroll);
            var left = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            leftScroll.Controls.Add(left);

            AddSectionTitle(left, "Modèles d'images");
            AddHint(left, "Choisis le dossier racine stable-diffusion.cpp. Les .safetensors sont recherchés dans les catégories et sous-dossiers; un checkpoint directement à la racine est aussi détecté.");
            var modelRow = new FlowLayoutPanel { Width = 430, Height = 34, WrapContents = false, BackColor = ThemeColors.Background, Margin = new Padding(0, 4, 0, 4) };
            _modelDirectoryBox = CreateTextBox(saved.ModelDirectory ?? "", false);
            _modelDirectoryBox.Width = 330;
            _modelDirectoryBox.TextChanged += (s, e) => SaveFormSettings();
            var browseModels = CreateButton("Parcourir…", 90);
            browseModels.Click += (s, e) => BrowseModelDirectory();
            modelRow.Controls.Add(_modelDirectoryBox);
            modelRow.Controls.Add(browseModels);
            left.Controls.Add(modelRow);

            _catalogButton = CreateButton("Analyser les modèles", 180);
            _catalogButton.Click += (s, e) => RequestCatalog();
            left.Controls.Add(_catalogButton);

            AddFieldLabel(left, "Profil de modèle");
            _recipeBox = new ComboBox
            {
                Width = 430,
                Height = 34,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = ThemeColors.CodeBackground,
                ForeColor = ThemeColors.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                Margin = new Padding(0, 2, 0, 8)
            };
            _recipeBox.SelectedIndexChanged += (s, e) => RebuildSlots();
            left.Controls.Add(_recipeBox);

            AddFieldLabel(left, "Fichiers requis par ce profil");
            _slotsPanel = new FlowLayoutPanel
            {
                Width = 430,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = ThemeColors.Surface,
                Padding = new Padding(8),
                Margin = new Padding(0, 2, 0, 8)
            };
            left.Controls.Add(_slotsPanel);

            AddSectionTitle(left, "Description de l'image");
            _promptBox = CreateTextBox("Décris l'image à générer…", true);
            _promptBox.Height = 105;
            left.Controls.Add(_promptBox);
            AddFieldLabel(left, "Prompt négatif (facultatif)");
            _negativePromptBox = CreateTextBox("Éléments à éviter…", true);
            _negativePromptBox.Height = 72;
            left.Controls.Add(_negativePromptBox);

            AddFieldLabel(left, "Dimensions, étapes et guidage");
            var parameterGrid = new TableLayoutPanel
            {
                Width = 430,
                Height = 104,
                ColumnCount = 4,
                RowCount = 2,
                BackColor = ThemeColors.Background,
                Margin = new Padding(0, 2, 0, 8)
            };
            parameterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            parameterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            parameterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            parameterGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            parameterGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            parameterGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _widthBox = CreateNumeric(64, 4096, 1024, 64, 0);
            _heightBox = CreateNumeric(64, 4096, 1024, 64, 0);
            _stepsBox = CreateNumeric(1, 200, 20, 1, 0);
            _cfgBox = CreateNumeric(0, 50, 7, 0.5m, 1);
            AddNumericColumn(parameterGrid, 0, "Largeur", _widthBox);
            AddNumericColumn(parameterGrid, 1, "Hauteur", _heightBox);
            AddNumericColumn(parameterGrid, 2, "Étapes", _stepsBox);
            AddNumericColumn(parameterGrid, 3, "CFG", _cfgBox);
            left.Controls.Add(parameterGrid);

            var advancedGrid = new TableLayoutPanel
            {
                Width = 430,
                Height = 64,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = ThemeColors.Background,
                Margin = new Padding(0, 0, 0, 8)
            };
            advancedGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            advancedGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            advancedGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            advancedGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            _guidanceBox = CreateNumeric(0, 50, 3.5m, 0.1m, 1);
            _seedBox = CreateNumeric(-1, int.MaxValue, -1, 1, 0);
            AddNumericColumn(advancedGrid, 0, "Distilled guidance", _guidanceBox);
            AddNumericColumn(advancedGrid, 1, "Seed (-1 = aléatoire)", _seedBox);
            left.Controls.Add(advancedGrid);

            AddFieldLabel(left, "Dossier de sortie");
            var outputRow = new FlowLayoutPanel { Width = 430, Height = 34, WrapContents = false, BackColor = ThemeColors.Background, Margin = new Padding(0, 2, 0, 8) };
            var defaultOutput = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LocalIARunner");
            _outputDirectoryBox = CreateTextBox(string.IsNullOrWhiteSpace(saved.OutputDirectory) ? defaultOutput : saved.OutputDirectory, false);
            _outputDirectoryBox.Width = 330;
            _outputDirectoryBox.TextChanged += (s, e) => SaveFormSettings();
            var browseOutput = CreateButton("Parcourir…", 90);
            browseOutput.Click += (s, e) => BrowseOutputDirectory();
            outputRow.Controls.Add(_outputDirectoryBox);
            outputRow.Controls.Add(browseOutput);
            left.Controls.Add(outputRow);

            var actions = new FlowLayoutPanel { Width = 430, Height = 42, WrapContents = false, BackColor = ThemeColors.Background, Margin = new Padding(0, 4, 0, 12) };
            _generateButton = CreateButton("Générer", 140, true);
            _generateButton.Click += (s, e) => StartGeneration();
            _cancelButton = CreateButton("Annuler", 110);
            _cancelButton.Enabled = false;
            _cancelButton.Click += (s, e) => CancelRequested?.Invoke(this, EventArgs.Empty);
            actions.Controls.Add(_generateButton);
            actions.Controls.Add(_cancelButton);
            left.Controls.Add(actions);

            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(14),
                BackColor = ThemeColors.Background
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            split.Panel2.Controls.Add(rightLayout);

            var previewTitle = new Label { Text = "Aperçu", Dock = DockStyle.Fill, ForeColor = ThemeColors.TextPrimary, Font = new Font("Segoe UI Semibold", 11f), TextAlign = ContentAlignment.MiddleLeft };
            rightLayout.Controls.Add(previewTitle, 0, 0);
            _previewBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.Surface,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            rightLayout.Controls.Add(_previewBox, 0, 1);

            var progressLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = ThemeColors.Background };
            progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            progressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            _statusLabel = new Label { Text = "Choisis un dossier de modèles puis analyse-le.", Dock = DockStyle.Fill, ForeColor = ThemeColors.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            progressLayout.Controls.Add(_statusLabel, 0, 0);
            _progressBar = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100, Value = 0 };
            progressLayout.Controls.Add(_progressBar, 0, 1);
            rightLayout.Controls.Add(progressLayout, 0, 2);

            var outputLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = ThemeColors.Background };
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            _outputLabel = new Label { Text = "Les images terminées apparaîtront ici.", Dock = DockStyle.Fill, ForeColor = ThemeColors.TextMuted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            outputLayout.Controls.Add(_outputLabel, 0, 0);
            var openOutput = CreateButton("Ouvrir le dossier", 126);
            openOutput.Click += (s, e) => OpenOutputDirectory();
            outputLayout.Controls.Add(openOutput, 1, 0);
            var engineNote = new Label { Text = "Moteur stable-diffusion.cpp · le modèle reste chargé jusqu'au changement de moteur.", Dock = DockStyle.Fill, ForeColor = ThemeColors.TextMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            outputLayout.Controls.Add(engineNote, 0, 1);
            outputLayout.SetColumnSpan(engineNote, 2);
            rightLayout.Controls.Add(outputLayout, 0, 3);

            FormClosing += (s, e) =>
            {
                if (_busy)
                    CancelRequested?.Invoke(this, EventArgs.Empty);
            };
        }

        private void RequestCatalog()
        {
            var directory = _modelDirectoryBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                SetStatus("Sélectionne un dossier de modèles existant.");
                return;
            }
            _catalogLoading = true;
            _catalogButton.Enabled = false;
            _recipeBox.Enabled = false;
            SetStatus("Analyse des profils et fichiers de modèles…");
            CatalogRequested?.Invoke(this, directory);
        }

        private void BrowseModelDirectory()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choisis le dossier racine des modèles stable-diffusion.cpp",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(_modelDirectoryBox.Text) ? _modelDirectoryBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _modelDirectoryBox.Text = dialog.SelectedPath;
                SaveFormSettings();
            }
        }

        private void BrowseOutputDirectory()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choisis où enregistrer les images générées",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(_outputDirectoryBox.Text) ? _outputDirectoryBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _outputDirectoryBox.Text = dialog.SelectedPath;
                SaveFormSettings();
            }
        }

        public void SetCatalog(List<ImageRecipeInfo> recipes, List<ImageModelFileInfo> files, List<ImageCatalogError> errors)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetCatalog(recipes, files, errors))); return; }
            _catalogLoading = false;
            _catalogButton.Enabled = !_busy;
            _recipes = recipes ?? new List<ImageRecipeInfo>();
            _modelFiles.Clear();
            _modelFiles.AddRange((files ?? new List<ImageModelFileInfo>())
                .OrderBy(file => file.DisplayName, StringComparer.OrdinalIgnoreCase));
            _recipeBox.BeginUpdate();
            _recipeBox.Items.Clear();
            foreach (var recipe in _recipes)
                _recipeBox.Items.Add(recipe);
            _recipeBox.EndUpdate();
            _recipeBox.Enabled = _recipes.Count > 0 && !_busy;
            var defaultRecipeIndex = FindDefaultRecipeIndex();
            if (defaultRecipeIndex >= 0)
                _recipeBox.SelectedIndex = defaultRecipeIndex;
            else
                RebuildSlots();

            var errorSummary = errors?.FirstOrDefault();
            var safetensorsCount = _modelFiles.Count(file =>
                Path.GetExtension(file.RelativePath ?? file.Name ?? file.Id ?? "")
                    .Equals(".safetensors", StringComparison.OrdinalIgnoreCase));
            if (_recipes.Count == 0)
            {
                SetStatus(errorSummary == null
                    ? "Aucun profil trouvé. Vérifie la structure du dossier de modèles."
                    : errorSummary.Message);
            }
            else if (_modelFiles.Count == 0)
            {
                SetStatus($"Aucun modèle détecté. Les fichiers .safetensors sont pris en charge : vérifie leur dossier puis relance l’analyse.");
            }
            else if (defaultRecipeIndex < 0)
            {
                SetStatus($"{_modelFiles.Count} fichiers détectés, dont {safetensorsCount} .safetensors, mais aucun profil ne correspond à leurs catégories.");
            }
            else
            {
                var selectedRecipe = _recipes[defaultRecipeIndex];
                SetStatus($"{_modelFiles.Count} fichiers détectés, dont {safetensorsCount} .safetensors. Profil compatible sélectionné : {selectedRecipe.Name ?? selectedRecipe.Id}.");
            }
        }

        private static string? TryGetImageModelRoot(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return null;

            var modelCategoryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "checkpoints", "stable-diffusion", "diffusion_models", "unet",
                "text_encoders", "vae", "vae_approx", "clip_vision", "tokenizers",
                "controlnet", "ipadapter", "motion_modules", "photomaker", "pulid"
            };

            var directory = Directory.GetParent(Path.GetFullPath(modelPath));
            while (directory != null)
            {
                if (modelCategoryDirectories.Contains(directory.Name))
                    return directory.Parent?.FullName;
                directory = directory.Parent;
            }

            return Directory.GetParent(Path.GetFullPath(modelPath))?.FullName;
        }

        private bool IsDefaultModelFile(ImageModelFileInfo file)
        {
            if (string.IsNullOrWhiteSpace(_defaultModelPath) || !File.Exists(_defaultModelPath) ||
                string.IsNullOrWhiteSpace(file.RelativePath) || string.IsNullOrWhiteSpace(_modelDirectoryBox.Text))
                return false;

            try
            {
                var relativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var catalogPath = Path.GetFullPath(Path.Combine(_modelDirectoryBox.Text.Trim(), relativePath));
                var defaultPath = Path.GetFullPath(_defaultModelPath);
                if (string.Equals(catalogPath, defaultPath, StringComparison.OrdinalIgnoreCase))
                    return true;

                var modelDirectory = Path.GetFullPath(_modelDirectoryBox.Text.Trim());
                var defaultDirectory = Path.GetDirectoryName(defaultPath);
                return string.Equals(defaultDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Path.GetFileName(file.RelativePath), Path.GetFileName(defaultPath), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private int FindDefaultRecipeIndex()
        {
            var bestPartialIndex = -1;
            var bestSatisfiedCount = -1;
            var bestMissingCount = int.MaxValue;
            var bestPreferredPartialIndex = -1;
            var bestPreferredSatisfiedCount = -1;
            var bestPreferredMissingCount = int.MaxValue;
            var firstCompleteIndex = -1;

            for (var recipeIndex = 0; recipeIndex < _recipes.Count; recipeIndex++)
            {
                var recipe = _recipes[recipeIndex];
                var slots = recipe.Slots ?? new List<ImageModelSlotInfo>();
                var requiredSlots = slots.Where(slot => slot.Required).ToList();
                var matchedRequired = requiredSlots.Count(SlotHasFiles);
                var satisfiedGroups = (recipe.RequiredAny ?? new List<List<string>>())
                    .Count(group => group.Any(slotId =>
                    {
                        var slot = slots.FirstOrDefault(candidate =>
                            string.Equals(candidate.Id, slotId, StringComparison.OrdinalIgnoreCase));
                        return slot != null && SlotHasFiles(slot);
                    }));
                var missingCount = requiredSlots.Count - matchedRequired +
                                   (recipe.RequiredAny?.Count ?? 0) - satisfiedGroups;
                var hasAnyMatchingFile = slots.Any(SlotHasFiles);
                var hasPreferredModel = slots.Any(slot => GetFilesForSlot(slot).Any(IsDefaultModelFile));

                if (!hasAnyMatchingFile)
                    continue;

                if (missingCount == 0)
                {
                    if (hasPreferredModel)
                        return recipeIndex;
                    if (firstCompleteIndex < 0)
                        firstCompleteIndex = recipeIndex;
                    continue;
                }

                var satisfiedCount = matchedRequired + satisfiedGroups;
                if (hasPreferredModel && (satisfiedCount > bestPreferredSatisfiedCount ||
                    (satisfiedCount == bestPreferredSatisfiedCount && missingCount < bestPreferredMissingCount)))
                {
                    bestPreferredPartialIndex = recipeIndex;
                    bestPreferredSatisfiedCount = satisfiedCount;
                    bestPreferredMissingCount = missingCount;
                }

                if (satisfiedCount > bestSatisfiedCount ||
                    (satisfiedCount == bestSatisfiedCount && missingCount < bestMissingCount))
                {
                    bestPartialIndex = recipeIndex;
                    bestSatisfiedCount = satisfiedCount;
                    bestMissingCount = missingCount;
                }
            }

            if (bestPreferredPartialIndex >= 0)
                return bestPreferredPartialIndex;
            return firstCompleteIndex >= 0 ? firstCompleteIndex : bestPartialIndex;
        }

        private bool SlotHasFiles(ImageModelSlotInfo slot) => GetFilesForSlot(slot).Any();

        private IEnumerable<ImageModelFileInfo> GetFilesForSlot(ImageModelSlotInfo slot)
        {
            var categories = slot.Categories != null && slot.Categories.Count > 0
                ? slot.Categories
                : string.IsNullOrWhiteSpace(slot.Category)
                    ? new List<string>()
                    : new List<string> { slot.Category };

            return _modelFiles.Where(file => categories.Count == 0 ||
                (!string.IsNullOrWhiteSpace(file.Category) &&
                 categories.Contains(file.Category, StringComparer.OrdinalIgnoreCase)));
        }

        private void RebuildSlots()
        {
            if (_slotsPanel == null) return;
            _slotsPanel.SuspendLayout();
            _slotsPanel.Controls.Clear();
            _slotSelectors.Clear();
            if (_recipeBox.SelectedItem is not ImageRecipeInfo recipe)
            {
                _slotsPanel.Height = 56;
                _slotsPanel.Controls.Add(new Label { Text = "Analyse le dossier pour afficher les profils.", AutoSize = true, ForeColor = ThemeColors.TextMuted, Margin = new Padding(2, 6, 2, 6) });
                _slotsPanel.ResumeLayout();
                return;
            }

            foreach (var slot in recipe.Slots ?? new List<ImageModelSlotInfo>())
            {
                var matches = GetFilesForSlot(slot).ToList();
                var row = new TableLayoutPanel { Width = 400, Height = 58, ColumnCount = 1, RowCount = 2, BackColor = ThemeColors.Surface, Margin = new Padding(2, 2, 2, 6) };
                row.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
                row.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                var label = new Label
                {
                    Text = (slot.Label ?? slot.Id) + (slot.Required ? " *" : ""),
                    Dock = DockStyle.Fill,
                    ForeColor = slot.Required ? ThemeColors.TextPrimary : ThemeColors.TextSecondary,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                row.Controls.Add(label, 0, 0);
                var selector = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = ThemeColors.CodeBackground,
                    ForeColor = ThemeColors.TextPrimary,
                    FlatStyle = FlatStyle.Flat
                };
                selector.DisplayMember = nameof(ImageModelFileInfo.DisplayName);
                selector.Items.Add(new ImageModelFileInfo { Id = "", Name = matches.Count == 0 ? "Aucun fichier compatible" : "— Ne pas sélectionner —" });
                foreach (var file in matches)
                    selector.Items.Add(file);
                var defaultModelIndex = matches.FindIndex(IsDefaultModelFile);
                selector.SelectedIndex = defaultModelIndex >= 0
                    ? defaultModelIndex + 1
                    : matches.Count == 1 && slot.Required ? 1 : 0;
                selector.Enabled = matches.Count > 0 && !_busy;
                row.Controls.Add(selector, 0, 1);
                _slotsPanel.Controls.Add(row);
                _slotSelectors[slot.Id ?? ""] = selector;
            }
            if (_slotSelectors.Count == 0)
                _slotsPanel.Controls.Add(new Label { Text = "Ce profil ne déclare aucun composant.", AutoSize = true, ForeColor = ThemeColors.Warning });
            _slotsPanel.ResumeLayout();
            ApplyRecipeDefaults(recipe);
        }

        private void ApplyRecipeDefaults(ImageRecipeInfo recipe)
        {
            var defaults = recipe.Defaults;
            if (defaults == null) return;
            SetNumericFromDefaults(_widthBox, defaults["width"]);
            SetNumericFromDefaults(_heightBox, defaults["height"]);
            SetNumericFromDefaults(_stepsBox, defaults["steps"]);
            SetNumericFromDefaults(_cfgBox, defaults["cfg_scale"]);
            SetNumericFromDefaults(_guidanceBox, defaults["distilled_guidance"]);
        }

        private static void SetNumericFromDefaults(NumericUpDown control, JToken token)
        {
            if (token == null || !decimal.TryParse(token.ToString(), out var value)) return;
            control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
        }

        private void StartGeneration()
        {
            if (_busy) return;
            if (_recipeBox.SelectedItem is not ImageRecipeInfo recipe)
            {
                SetStatus("Analyse le dossier et choisis un profil de modèle.");
                return;
            }
            if (string.IsNullOrWhiteSpace(recipe.Id))
            {
                SetStatus("Le profil choisi ne possède pas d'identifiant valide.");
                return;
            }
            if (string.IsNullOrWhiteSpace(_promptBox.Text))
            {
                SetStatus("Écris une description avant de générer.");
                _promptBox.Focus();
                return;
            }

            var selectedModels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in recipe.Slots ?? new List<ImageModelSlotInfo>())
            {
                _slotSelectors.TryGetValue(slot.Id ?? "", out var selector);
                var selected = selector?.SelectedItem as ImageModelFileInfo;
                if (slot.Required && string.IsNullOrWhiteSpace(selected?.Id))
                {
                    SetStatus($"Sélectionne le fichier requis : {slot.Label ?? slot.Id}.");
                    return;
                }
                var slotId = slot.Id ?? "";
                if (!string.IsNullOrWhiteSpace(slotId) && !string.IsNullOrWhiteSpace(selected?.Id))
                    selectedModels[slotId] = selected.Id;
            }
            foreach (var requiredGroup in recipe.RequiredAny ?? new List<List<string>>())
            {
                if (!requiredGroup.Any(selectedModels.ContainsKey))
                {
                    SetStatus("Sélectionne au moins un fichier parmi : " + string.Join(", ", requiredGroup));
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(_outputDirectoryBox.Text))
            {
                SetStatus("Choisis un dossier de sortie.");
                return;
            }

            SetGenerating(true);
            SetStatus("Envoi de la demande…");
            GenerateRequested?.Invoke(this, new
            {
                type = "image_generate",
                model_dir = _modelDirectoryBox.Text.Trim(),
                recipe = recipe.Id,
                models = selectedModels,
                prompt = _promptBox.Text.Trim(),
                negative_prompt = _negativePromptBox.Text.Trim(),
                width = (int)_widthBox.Value,
                height = (int)_heightBox.Value,
                steps = (int)_stepsBox.Value,
                cfg_scale = (double)_cfgBox.Value,
                distilled_guidance = (double)_guidanceBox.Value,
                seed = (long)_seedBox.Value,
                output_dir = _outputDirectoryBox.Text.Trim()
            });
        }

        public void SetGenerating(bool generating)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetGenerating(generating))); return; }
            _busy = generating;
            _generateButton.Enabled = !generating && !_catalogLoading && _recipeBox.SelectedItem != null;
            _cancelButton.Enabled = generating;
            _catalogButton.Enabled = !generating && !_catalogLoading;
            _recipeBox.Enabled = !generating && _recipes.Count > 0;
            _modelDirectoryBox.Enabled = !generating;
            _outputDirectoryBox.Enabled = !generating;
            foreach (var selector in _slotSelectors.Values)
                selector.Enabled = !generating && selector.Items.Count > 1;
            if (generating)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 24;
            }
            else if (_progressBar.Style == ProgressBarStyle.Marquee)
            {
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = 0;
            }
            else if (!generating)
            {
                _progressBar.Value = 0;
            }
        }

        public void SetStatus(string? text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
            _statusLabel.Text = text ?? "";
        }

        public void SetDownloadProgress(long downloaded, long total)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetDownloadProgress(downloaded, total))); return; }
            if (total <= 0) return;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Maximum = 1000;
            _progressBar.Value = (int)Math.Clamp(downloaded * 1000 / total, 0, 1000);
            SetStatus($"Téléchargement du moteur : {downloaded / 1048576.0:0.0} / {total / 1048576.0:0.0} Mo");
        }

        public void SetProgress(string stage, string detail, int? completed, int? total, int? pass)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(stage, detail, completed, total, pass))); return; }
            var label = string.IsNullOrWhiteSpace(detail) ? stage : $"{stage} — {detail}";
            if (completed.HasValue && total.HasValue && total.Value > 0)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Maximum = Math.Max(1, total.Value);
                _progressBar.Value = Math.Clamp(completed.Value, 0, _progressBar.Maximum);
                label += $" ({completed}/{total})";
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 24;
            }
            if (pass.HasValue && pass.Value > 0) label += $" · passe {pass}";
            SetStatus(label ?? "Génération en cours…");
        }

        public void SetPreview(string? base64Png)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetPreview(base64Png))); return; }
            if (string.IsNullOrWhiteSpace(base64Png)) return;
            try
            {
                var bytes = Convert.FromBase64String(base64Png);
                using var stream = new MemoryStream(bytes);
                using var source = Image.FromStream(stream);
                var copy = new Bitmap(source);
                var previous = _previewBox.Image;
                _previewBox.Image = copy;
                previous?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Image preview decode failed: " + ex.Message);
            }
        }

        public void SetCompleted(List<string>? paths, string? error, bool cancelled)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetCompleted(paths, error, cancelled))); return; }
            _catalogLoading = false;
            SetGenerating(false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                SetStatus(error);
                _statusLabel.ForeColor = ThemeColors.Error;
                return;
            }
            _statusLabel.ForeColor = ThemeColors.TextSecondary;
            if (cancelled)
            {
                SetStatus("Génération annulée.");
                return;
            }
            var firstPath = paths?.FirstOrDefault(File.Exists);
            if (firstPath == null)
            {
                SetStatus("Génération terminée.");
                return;
            }
            try
            {
                using var source = Image.FromFile(firstPath);
                var copy = new Bitmap(source);
                var previous = _previewBox.Image;
                _previewBox.Image = copy;
                previous?.Dispose();
            }
            catch { }
            var pathCount = paths?.Count ?? 0;
            _outputLabel.Text = pathCount > 1 ? $"{pathCount} images sauvegardées · {firstPath}" : "Image sauvegardée · " + firstPath;
            SetStatus("Image générée et enregistrée.");
        }

        private void OpenOutputDirectory()
        {
            try
            {
                var directory = _outputDirectoryBox.Text.Trim();
                if (Directory.Exists(directory))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
            }
            catch (Exception error) { SetStatus(error.Message); }
        }

        private ImageGeneratorFormSettings LoadFormSettings()
        {
            try
            {
                if (File.Exists(_settingsFile))
                    return JsonConvert.DeserializeObject<ImageGeneratorFormSettings>(File.ReadAllText(_settingsFile)) ?? new ImageGeneratorFormSettings();
            }
            catch { }
            return new ImageGeneratorFormSettings();
        }

        private void SaveFormSettings()
        {
            try
            {
                File.WriteAllText(_settingsFile, JsonConvert.SerializeObject(new ImageGeneratorFormSettings
                {
                    ModelDirectory = _modelDirectoryBox?.Text,
                    OutputDirectory = _outputDirectoryBox?.Text
                }, Formatting.Indented));
            }
            catch { }
        }

        private static void AddSectionTitle(Control parent, string text)
        {
            parent.Controls.Add(new Label { Text = text, Width = 430, Height = 28, Font = new Font("Segoe UI Semibold", 10f), ForeColor = ThemeColors.TextPrimary, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 8, 0, 2) });
        }

        private static void AddHint(Control parent, string text)
        {
            parent.Controls.Add(new Label { Text = text, Width = 430, Height = 50, ForeColor = ThemeColors.TextMuted, Font = new Font("Segoe UI", 9.5f), AutoEllipsis = true, Margin = new Padding(0, 0, 0, 4) });
        }

        private static void AddFieldLabel(Control parent, string text)
        {
            parent.Controls.Add(new Label { Text = text, Width = 430, Height = 22, ForeColor = ThemeColors.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 0) });
        }

        private static void AddNumericColumn(TableLayoutPanel grid, int column, string labelText, NumericUpDown numeric)
        {
            var label = new Label { Text = labelText, Dock = DockStyle.Fill, ForeColor = ThemeColors.TextMuted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9.5f) };
            grid.Controls.Add(label, column, 0);
            numeric.Dock = DockStyle.Fill;
            grid.Controls.Add(numeric, column, 1);
        }

        private static NumericUpDown CreateNumeric(decimal min, decimal max, decimal value, decimal increment, int decimals)
        {
            return new ModernNumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Increment = increment,
                DecimalPlaces = decimals,
                ThousandsSeparator = false,
                Width = 90,
                Height = 30
            };
        }

        private static TextBox CreateTextBox(string? text, bool multiline)
        {
            return new TextBox
            {
                Text = text ?? "",
                Width = 430,
                Height = multiline ? 90 : 32,
                Multiline = multiline,
                AcceptsReturn = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                BackColor = ThemeColors.CodeBackground,
                ForeColor = ThemeColors.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f),
                Margin = new Padding(0, 2, 0, 4)
            };
        }

        private static Button CreateButton(string text, int width, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? ThemeColors.Primary : ThemeColors.Surface,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Margin = new Padding(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = ThemeColors.Border;
            button.FlatAppearance.MouseOverBackColor = primary ? ThemeColors.PrimaryHover : ThemeColors.SurfaceHover;
            button.FlatAppearance.MouseDownBackColor = primary ? ThemeColors.PrimaryPressed : ThemeColors.SurfaceHover;
            return button;
        }
    }
}
