Warning: truncated output (original token count: 6524)
Total output lines: 614

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
                        pane.ThinkingBlockSt…3524 tokens truncated…;
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

