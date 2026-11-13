using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace LocalAIApp
{
    internal sealed class ForumPostRecord
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("content")] public string Content { get; set; } = "";
        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("objective")] public string Objective { get; set; } = "";
        [JsonProperty("session_id")] public string SessionId { get; set; } = "";
        [JsonProperty("created_at")] public string CreatedAt { get; set; } = "";
        [JsonIgnore] public string SourcePath { get; set; } = "";

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Model) ? Title : $"{Title}  —  {Model}";
    }

    public sealed class ForumForm : Form
    {
        private readonly string _forumDirectory;
        private readonly string _subagentForumDirectory;
        private readonly ListBox _postsList;
        private readonly Label _listHeader;
        private readonly TextBox _titleBox;
        private readonly Label _metadataLabel;
        private readonly TextBox _contentBox;
        private readonly Label _statusLabel;
        private readonly Button _deleteButton;
        private readonly Button _clearButton;
        private readonly Button _sharedForumButton;
        private readonly Button _temporaryForumButton;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private bool _showingTemporaryForum;
        private bool _loadingPosts;

        public ForumForm()
        {
            _forumDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalIARunner", "Forum", "Posts");
            _subagentForumDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LocalIARunner", "Forum", "Subagents");

            Text = "Forum des IA";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 660);
            MinimumSize = new Size(720, 480);
            BackColor = ThemeColors.Background;
            ForeColor = ThemeColors.TextPrimary;
            Font = new Font("Segoe UI", 9.5f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = ThemeColors.Background
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);

            var forumModes = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = ThemeColors.Background
            };
            forumModes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            forumModes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _sharedForumButton = CreateButton("Forum partagé");
            _sharedForumButton.Click += (s, e) => SetForumMode(showTemporary: false);
            _temporaryForumButton = CreateButton("Forum temporaire des sous-agents");
            _temporaryForumButton.Click += (s, e) => SetForumMode(showTemporary: true);
            forumModes.Controls.Add(_sharedForumButton, 0, 0);
            forumModes.Controls.Add(_temporaryForumButton, 1, 0);
            root.Controls.Add(forumModes, 0, 0);

            var split = new SplitContainer
            {
                Size = new Size(900, 560),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 300,
                Panel1MinSize = 220,
                Panel2MinSize = 350,
                BackColor = ThemeColors.Border
            };
            Shown += (s, e) =>
            {
                if (split.Width > split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth)
                    split.SplitterDistance = Math.Clamp(300, split.Panel1MinSize, split.Width - split.Panel2MinSize - split.SplitterWidth);
            };
            root.Controls.Add(split, 0, 1);

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8),
                BackColor = ThemeColors.Surface
            };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _listHeader = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = ThemeColors.Primary,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            left.Controls.Add(_listHeader, 0, 0);
            _postsList = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = ThemeColors.CodeBackground,
                ForeColor = ThemeColors.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false
            };
            _postsList.SelectedIndexChanged += (s, e) => ShowSelectedPost();
            left.Controls.Add(_postsList, 0, 1);
            split.Panel1.Controls.Add(left);

            var details = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = ThemeColors.Surface
            };
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _titleBox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = ThemeColors.Surface,
                ForeColor = ThemeColors.TextPrimary,
                Font = new Font("Segoe UI Semibold", 13),
                Text = "Sélectionne une publication"
            };
            _metadataLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = ThemeColors.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _contentBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = ThemeColors.CodeBackground,
                ForeColor = ThemeColors.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10)
            };
            details.Controls.Add(_titleBox, 0, 0);
            details.Controls.Add(_metadataLabel, 0, 1);
            details.Controls.Add(_contentBox, 0, 2);
            split.Panel2.Controls.Add(details);

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = ThemeColors.Background
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = ThemeColors.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var refreshButton = CreateButton("Actualiser");
            refreshButton.Click += (s, e) => LoadPosts();
            _deleteButton = CreateButton("Supprimer");
            _deleteButton.Enabled = false;
            _deleteButton.Click += (s, e) => DeleteSelectedPost();
            _clearButton = CreateButton("Tout effacer");
            _clearButton.Click += (s, e) => ClearForum();
            var closeButton = CreateButton("Fermer");
            closeButton.Click += (s, e) => Close();
            actions.Controls.Add(_statusLabel, 0, 0);
            actions.Controls.Add(refreshButton, 1, 0);
            actions.Controls.Add(_deleteButton, 2, 0);
            actions.Controls.Add(_clearButton, 3, 0);
            actions.Controls.Add(closeButton, 4, 0);
            root.Controls.Add(actions, 0, 2);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _refreshTimer.Tick += (s, e) =>
            {
                if (_showingTemporaryForum && !IsDisposed)
                    LoadPosts();
            };
            _refreshTimer.Start();
            FormClosed += (s, e) => _refreshTimer.Stop();
            SetForumMode(showTemporary: false);
            LoadPosts();
        }

        private static Button CreateButton(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = ThemeColors.BorderLight,
            ForeColor = ThemeColors.TextPrimary,
            Margin = new Padding(4, 0, 4, 0)
        };

        private void SetForumMode(bool showTemporary)
        {
            _showingTemporaryForum = showTemporary;
            _listHeader.Text = showTemporary
                ? "RAPPORTS TEMPORAIRES DES SOUS-AGENTS"
                : "PUBLICATIONS LOCALES";
            _sharedForumButton.BackColor = showTemporary ? ThemeColors.BorderLight : ThemeColors.Primary;
            _temporaryForumButton.BackColor = showTemporary ? ThemeColors.Primary : ThemeColors.BorderLight;
            _temporaryForumButton.ForeColor = ThemeColors.TextPrimary;
            _deleteButton.Enabled = false;
            _clearButton.Enabled = !showTemporary;
            LoadPosts();
        }

        private void LoadPosts()
        {
            var posts = new List<ForumPostRecord>();
            var invalidCount = 0;
            var activeDirectory = _showingTemporaryForum ? _subagentForumDirectory : _forumDirectory;
            var selectedPath = (_postsList.SelectedItem as ForumPostRecord)?.SourcePath;
            try
            {
                if (Directory.Exists(activeDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(activeDirectory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var post = JsonConvert.DeserializeObject<ForumPostRecord>(File.ReadAllText(path));
                            if (post == null || string.IsNullOrWhiteSpace(post.Content)
                                    || (_showingTemporaryForum
                                        ? string.IsNullOrWhiteSpace(post.Title) && string.IsNullOrWhiteSpace(post.Objective)
                                        : string.IsNullOrWhiteSpace(post.Title)))
                            {
                                invalidCount++;
                                continue;
                            }
                            post.SourcePath = path;
                            if (_showingTemporaryForum && string.IsNullOrWhiteSpace(post.Title))
                                post.Title = string.IsNullOrWhiteSpace(post.Objective) ? "Rapport de sous-agent" : post.Objective;
                            posts.Add(post);
                        }
                        catch
                        {
                            invalidCount++;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _statusLabel.Text = "Lecture impossible : " + exception.Message;
                return;
            }

            posts.Sort((left, right) => string.Compare(right.CreatedAt, left.CreatedAt, StringComparison.Ordinal));
            _loadingPosts = true;
            _postsList.BeginUpdate();
            _postsList.Items.Clear();
            foreach (var post in posts)
                _postsList.Items.Add(post);
            var selectedIndex = posts.FindIndex(post =>
                !string.IsNullOrWhiteSpace(selectedPath) &&
                string.Equals(post.SourcePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0 && posts.Count > 0)
                selectedIndex = 0;
            if (selectedIndex >= 0)
                _postsList.SelectedIndex = selectedIndex;
            _postsList.EndUpdate();
            _loadingPosts = false;

            _statusLabel.Text = (_showingTemporaryForum
                ? $"{posts.Count} rapport(s) temporaire(s)"
                : $"{posts.Count} publication(s)") +
                (invalidCount > 0 ? $" · {invalidCount} fichier(s) illisible(s) ignoré(s)" : "");
            ShowSelectedPost();
        }

        private void ShowSelectedPost()
        {
            if (_loadingPosts)
                return;
            var post = _postsList.SelectedItem as ForumPostRecord;
            _deleteButton.Enabled = post != null && !_showingTemporaryForum;
            _clearButton.Enabled = !_showingTemporaryForum;
            if (post == null)
            {
                _titleBox.Text = "Aucune publication";
                _metadataLabel.Text = _showingTemporaryForum
                    ? "Aucun rapport de sous-agent enregistré."
                    : "Le forum local est vide.";
                _contentBox.Clear();
                return;
            }

            _titleBox.Text = post.Title;
            _metadataLabel.Text = _showingTemporaryForum
                ? $"{post.CreatedAt}  ·  Modèle : {post.Model}  ·  Session : {post.SessionId}"
                : $"{post.CreatedAt}  ·  Modèle : {post.Model}";
            _contentBox.Text = post.Content;
        }

        private void DeleteSelectedPost()
        {
            if (_showingTemporaryForum)
                return;
            if (_postsList.SelectedItem is not ForumPostRecord post)
                return;
            if (MessageBox.Show(this, $"Supprimer « {post.Title} » ?", "Forum des IA",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                var root = Path.GetFullPath(_forumDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(post.SourcePath);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Le chemin de publication est invalide.");
                File.Delete(path);
                LoadPosts();
            }
            catch (Exception exception)
            {
                _statusLabel.Text = "Suppression impossible : " + exception.Message;
            }
        }

        private void ClearForum()
        {
            if (_showingTemporaryForum)
                return;
            if (!Directory.Exists(_forumDirectory))
            {
                LoadPosts();
                return;
            }
            if (MessageBox.Show(this, "Supprimer toutes les publications du forum local ?", "Forum des IA",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try
            {
                foreach (var path in Directory.EnumerateFiles(_forumDirectory, "*.json", SearchOption.TopDirectoryOnly))
                    File.Delete(path);
                LoadPosts();
            }
            catch (Exception exception)
            {
                _statusLabel.Text = "Nettoyage impossible : " + exception.Message;
            }
        }
    }
}
