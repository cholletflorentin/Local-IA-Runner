using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LocalAIApp
{
    public sealed class ModelComparisonRequestedEventArgs : EventArgs
    {
        public LocalModelInfo ModelA { get; }
        public LocalModelInfo ModelB { get; }
        public string Prompt { get; }

        public ModelComparisonRequestedEventArgs(LocalModelInfo modelA, LocalModelInfo modelB, string prompt)
        {
            ModelA = modelA;
            ModelB = modelB;
            Prompt = prompt;
        }
    }

    public sealed class ModelComparisonForm : Form
    {
        private readonly ComboBox _modelA = new();
        private readonly ComboBox _modelB = new();
        private readonly TextBox _prompt = new();
        private readonly RichTextBox _responseA = new();
        private readonly RichTextBox _responseB = new();
        private readonly Label _status = new();
        private readonly Button _compareButton = new();
        private readonly Button _cancelButton = new();
        private readonly object _streamLock = new();
        private readonly StringBuilder[] _pendingText = { new(), new() };
        private readonly System.Windows.Forms.Timer _renderTimer;

        public event EventHandler<ModelComparisonRequestedEventArgs>? CompareRequested;
        public event EventHandler? CancelRequested;
        public bool IsComparisonRunning { get; private set; }

        public ModelComparisonForm(IEnumerable<LocalModelInfo> models, string? preferredModelPath)
        {
            Text = "Comparer deux modèles";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1120, 780);
            MinimumSize = new Size(850, 620);
            BackColor = ThemeColors.Background;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;

            _modelA.DropDownStyle = ComboBoxStyle.DropDownList;
            _modelB.DropDownStyle = ComboBoxStyle.DropDownList;
            _modelA.DisplayMember = nameof(LocalModelInfo.Name);
            _modelB.DisplayMember = nameof(LocalModelInfo.Name);
            _modelA.Width = 360;
            _modelB.Width = 360;
            _modelA.Font = Font;
            _modelB.Font = Font;

            _prompt.Multiline = true;
            _prompt.AcceptsReturn = true;
            _prompt.ScrollBars = ScrollBars.Vertical;
            _prompt.Dock = DockStyle.Fill;
            _prompt.Font = Font;

            ConfigureResponseBox(_responseA);
            ConfigureResponseBox(_responseB);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(16),
                BackColor = ThemeColors.Background
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            var modelsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 1, 0, 0),
                BackColor = ThemeColors.Background
            };
            modelsPanel.Controls.Add(CreateModelSelector("Modèle A", _modelA));
            modelsPanel.Controls.Add(CreateModelSelector("Modèle B", _modelB));
            root.Controls.Add(modelsPanel, 0, 0);

            var promptPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = ThemeColors.Background,
                Padding = new Padding(0, 4, 0, 6)
            };
            promptPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            promptPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            promptPanel.Controls.Add(new Label
            {
                Text = "Même demande envoyée aux deux modèles (sans outils ni historique du chat)",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeColors.TextSecondary
            }, 0, 0);
            promptPanel.Controls.Add(_prompt, 0, 1);
            root.Controls.Add(promptPanel, 0, 1);

            var responses = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 8,
                BackColor = ThemeColors.Border
            };
            responses.Panel1.Controls.Add(CreateResponsePanel(_modelA, _responseA));
            responses.Panel2.Controls.Add(CreateResponsePanel(_modelB, _responseB));
            root.Controls.Add(responses, 0, 2);
            Shown += (_, _) =>
            {
                if (responses.ClientSize.Width > 0)
                    responses.SplitterDistance = responses.ClientSize.Width / 2;
            };

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = ThemeColors.Background,
                Padding = new Padding(0, 6, 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            _status.Text = "Prêt à comparer.";
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = ThemeColors.TextSecondary;
            _compareButton.Text = "Comparer";
            _compareButton.Dock = DockStyle.Fill;
            _compareButton.Margin = new Padding(4, 0, 8, 0);
            _compareButton.BackColor = ThemeColors.Primary;
            _compareButton.ForeColor = Color.White;
            _compareButton.FlatStyle = FlatStyle.Flat;
            _compareButton.FlatAppearance.BorderSize = 0;
            _compareButton.Click += (_, _) => RequestComparison();
            _cancelButton.Text = "Annuler";
            _cancelButton.Dock = DockStyle.Fill;
            _cancelButton.Margin = new Padding(0);
            _cancelButton.BackColor = ThemeColors.Surface;
            _cancelButton.ForeColor = ThemeColors.TextPrimary;
            _cancelButton.FlatStyle = FlatStyle.Flat;
            _cancelButton.FlatAppearance.BorderColor = ThemeColors.Border;
            _cancelButton.Enabled = false;
            _cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(_compareButton, 1, 0);
            footer.Controls.Add(_cancelButton, 2, 0);
            root.Controls.Add(footer, 0, 3);

            Controls.Add(root);
            SetModels(models, preferredModelPath);

            _renderTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _renderTimer.Tick += (_, _) => FlushPendingTokens();
            _renderTimer.Start();
            FormClosing += (_, _) =>
            {
                if (IsComparisonRunning)
                    CancelRequested?.Invoke(this, EventArgs.Empty);
                _renderTimer.Stop();
            };
            FormClosed += (_, _) => _renderTimer.Dispose();
        }

        public void SetModels(IEnumerable<LocalModelInfo> models, string? preferredModelPath)
        {
            if (IsDisposed || IsComparisonRunning)
                return;

            var currentA = (_modelA.SelectedItem as LocalModelInfo)?.Path;
            var currentB = (_modelB.SelectedItem as LocalModelInfo)?.Path;
            var available = models
                .Where(model => model.Type == ModelType.GGUF && !string.IsNullOrWhiteSpace(model.Path))
                .GroupBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            _modelA.BeginUpdate();
            _modelB.BeginUpdate();
            try
            {
                _modelA.Items.Clear();
                _modelB.Items.Clear();
                _modelA.Items.AddRange(available.Cast<object>().ToArray());
                _modelB.Items.AddRange(available.Cast<object>().ToArray());

                _modelA.SelectedItem = available.FirstOrDefault(model =>
                    string.Equals(model.Path, currentA, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault(model =>
                        string.Equals(model.Path, preferredModelPath, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault();

                _modelB.SelectedItem = available.FirstOrDefault(model =>
                    string.Equals(model.Path, currentB, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(model.Path, (_modelA.SelectedItem as LocalModelInfo)?.Path, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault(model =>
                        !string.Equals(model.Path, (_modelA.SelectedItem as LocalModelInfo)?.Path, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _modelA.EndUpdate();
                _modelB.EndUpdate();
            }
        }

        public void SetRunning(bool running)
        {
            RunOnUi(() =>
            {
                IsComparisonRunning = running;
                _modelA.Enabled = !running;
                _modelB.Enabled = !running;
                _prompt.Enabled = !running;
                _compareButton.Enabled = !running;
                _cancelButton.Enabled = running;
                if (!running)
                    _status.Text = "Comparaison terminée.";
            });
        }

        public void SetStage(int stage, string? detail = null)
        {
            RunOnUi(() =>
            {
                var model = stage == 1 ? _modelA.SelectedItem as LocalModelInfo : _modelB.SelectedItem as LocalModelInfo;
                var modelName = model?.Name ?? $"Modèle {stage}";
                _status.Text = string.IsNullOrWhiteSpace(detail)
                    ? $"Réponse {stage}/2 en cours : {modelName}"
                    : $"Réponse {stage}/2 — {detail}";
            });
        }

        public void SetStatus(string message, bool isError = false)
        {
            RunOnUi(() =>
            {
                _status.Text = message;
                _status.ForeColor = isError ? ThemeColors.Error : ThemeColors.TextSecondary;
            });
        }

        public void AppendToken(int stage, string token)
        {
            if (stage is < 1 or > 2 || string.IsNullOrEmpty(token) || IsDisposed)
                return;
            lock (_streamLock)
                _pendingText[stage - 1].Append(token);
        }

        public void FlushPendingTokens()
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(FlushPendingTokens)); } catch (InvalidOperationException) { }
                return;
            }

            for (var index = 0; index < _pendingText.Length; index++)
            {
                string batch;
                lock (_streamLock)
                {
                    if (_pendingText[index].Length == 0)
                        continue;
                    batch = _pendingText[index].ToString();
                    _pendingText[index].Clear();
                }

                var box = index == 0 ? _responseA : _responseB;
                box.AppendText(batch);
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            }
        }

        public void CompleteResponse(int stage, string content)
        {
            RunOnUi(() =>
            {
                FlushPendingTokens();
                var box = stage == 1 ? _responseA : _responseB;
                if (!string.IsNullOrEmpty(content) && !string.Equals(box.Text, content, StringComparison.Ordinal))
                    box.Text = content;
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            });
        }

        public void ClearResponses()
        {
            lock (_streamLock)
            {
                _pendingText[0].Clear();
                _pendingText[1].Clear();
            }
            _responseA.Clear();
            _responseB.Clear();
            _status.ForeColor = ThemeColors.TextSecondary;
            _status.Text = "Préparation de la comparaison…";
        }

        private void RequestComparison()
        {
            var modelA = _modelA.SelectedItem as LocalModelInfo;
            var modelB = _modelB.SelectedItem as LocalModelInfo;
            var prompt = _prompt.Text.Trim();
            if (modelA == null || modelB == null)
            {
                SetStatus("Sélectionne deux modèles locaux.", isError: true);
                return;
            }
            if (string.Equals(modelA.Path, modelB.Path, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Choisis deux modèles différents.", isError: true);
                return;
            }
            if (string.IsNullOrWhiteSpace(prompt))
            {
                SetStatus("Écris une demande à comparer.", isError: true);
                _prompt.Focus();
                return;
            }

            ClearResponses();
            SetRunning(true);
            CompareRequested?.Invoke(this, new ModelComparisonRequestedEventArgs(modelA, modelB, prompt));
        }

        private static void ConfigureResponseBox(RichTextBox box)
        {
            box.Dock = DockStyle.Fill;
            box.ReadOnly = true;
            box.DetectUrls = true;
            box.WordWrap = true;
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font("Segoe UI", 10f);
            box.BackColor = ThemeColors.Surface;
            box.ForeColor = ThemeColors.TextPrimary;
            box.ScrollBars = RichTextBoxScrollBars.Vertical;
            box.Padding = new Padding(10);
        }

        private static Control CreateModelSelector(string title, ComboBox selector)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0, 0, 20, 0),
                BackColor = ThemeColors.Background
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = ThemeColors.TextSecondary,
                Margin = new Padding(0, 0, 0, 2)
            });
            panel.Controls.Add(selector);
            return panel;
        }

        private static Control CreateResponsePanel(ComboBox modelSelector, RichTextBox response)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = ThemeColors.Surface, Padding = new Padding(10) };
            var heading = new Label
            {
                Text = (modelSelector.SelectedItem as LocalModelInfo)?.Name ?? "Modèle",
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            modelSelector.SelectedIndexChanged += (_, _) =>
                heading.Text = (modelSelector.SelectedItem as LocalModelInfo)?.Name ?? "Modèle";
            panel.Controls.Add(response);
            panel.Controls.Add(heading);
            return panel;
        }

        private void RunOnUi(Action action)
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
            {
                try { BeginInvoke(action); } catch (InvalidOperationException) { }
                return;
            }
            action();
        }
    }
}

