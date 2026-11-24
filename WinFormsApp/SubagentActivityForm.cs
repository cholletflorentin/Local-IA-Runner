using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LocalAIApp
{
    public sealed class SubagentActivityForm : Form
    {
        private sealed class AgentActivityPane
        {
            public string ActivityId { get; init; } = "";
            public required string Model { get; init; }
            public required int Number { get; init; }
            public required TabPage Page { get; init; }
            public required Label StatusLabel { get; init; }
            public required RichTextBox Log { get; init; }
            public string CurrentBlock { get; set; } = "";
            public int ThinkingBlockStart { get; set; } = -1;
            public int ThinkingBlockLength { get; set; }
            public string State { get; set; } = "En cours";
        }

        private readonly Label _summaryLabel;
        private readonly TabControl _activityTabs;
        private readonly Dictionary<string, AgentActivityPane> _agentPanes = new(StringComparer.Ordinal);
        private readonly Font _bodyFont = new("Segoe UI", 9.5f);
        private readonly Font _thinkingFont = new("Segoe UI", 9.5f, FontStyle.Italic);
        private readonly Font _codeFont = new("Consolas", 9f);
        private readonly Font _headerFont = new("Segoe UI Semibold", 9.5f);
        private readonly TabPage _waitingPage;
        private TabPage? _imagePage;
        private Label? _imageStatusLabel;
        private ProgressBar? _imageProgressBar;
        private RichTextBox? _imageLog;
        private PictureBox? _imagePreview;
        private string _imageState = "En attente d’une génération d’image…";
        private bool _allowClose;
        private int _imageGenerationNumber;

        public SubagentActivityForm()
        {
            Text = "Suivi des tâches IA";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1000, 720);
            MinimumSize = new Size(720, 480);
            BackColor = ThemeColors.Background;
            ForeColor = ThemeColors.TextPrimary;
            Font = _bodyFont;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12),
                BackColor = ThemeColors.Background
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            _summaryLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Les réflexions, outils et générations en cours apparaîtront ici.",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeColors.TextMuted,
                Font = _headerFont
            };
            root.Controls.Add(_summaryLabel, 0, 0);

            _activityTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                HotTrack = true,
                Font = _bodyFont
            };
            root.Controls.Add(_activityTabs, 0, 1);

            _waitingPage = new TabPage("Activité")
            {
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            var waitingLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "En attente d’un sous-agent ou d’une génération d’image…",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = ThemeColors.TextMuted
            };
            _waitingPage.Controls.Add(waitingLabel);
            _activityTabs.TabPages.Add(_waitingPage);

            FormClosing += (s, e) =>
            {
                if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            FormClosed += (s, e) => DisposeImagePreview();
            ThemeColors.ApplyToControlTree(this);
        }

        public void CloseForApplicationExit()
        {
            _allowClose = true;
            Close();
        }

        public void AppendActivity(string sessionId, string activityId, string model, string kind, string content)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendActivity(sessionId, activityId, model, kind, content)));
                return;
            }

            sessionId ??= "";
            activityId ??= "";
            model = string.IsNullOrWhiteSpace(model) ? "Sous-agent" : model;
            kind ??= "status";
            content ??= "";

            RemoveWaitingPageIfNeeded();
            var paneKey = string.IsNullOrEmpty(sessionId) ? activityId : sessionId + ":" + activityId;
            var pane = GetOrCreateAgentPane(paneKey, model);
            if (kind == "clear_thinking")
            {
                ClearThinking(pane);
                return;
            }

            if (kind is "thinking" or "generated")
            {
                var blockKey = kind;
                if (!string.Equals(pane.CurrentBlock, blockKey, StringComparison.Ordinal))
                {
                    var start = pane.Log.TextLength;
                    StartBlock(pane.Log,
                        kind == "thinking" ? "Réflexion" : "Texte généré / appel d’outil",
                        pane.Model,
                        kind == "thinking" ? ThemeColors.TextMuted : ThemeColors.Primary,
                        kind == "thinking" ? _thinkingFont : _headerFont);
                    pane.CurrentBlock = blockKey;
                    if (kind == "thinking")
                    {
                        pane.ThinkingBlockStart = start;
                        pane.ThinkingBlockLength = pane.Log.TextLength - start;
                    }
                }

                var priorLength = pane.Log.TextLength;
                AppendText(pane.Log, content,
                    kind == "thinking" ? _thinkingFont : _codeFont,
                    kind == "thinking" ? ThemeColors.TextMuted : ThemeColors.TextPrimary);
                if (kind == "thinking" && pane.ThinkingBlockStart >= 0)
                    pane.ThinkingBlockLength += pane.Log.TextLength - priorLength;
                ScrollToEnd(pane.Log);
                SetAgentState(pane, kind == "thinking" ? "Réflexion" : "Génération");
                return;
            }

            var (title, color, state) = kind switch
            {
                "start" => ("Démarrage", ThemeColors.Primary, "Démarrage"),
                "status" => ("État", ThemeColors.TextMuted, "En cours"),
                "tool_start" => ("Exécution d’un outil", ThemeColors.Warning, "Outil en cours"),
                "tool_result" => ("Résultat de l’outil", ThemeColors.Success, "Outil terminé"),
                "final" => ("Réponse du sous-agent", ThemeColors.Primary, "Réponse"),
                "complete" => ("Terminé", ThemeColors.Success, "Terminé"),
                "cancelled" => ("Annulé", ThemeColors.Warning, "Annulé"),
                "error" => ("Erreur", ThemeColors.Error, "Erreur"),
                _ => (kind, ThemeColors.TextMuted, kind)
            };

            var logFont = kind is "tool_start" or "tool_result" ? _codeFont : _bodyFont;
            StartBlock(pane.Log,
                title, pane.Model, color, _headerFont);
            if (!string.IsNullOrWhiteSpace(content))
                AppendText(pane.Log, content, logFont,
                    kind == "error" ? ThemeColors.Error : ThemeColors.TextPrimary);
            AppendText(pane.Log, "\n", _bodyFont, ThemeColors.TextPrimary);
            pane.CurrentBlock = "";
            ScrollToEnd(pane.Log);
            SetAgentState(pane, state + (string.IsNullOrWhiteSpace(content) ? "" : " — " + FirstLine(content)));
            if (kind is "start" or "tool_start")
                _activityTabs.SelectedTab = pane.Page;
        }

        public void AppendImageActivity(string kind, string content = "", int? completed = null,
            int? total = null, int? pass = null, long? downloaded = null, long? downloadTotal = null)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendImageActivity(
                    kind, content, completed, total, pass, downloaded, downloadTotal)));
                return;
            }

            EnsureImagePage();
            RemoveWaitingPageIfNeeded();
            content ??= "";

            switch (kind)
            {
                case "start":
                    _imageGenerationNumber++;
                    _imageState = "Préparation";
                    _imageProgressBar!.Value = 0;
                    _imageProgressBar.Maximum = 1000;
                    _imageProgressBar!.Style = ProgressBarStyle.Marquee;
                    _imageProgressBar.MarqueeAnimationSpeed = 24;
                    _imageStatusLabel!.ForeColor = ThemeColors.TextSecondary;
                    _imageStatusLabel.Text = content;
                    AppendImageLog($"Nouvelle génération #{_imageGenerationNumber}", ThemeColors.Primary);
                    AppendImageLog(content, ThemeColors.TextPrimary);
                    _activityTabs.SelectedTab = _imagePage;
                    break;
                case "progress":
                    _imageState = "Génération";
                    var stage = string.IsNullOrWhiteSpace(content) ? "Génération en cours…" : content;
                    if (pass.HasValue)
                        stage += $" — passe {pass.Value}";
                    if (completed.HasValue && total.HasValue && total.Value > 0)
                        stage += $" — {completed.Value}/{total.Value}";
                    SetImageProgress(completed, total);
                    SetImageStatus(stage, ThemeColors.TextSecondary);
                    AppendImageLog(stage, ThemeColors.TextPrimary);
                    break;
                case "engine_progress":
                    var downloadText = downloadTotal.GetValueOrDefault() > 0
                        ? $"Téléchargement du moteur : {FormatBytes(downloaded.GetValueOrDefault())} / {FormatBytes(downloadTotal.GetValueOrDefault())}"
                        : "Téléchargement du moteur d’images…";
                    SetImageStatus(downloadText, ThemeColors.TextSecondary);
                    var totalBytes = downloadTotal.GetValueOrDefault();
                    if (downloaded.HasValue && totalBytes > 0)
                    {
                        _imageProgressBar!.Style = ProgressBarStyle.Continuous;
                        _imageProgressBar.Maximum = 1000;
                        _imageProgressBar.Value = (int)Math.Clamp(
                            downloaded.Value * 1000 / totalBytes, 0, 1000);
                    }
                    AppendImageLog(downloadText, ThemeColors.TextPrimary);
                    break;
                case "preview":
                    SetImagePreview(content);
                    SetImageStatus("Aperçu actualisé", ThemeColors.TextSecondary);
                    break;
                case "complete":
                    _imageState = "Terminé";
                    StopImageProgress();
                    _imageProgressBar!.Value = _imageProgressBar.Maximum;
                    SetImageStatus(string.IsNullOrWhiteSpace(content) ? "Image générée." : content,
                        ThemeColors.Success);
                    AppendImageLog(string.IsNullOrWhiteSpace(content) ? "Génération terminée." : content,
                        ThemeColors.Success);
                    break;
                case "cancelled":
                    _imageState = "Annulée";
                    StopImageProgress();
                    SetImageStatus(string.IsNullOrWhiteSpace(content) ? "Génération annulée." : content,
                        ThemeColors.Warning);
                    AppendImageLog(string.IsNullOrWhiteSpace(content) ? "Génération annulée." : content,
                        ThemeColors.Warning);
                    break;
                case "error":
                    _imageState = "Erreur";
                    StopImageProgress();
                    SetImageStatus(string.IsNullOrWhiteSpace(content) ? "Échec de la génération." : content,
                        ThemeColors.Error);
                    AppendImageLog(string.IsNullOrWhiteSpace(content) ? "Échec de la génération." : content,
                        ThemeColors.Error);
                    break;
                default:
                    _imageState = "En cours";
                    SetImageStatus(content, ThemeColors.TextSecondary);
                    if (!string.IsNullOrWhiteSpace(content))
                        AppendImageLog(content, ThemeColors.TextPrimary);
                    break;
            }

            UpdateImageTabTitle();
            _summaryLabel.Text = "Génération d’image — " + _imageState;
        }

        private AgentActivityPane GetOrCreateAgentPane(string activityId, string model)
        {
            if (_agentPanes.TryGetValue(activityId, out var existing))
                return existing;

            var number = _agentPanes.Count + 1;
            var page = new TabPage($"{number}. {TrimTabText(model)}")
            {
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary,
                ToolTipText = model
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8),
                BackColor = ThemeColors.Background
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var status = new Label
            {
                Dock = DockStyle.Fill,
                Text = "En cours",
                ForeColor = ThemeColors.TextMuted,
                Font = _headerFont,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            var log = CreateLog();
            layout.Controls.Add(status, 0, 0);
            layout.Controls.Add(log, 0, 1);
            page.Controls.Add(layout);
            _activityTabs.TabPages.Add(page);
            ThemeColors.ApplyToControlTree(page);

            var pane = new AgentActivityPane
            {
                ActivityId = activityId,
                Model = model,
                Number = number,
                Page = page,
                StatusLabel = status,
                Log = log
            };
            _agentPanes[activityId] = pane;
            _summaryLabel.Text = $"{_agentPanes.Count} sous-agent(s) suivi(s) dans des onglets séparés.";
            return pane;
        }

        private void SetAgentState(AgentActivityPane pane, string state)
        {
            pane.State = state;
            pane.StatusLabel.Text = $"{pane.Model} — {state}";
            pane.StatusLabel.ForeColor = state.StartsWith("Erreur", StringComparison.Ordinal)
                ? ThemeColors.Error
                : state.StartsWith("Terminé", StringComparison.Ordinal)
                    ? ThemeColors.Success
                    : ThemeColors.TextMuted;
            pane.Page.Text = $"{pane.Number}. {TrimTabText(pane.Model)} — {ShortState(state)}";
            _summaryLabel.Text = $"{_agentPanes.Count} sous-agent(s) suivi(s) dans des onglets séparés.";
        }

        private void ClearThinking(AgentActivityPane pane)
        {
            if (pane.ThinkingBlockStart >= 0 && pane.ThinkingBlockLength > 0 &&
                pane.ThinkingBlockStart <= pane.Log.TextLength)
            {
                var length = Math.Min(pane.ThinkingBlockLength,
                    pane.Log.TextLength - pane.ThinkingBlockStart);
                pane.Log.Select(pane.ThinkingBlockStart, length);
                pane.Log.SelectedText = "";
            }
            pane.ThinkingBlockStart = -1;
            pane.ThinkingBlockLength = 0;
            pane.CurrentBlock = "";
            ScrollToEnd(pane.Log);
        }

        private void EnsureImagePage()
        {
            if (_imagePage != null && !_imagePage.IsDisposed)
                return;

            _imagePage = new TabPage("Images")
            {
                BackColor = ThemeColors.Background,
                ForeColor = ThemeColors.TextPrimary
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8),
                BackColor = ThemeColors.Background
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _imageStatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = _imageState,
                ForeColor = ThemeColors.TextMuted,
                Font = _headerFont,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            _imageProgressBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 1000,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = ThemeColors.Background,
                Padding = new Padding(0, 8, 0, 0)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            _imageLog = CreateLog();
            _imagePreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = ThemeColors.Surface,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            var previewPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8, 0, 0, 0),
                BackColor = ThemeColors.Background
            };
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewPanel.Controls.Add(new Label
            {
                Text = "Aperçu en direct",
                Dock = DockStyle.Fill,
                ForeColor = ThemeColors.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            previewPanel.Controls.Add(_imagePreview, 0, 1);
            body.Controls.Add(_imageLog, 0, 0);
            body.Controls.Add(previewPanel, 1, 0);
            layout.Controls.Add(_imageStatusLabel, 0, 0);
            layout.Controls.Add(_imageProgressBar, 0, 1);
            layout.Controls.Add(body, 0, 2);
            _imagePage.Controls.Add(layout);
            _activityTabs.TabPages.Add(_imagePage);
            ThemeColors.ApplyToControlTree(_imagePage);
        }

        private void SetImageProgress(int? completed, int? total)
        {
            if (_imageProgressBar == null || _imageProgressBar.IsDisposed)
                return;
            if (completed.HasValue && total.GetValueOrDefault() > 0)
            {
                StopImageProgress();
                _imageProgressBar.Value = 0;
                _imageProgressBar.Maximum = Math.Max(1, total!.Value);
                _imageProgressBar.Value = Math.Clamp(completed.Value, 0, _imageProgressBar.Maximum);
            }
            else
            {
                _imageProgressBar.Style = ProgressBarStyle.Marquee;
                _imageProgressBar.MarqueeAnimationSpeed = 24;
            }
        }

        private void StopImageProgress()
        {
            if (_imageProgressBar == null || _imageProgressBar.IsDisposed)
                return;
            if (_imageProgressBar.Style == ProgressBarStyle.Marquee)
                _imageProgressBar.MarqueeAnimationSpeed = 0;
            _imageProgressBar.Style = ProgressBarStyle.Continuous;
        }

        private void SetImageStatus(string text, Color color)
        {
            if (_imageStatusLabel == null || _imageStatusLabel.IsDisposed)
                return;
            _imageStatusLabel.Text = text;
            _imageStatusLabel.ForeColor = color;
        }

        private void AppendImageLog(string text, Color color)
        {
            if (_imageLog == null || _imageLog.IsDisposed || string.IsNullOrWhiteSpace(text))
                return;
            AppendText(_imageLog, $"[{DateTime.Now:HH:mm:ss}] {text}\n", _bodyFont, color);
            ScrollToEnd(_imageLog);
        }

        private void SetImagePreview(string encoded)
        {
            if (_imagePreview == null || _imagePreview.IsDisposed || string.IsNullOrWhiteSpace(encoded))
                return;
            try
            {
                var separator = encoded.IndexOf(',');
                if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && separator >= 0)
                    encoded = encoded[(separator + 1)..];
                if (encoded.Length > 24 * 1024 * 1024)
                    return;
                var bytes = Convert.FromBase64String(encoded);
                using var stream = new MemoryStream(bytes);
                using var decoded = Image.FromStream(stream);
                var replacement = new Bitmap(decoded);
                var previous = _imagePreview.Image;
                _imagePreview.Image = replacement;
                previous?.Dispose();
            }
            catch (Exception)
            {
                SetImageStatus("Un aperçu est arrivé, mais il n’a pas pu être affiché.", ThemeColors.Warning);
            }
        }

        private void DisposeImagePreview()
        {
            var preview = _imagePreview?.Image;
            if (_imagePreview != null)
                _imagePreview.Image = null;
            preview?.Dispose();
        }

        private void UpdateImageTabTitle()
        {
            if (_imagePage == null || _imagePage.IsDisposed)
                return;
            _imagePage.Text = "Images — " + _imageState;
        }

        private void RemoveWaitingPageIfNeeded()
        {
            if (_activityTabs.TabPages.Contains(_waitingPage))
                _activityTabs.TabPages.Remove(_waitingPage);
        }

        private RichTextBox CreateLog() => new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Both,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = ThemeColors.CodeBackground,
            ForeColor = ThemeColors.TextPrimary,
            Font = _bodyFont,
            HideSelection = false
        };

        private void StartBlock(RichTextBox log, string title, string model, Color color, Font font)
        {
            if (log.TextLength > 0 && !log.Text.EndsWith("\n", StringComparison.Ordinal))
                AppendText(log, "\n", _bodyFont, ThemeColors.TextPrimary);
            AppendText(log, $"[{DateTime.Now:HH:mm:ss}] {title} — {model}\n", font, color);
        }

        private static void AppendText(RichTextBox log, string text, Font font, Color color)
        {
            if (string.IsNullOrEmpty(text))
                return;
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionFont = font;
            log.SelectionColor = color;
            log.AppendText(text);
        }

        private static void ScrollToEnd(RichTextBox log)
        {
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.ScrollToCaret();
        }

        private static string FirstLine(string text)
        {
            var newline = text.IndexOfAny(new[] { '\r', '\n' });
            var first = newline >= 0 ? text[..newline] : text;
            return first.Length > 120 ? first[..117] + "…" : first;
        }

        private static string TrimTabText(string text) =>
            string.IsNullOrWhiteSpace(text) ? "Sous-agent" : text.Length > 22 ? text[..20] + "…" : text;

        private static string ShortState(string state)
        {
            if (state.StartsWith("Terminé", StringComparison.Ordinal)) return "Terminé";
            if (state.StartsWith("Annulé", StringComparison.Ordinal)) return "Annulé";
            if (state.StartsWith("Erreur", StringComparison.Ordinal)) return "Erreur";
            if (state.StartsWith("Outil", StringComparison.Ordinal)) return "Outil";
            if (state.StartsWith("Réflexion", StringComparison.Ordinal)) return "Réflexion";
            return "En cours";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " o";
            if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} Ko";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} Mo";
            return $"{bytes / (1024d * 1024 * 1024):0.0} Go";
        }
    }
}

