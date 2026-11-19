using System;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace LocalAIApp
{
    public sealed class SubagentActivityForm : Form
    {
        private readonly Label _statusLabel;
        private readonly RichTextBox _activityLog;
        private readonly Font _bodyFont = new("Segoe UI", 9.5f);
        private readonly Font _thinkingFont = new("Segoe UI", 9.5f, FontStyle.Italic);
        private readonly Font _codeFont = new("Consolas", 9f);
        private readonly Font _headerFont = new("Segoe UI Semibold", 9.5f);
        private string _sessionId = "";
        private string _currentActivityId = "";
        private string _currentBlock = "";
        private int _thinkingBlockStart = -1;
        private bool _allowClose;

        public SubagentActivityForm()
        {
            Text = "Activité des sous-agents";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 680);
            MinimumSize = new Size(640, 420);
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
            FormClosing += (s, e) =>
            {
                if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "En attente d’un sous-agent…",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeColors.TextMuted,
                Font = _headerFont
            };
            root.Controls.Add(_statusLabel, 0, 0);

            _activityLog = new RichTextBox
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
            root.Controls.Add(_activityLog, 0, 1);
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
            model ??= "sous-agent";
            kind ??= "status";
            content ??= "";

            if (!string.IsNullOrEmpty(sessionId) && !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                _sessionId = sessionId;
                _activityLog.Clear();
                _currentActivityId = "";
                _currentBlock = "";
                _thinkingBlockStart = -1;
            }

            if (!string.Equals(_currentActivityId, activityId, StringComparison.Ordinal))
            {
                _currentActivityId = activityId;
                _currentBlock = "";
                _thinkingBlockStart = -1;
            }

            if (kind == "clear_thinking")
            {
                if (_thinkingBlockStart >= 0 && _thinkingBlockStart <= _activityLog.TextLength)
                {
                    _activityLog.Select(_thinkingBlockStart, _activityLog.TextLength - _thinkingBlockStart);
                    _activityLog.SelectedText = "";
                }
                _thinkingBlockStart = -1;
                _currentBlock = "";
                ScrollToEnd();
                return;
            }

            if (kind is "thinking" or "generated")
            {
                var blockKey = activityId + ":" + kind;
                if (!string.Equals(_currentBlock, blockKey, StringComparison.Ordinal))
                {
                    StartBlock(blockKey, kind == "thinking" ? "Réflexion" : "Texte généré / appel d’outil", model,
                        kind == "thinking" ? ThemeColors.TextMuted : ThemeColors.Primary,
                        kind == "thinking" ? _thinkingFont : _headerFont);
                }

                AppendText(content, kind == "thinking" ? _thinkingFont : _codeFont,
                    kind == "thinking" ? ThemeColors.TextMuted : ThemeColors.TextPrimary);
                ScrollToEnd();
                return;
            }

            var (title, color) = kind switch
            {
                "start" => ("Démarrage", ThemeColors.Primary),
                "status" => ("État", ThemeColors.TextMuted),
                "tool_start" => ("Exécution d’un outil", ThemeColors.Warning),
                "tool_result" => ("Résultat de l’outil", ThemeColors.Success),
                "final" => ("Réponse du sous-agent", ThemeColors.Primary),
                "complete" => ("Terminé", ThemeColors.Success),
                "cancelled" => ("Annulé", ThemeColors.Warning),
                "error" => ("Erreur", ThemeColors.Error),
                _ => (kind, ThemeColors.TextMuted)
            };

            var font = kind is "tool_start" or "tool_result" ? _codeFont : _bodyFont;
            StartBlock(activityId + ":" + kind + ":" + _activityLog.TextLength, title, model, color, _headerFont);
            if (!string.IsNullOrWhiteSpace(content))
                AppendText(content, font, kind == "error" ? ThemeColors.Error : ThemeColors.TextPrimary);
            AppendText("\n", _bodyFont, ThemeColors.TextPrimary);

            if (kind is "status" or "tool_start" or "tool_result" or "complete" or "cancelled" or "error")
                _statusLabel.Text = $"{model} — {content.Split('\n', 2)[0]}";
            else if (kind == "start")
                _statusLabel.Text = $"{model} — sous-agent lancé";
            ScrollToEnd();
        }

        private void StartBlock(string key, string title, string model, Color color, Font font)
        {
            if (_activityLog.TextLength > 0 && !_activityLog.Text.EndsWith("\n", StringComparison.Ordinal))
                AppendText("\n", _bodyFont, ThemeColors.TextPrimary);

            _currentBlock = key;
            var start = _activityLog.TextLength;
            AppendText($"[{DateTime.Now:HH:mm:ss}] {title} — {model}\n", font, color);
            if (key.EndsWith(":thinking", StringComparison.Ordinal))
                _thinkingBlockStart = start;
        }

        private void AppendText(string text, Font font, Color color)
        {
            _activityLog.SelectionStart = _activityLog.TextLength;
            _activityLog.SelectionLength = 0;
            _activityLog.SelectionFont = font;
            _activityLog.SelectionColor = color;
            _activityLog.AppendText(text);
        }

        private void ScrollToEnd()
        {
            _activityLog.SelectionStart = _activityLog.TextLength;
            _activityLog.SelectionLength = 0;
            _activityLog.ScrollToCaret();
        }
    }
}
