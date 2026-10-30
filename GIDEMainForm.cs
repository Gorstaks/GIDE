using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace GIDE
{
    /// <summary>
    /// GIDE Main Form - Professional IDE interface for AI coding
    /// Dark theme, chat interface, model selector, project browser
    /// </summary>
    public class GIDEMainForm : Form
    {
        // UI Components
        private SplitContainer mainSplit;
        private SplitContainer leftSplit;
        private Panel chatPanel;
        private Panel inputPanel;
        private Panel projectPanel;
        private Panel statusPanel;
        private Panel progressFillPanel;
        private Panel centerArea;
        private Label percentLabel;
        private RichTextBox chatHistory;
        private TextBox inputBox;
        private Button sendButton;
        private ComboBox modelSelector;
        private Button projectButton;
        private Button newChatButton;
        private TreeView projectTree;
        private Label statusLabel;
        private Label projectPathLabel;

        // Core components
        private GIDEClient _client;
        private HistoryManager _history;
        private string _currentProjectPath = "";
        private bool _isProcessing = false;
        private Thread _downloadThread;
        private bool _isDownloading = false;

        // Available models
        private List<ModelInfo> _availableModels;

        public class ModelInfo
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string Size { get; set; }
            public long SizeBytes { get; set; }
            public int MinRamGB { get; set; }
            public int MinVramGB { get; set; }
            public string DownloadUrl { get; set; }
            public bool IsDownloaded { get; set; }
            public bool IsRecommended { get; set; }
        }

        public GIDEMainForm()
        {
            InitializeModels();
            InitializeComponent();
            ApplyTheme();
            _client = new GIDEClient();

            // Use a safe default working directory
            string initialDir = Environment.CurrentDirectory;
            try
            {
                // Validate the path is legal, exists, and is writable
                Path.GetFullPath(initialDir);
                if (!Directory.Exists(initialDir) || IsProtectedPath(initialDir))
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch
            {
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            _currentProjectPath = initialDir;
            _history = new HistoryManager(initialDir);

            UpdateModelSelector();
            UpdateStatus("Ready");

            // Detect hardware in background so model selector updates without blocking UI
            ThreadPool.QueueUserWorkItem((_) =>
            {
                _client.DetectHardwareAndSelectModel();

                // Update model recommendations based on detected hardware
                try
                {
                    var hardware = HardwareDetector.GetHardwareInfo();
                    string recommended = HardwareDetector.RecommendModel(hardware).ModelId;

                    foreach (var model in _availableModels)
                    {
                        model.IsRecommended = (model.Id == recommended);
                        model.IsDownloaded = File.Exists(ModelManager.GetModelPath(model.Id));
                    }

                    this.Invoke(new Action(() =>
                    {
                        UpdateModelSelector();
                    }));
                }
                catch { }
            });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MicaHelper.Apply(this);
        }

        private void InitializeComponent()
        {
            this.Text = "GIDE";
            this.Size = new Size(1280, 820);
            this.MinimumSize = new Size(900, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.BgDeep;
            this.Font = Theme.Font(9.5f);
            this.ForeColor = Theme.Text;

            // Set icon from GIDE.ico file
            try
            {
                string appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string iconPath = Path.Combine(appDir, "GIDE.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new System.Drawing.Icon(iconPath);
                }
            }
            catch { }

            // Main container - gradient background with subtle glow for Mica-like depth
            GradientPanel mainContainer = new GradientPanel
            {
                Dock = DockStyle.Fill,
                GradientTop = Color.FromArgb(30, 31, 34),
                GradientBottom = Color.FromArgb(24, 25, 28),
                ShowGlow = true,
                Padding = new Padding(0)
            };

            // === TOP BAR (Menu + Model Selector) ===
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = Color.Transparent,
                Padding = new Padding(24, 14, 24, 14)
            };

            // Model selector on the right
            modelSelector = new ComboBox
            {
                Width = 240,
                Height = 36,
                Left = topBar.Width - 360,
                Top = 14,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.BgPanel,
                ForeColor = Theme.Text,
                Font = Theme.Font(10f),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            modelSelector.SelectedIndexChanged += ModelSelector_SelectedIndexChanged;
            topBar.Controls.Add(modelSelector);

            // Download button next to model selector
            RoundedButton downloadBtn = new RoundedButton
            {
                Text = "Download",
                Width = 100,
                Height = 36,
                Left = topBar.Width - 110,
                Top = 14,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Theme.BgPanel,
                HoverColor = Theme.BgPanelHi,
                PressColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Font = Theme.Font(9.5f, FontStyle.Bold),
                Radius = 12
            };
            downloadBtn.Click += DownloadBtn_Click;
            topBar.Controls.Add(downloadBtn);

            // Project button on the left - toggles sidebar
            RoundedButton toggleProjectBtn = new RoundedButton
            {
                Text = "Folder",
                Width = 90,
                Height = 36,
                Left = 24,
                Top = 14,
                BackColor = Theme.BgPanel,
                HoverColor = Theme.BgPanelHi,
                PressColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Font = Theme.Font(9.5f),
                Radius = 12
            };
            toggleProjectBtn.Click += (s, e) => {
                projectPanel.Visible = !projectPanel.Visible;
                toggleProjectBtn.Text = projectPanel.Visible ? "Close" : "Folder";
                this.PerformLayout();
            };
            topBar.Controls.Add(toggleProjectBtn);

            // New Chat button
            newChatButton = new RoundedButton
            {
                Text = "New Chat",
                Width = 100,
                Height = 36,
                Left = 124,
                Top = 14,
                BackColor = Theme.BgPanel,
                HoverColor = Theme.BgPanelHi,
                PressColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Font = Theme.Font(9.5f),
                Radius = 12
            };
            newChatButton.Click += NewChatButton_Click;
            topBar.Controls.Add(newChatButton);

            // === CENTER CONTENT AREA ===
            centerArea = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };

            // === CHAT HISTORY (fills the main area above input) ===
            chatHistory = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(32, 33, 36),
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.Font(11.5f),
                ReadOnly = true,
                Visible = false
            };

            // Wrapper to give chat history the same horizontal margins as input
            Panel chatMarginPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(80, 10, 80, 0),
                Visible = false
            };
            chatMarginPanel.Tag = "chatMarginPanel";
            chatMarginPanel.Controls.Add(chatHistory);

            // Welcome panel (shown initially, hidden after first message)
            Panel welcomePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent
            };
            welcomePanel.Tag = "welcomePanel";

            // Welcome label
            Label welcomeLabel = new Label
            {
                Text = "How can I help you today?",
                Font = Theme.Font(32f, FontStyle.Regular),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                AutoSize = true,
                Top = 90
            };
            welcomeLabel.Tag = "welcome";

            // Subtitle
            Label subtitleLabel = new Label
            {
                Text = "Free local AI - No API keys - Runs on your machine",
                Font = Theme.Font(10.5f, FontStyle.Regular),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                AutoSize = true,
                Top = 150
            };
            subtitleLabel.Tag = "subtitle";

            // === ACTION BUTTONS ===
            FlowLayoutPanel actionPanel = new FlowLayoutPanel
            {
                Top = 220,
                Height = 44,
                BackColor = Color.Transparent,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            // Action chips (pill-shaped)
            string[] actions = { "Help me write", "Learn about", "Analyze code", "Summarize", "See more" };
            foreach (var action in actions)
            {
                var capture = action;
                RoundedButton btn = new RoundedButton
                {
                    Text = action,
                    Width = 120,
                    Height = 36,
                    BackColor = Color.FromArgb(41, 42, 45),
                    HoverColor = Color.FromArgb(53, 54, 58),
                    PressColor = Color.FromArgb(60, 64, 67),
                    ForeColor = Theme.TextDim,
                    BorderColor = Theme.Border,
                    BorderWidth = 1,
                    Font = Theme.Font(9.5f),
                    Radius = 18,
                    Margin = new Padding(6)
                };
                btn.Click += (s, e) => {
                    if (capture == "Help me write")
                        inputBox.Text = "Help me write ";
                    else if (capture == "Learn about")
                        inputBox.Text = "I want to learn about ";
                    else if (capture == "Analyze code")
                        inputBox.Text = "Please analyze this code:\n\n";
                    else if (capture == "Summarize")
                        inputBox.Text = "Please summarize ";
                    inputBox.ForeColor = Theme.Text;
                    inputBox.Focus();
                };
                actionPanel.Controls.Add(btn);
            }

            welcomePanel.Controls.Add(actionPanel);
            welcomePanel.Controls.Add(subtitleLabel);
            welcomePanel.Controls.Add(welcomeLabel);

            welcomePanel.Resize += (s, e) => {
                welcomeLabel.Left = (welcomePanel.Width - welcomeLabel.Width) / 2;
                subtitleLabel.Left = (welcomePanel.Width - subtitleLabel.Width) / 2;
                actionPanel.Left = (welcomePanel.Width - actionPanel.Width) / 2;
            };

            // === BOTTOM INPUT PANEL (docked at bottom of center area) ===
            // Use a plain dark panel as the dock-bottom container
            Panel inputDockPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 130,
                BackColor = Color.Transparent,
                Padding = new Padding(80, 10, 80, 14)
            };

            // Inner rounded panel for the input area
            RoundedPanel inputWrapper = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(41, 42, 45),
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Radius = 14,
                Padding = new Padding(14, 10, 14, 10)
            };

            // Input textbox
            inputBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                BackColor = Color.FromArgb(41, 42, 45),
                ForeColor = Theme.TextDim,
                Font = Theme.Font(12f),
                BorderStyle = BorderStyle.None,
                Text = "Ask GIDE anything...",
                AcceptsReturn = true
            };
            inputBox.GotFocus += (s, e) => {
                if (inputBox.Text == "Ask GIDE anything...")
                {
                    inputBox.Text = "";
                    inputBox.ForeColor = Theme.Text;
                }
            };
            inputBox.LostFocus += (s, e) => {
                if (string.IsNullOrWhiteSpace(inputBox.Text))
                {
                    inputBox.Text = "Ask GIDE anything...";
                    inputBox.ForeColor = Theme.TextDim;
                }
            };
            inputBox.KeyDown += InputBox_KeyDown;

            // Bottom row with send button
            Panel bottomRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                BackColor = Color.FromArgb(41, 42, 45),
                Padding = new Padding(0, 4, 0, 0)
            };

            // Send button (right side)
            sendButton = new RoundedButton
            {
                Text = "Send",
                Width = 96,
                Height = 32,
                Dock = DockStyle.Right,
                BackColor = Theme.BgPanel,
                HoverColor = Theme.BgPanelHi,
                PressColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Font = Theme.Font(10f, FontStyle.Bold),
                Radius = 10
            };
            sendButton.Click += SendButton_Click;

            bottomRow.Controls.Add(sendButton);

            inputWrapper.Controls.Add(inputBox);
            inputWrapper.Controls.Add(bottomRow);

            inputDockPanel.Controls.Add(inputWrapper);

            // Assemble center area: input at bottom, chat/welcome fills rest
            centerArea.Controls.Add(welcomePanel);      // Dock Fill (shown initially)
            centerArea.Controls.Add(chatMarginPanel);   // Dock Fill (shown after first message)
            centerArea.Controls.Add(inputDockPanel);    // Dock Bottom

            // === STATUS BAR with fill-up progress ===
            statusPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = Color.FromArgb(32, 33, 36)
            };

            // Progress fill panel (hidden - progress shown via text only)
            progressFillPanel = new Panel
            {
                Visible = false,
                Width = 0,
                Height = 0
            };

            // Status text
            statusLabel = new Label
            {
                Text = "Ready",
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextDim,
                Font = Theme.Font(9f),
                Padding = new Padding(14, 6, 10, 0),
                BackColor = Color.Transparent
            };

            // Percentage label on the right
            percentLabel = new Label
            {
                Text = "",
                Dock = DockStyle.Right,
                Width = 70,
                ForeColor = Theme.Text,
                Font = Theme.Font(9f, FontStyle.Bold),
                Padding = new Padding(0, 6, 14, 0),
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
                Visible = false
            };

            // Add controls in correct order for proper z-index
            statusPanel.Controls.Add(progressFillPanel);  // Background fill first
            statusPanel.Controls.Add(statusLabel);        // Text on top
            statusPanel.Controls.Add(percentLabel);       // Percentage on top

            // === PROJECT SIDEBAR (Hidden by default) ===
            projectPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 270,
                BackColor = Theme.BgMid,
                Padding = new Padding(12, 12, 12, 12),
                Visible = false
            };

            // Open Folder button at top of sidebar
            RoundedButton openFolderBtn = new RoundedButton
            {
                Dock = DockStyle.Top,
                Height = 40,
                Text = "Open Folder",
                BackColor = Theme.BgPanel,
                HoverColor = Theme.BgPanelHi,
                PressColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderColor = Theme.Border,
                BorderWidth = 1,
                Font = Theme.Font(10f),
                Radius = 12,
                Margin = new Padding(0, 0, 0, 8)
            };
            openFolderBtn.Click += ProjectButton_Click;

            projectPathLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                Text = "No folder selected",
                ForeColor = Theme.TextFaint,
                BackColor = Theme.BgMid,
                Padding = new Padding(4, 8, 4, 4),
                Font = Theme.Font(8.5f)
            };

            projectTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgMid,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                ShowLines = false,
                ShowPlusMinus = true,
                Font = Theme.Font(9.5f),
                ImageList = CreateFileIcons()
            };
            projectTree.BeforeExpand += ProjectTree_BeforeExpand;

            projectPanel.Controls.Add(projectTree);
            projectPanel.Controls.Add(projectPathLabel);
            projectPanel.Controls.Add(openFolderBtn);

            // Assemble - control order matters for docking
            // Add in reverse order of docking priority
            mainContainer.Controls.Add(centerArea);
            mainContainer.Controls.Add(topBar);

            // Add controls to form in proper dock order (Fill first, then Left, then Bottom)
            this.Controls.Add(mainContainer);  // Dock Fill - fills remaining space
            this.Controls.Add(projectPanel); // Dock Left - takes left side
            this.Controls.Add(statusPanel);  // Dock Bottom - takes bottom

            // Menu
            CreateMenu();
        }

        private void CreateMenu()
        {
            MenuStrip menuStrip = new MenuStrip();
            menuStrip.BackColor = Theme.BgDeep;
            menuStrip.ForeColor = Theme.TextDim;
            menuStrip.Renderer = new ToolStripProfessionalRenderer(new GideMenuColors());
            menuStrip.Font = Theme.Font(9f);

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");
            fileMenu.DropDownItems.Add("Open Folder...", null, ProjectButton_Click);
            fileMenu.DropDownItems.Add("New Chat", null, NewChatButton_Click);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Exit", null, (s, e) => this.Close());

            ToolStripMenuItem toolsMenu = new ToolStripMenuItem("Tools");
            toolsMenu.DropDownItems.Add("Model Manager", null, (s, e) => OpenModelManager());
            toolsMenu.DropDownItems.Add("Clear History", null, NewChatButton_Click);

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Help");
            helpMenu.DropDownItems.Add("About GIDE", null, (s, e) =>
            {
                MessageBox.Show("GIDE v0.9.0 - Free Local AI Coding Assistant\n\n" +
                    "100% Free - No API Keys - Local Processing\n\n" +
                    "Uses Qwen3 models via llama.cpp",
                    "About GIDE", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            menuStrip.Items.Add(fileMenu);
            menuStrip.Items.Add(toolsMenu);
            menuStrip.Items.Add(helpMenu);

            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);
        }

        private void ApplyTheme()
        {
            this.BackColor = Theme.BgDeep;
        }

        // Custom dark + purple color table for the menu strip
        private class GideMenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Theme.BgPanelHi; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.BgPanelHi; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.BgPanelHi; } }
            public override Color MenuItemBorder { get { return Theme.Border; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.BgPanel; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.BgPanel; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color ToolStripDropDownBackground { get { return Theme.BgPanel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.BgPanel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.BgPanel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.BgPanel; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }

        private ImageList CreateFileIcons()
        {
            ImageList icons = new ImageList();
            icons.ColorDepth = ColorDepth.Depth32Bit;
            icons.ImageSize = new Size(16, 16);
            // Use system icons
            icons.Images.Add(SystemIcons.WinLogo); // Folder
            icons.Images.Add(SystemIcons.Application); // File
            return icons;
        }

        private void InitializeModels()
        {
            // Build model list without hardware detection initially (fast)
            // Will be updated once hardware detection completes in background
            _availableModels = new List<ModelInfo>
            {
                new ModelInfo
                {
                    Id = "qwen3-30b-a3b",
                    DisplayName = "Qwen3 30B A3B (Best Quality)",
                    Description = "Best quality coding (MoE). Requires 32GB RAM or 12GB VRAM.",
                    Size = "~18.6 GB",
                    SizeBytes = 18L * 1024 * 1024 * 1024 + 600L * 1024 * 1024,
                    MinRamGB = 32,
                    MinVramGB = 12,
                    DownloadUrl = "https://huggingface.co/Qwen/Qwen3-30B-A3B-GGUF/resolve/main/Qwen3-30B-A3B-Q4_K_M.gguf",
                    IsRecommended = false
                },
                new ModelInfo
                {
                    Id = "qwen3-14b",
                    DisplayName = "Qwen3 14B (Balanced)",
                    Description = "Good balance of quality and speed. 16GB RAM or 8GB VRAM.",
                    Size = "~9 GB",
                    SizeBytes = 9L * 1024 * 1024 * 1024,
                    MinRamGB = 16,
                    MinVramGB = 8,
                    DownloadUrl = "https://huggingface.co/Qwen/Qwen3-14B-GGUF/resolve/main/Qwen3-14B-Q4_K_M.gguf",
                    IsRecommended = false
                },
                new ModelInfo
                {
                    Id = "qwen3-8b",
                    DisplayName = "Qwen3 8B (Fast)",
                    Description = "Fast responses. Good for most coding. 8GB RAM.",
                    Size = "~5 GB",
                    SizeBytes = 5L * 1024 * 1024 * 1024,
                    MinRamGB = 8,
                    MinVramGB = 0,
                    DownloadUrl = "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
                    IsRecommended = false
                },
                new ModelInfo
                {
                    Id = "qwen3-4b",
                    DisplayName = "Qwen3 4B (Lightweight)",
                    Description = "Lightweight and fast. Works on any system. 4GB RAM.",
                    Size = "~2.5 GB",
                    SizeBytes = 2L * 1024 * 1024 * 1024 + 512L * 1024 * 1024,
                    MinRamGB = 4,
                    MinVramGB = 0,
                    DownloadUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
                    IsRecommended = false
                }
            };

            // Check which are downloaded
            foreach (var model in _availableModels)
            {
                string modelPath = ModelManager.GetModelPath(model.Id);
                model.IsDownloaded = File.Exists(modelPath);
            }
        }

        private void UpdateModelSelector()
        {
            modelSelector.Items.Clear();

            foreach (var model in _availableModels)
            {
                string status = model.IsDownloaded ? "[Ready] " : "";
                string rec = model.IsRecommended ? "[RECOMMENDED] " : "";
                modelSelector.Items.Add(rec + status + model.DisplayName);
            }

            // Select the recommended model or first available
            int recommendedIndex = _availableModels.FindIndex(m => m.IsRecommended && m.IsDownloaded);
            if (recommendedIndex < 0)
                recommendedIndex = _availableModels.FindIndex(m => m.IsRecommended);
            if (recommendedIndex < 0)
                recommendedIndex = 0;

            modelSelector.SelectedIndex = recommendedIndex;
        }

        private void DownloadBtn_Click(object sender, EventArgs e)
        {
            if (modelSelector.SelectedIndex < 0) return;

            var model = _availableModels[modelSelector.SelectedIndex];
            
            // Check if model actually exists on disk
            string modelPath = ModelManager.GetModelPath(model.Id);
            bool actuallyExists = File.Exists(modelPath);
            
            // Update the model's downloaded status to match reality
            model.IsDownloaded = actuallyExists;
            
            // Refresh the model selector display
            UpdateModelSelector();
            
            if (actuallyExists)
            {
                MessageBox.Show("Model is already downloaded.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            // Update the model's downloaded status
            model.IsDownloaded = false;

            var result = MessageBox.Show(
                string.Format("Download '{0}' ({1})?", model.DisplayName, model.Size),
                "Download Model",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                DownloadModel(model);
            }
        }

        private void ModelSelector_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (modelSelector.SelectedIndex < 0) return;

            var model = _availableModels[modelSelector.SelectedIndex];
            
            // Check if model actually exists on disk
            string modelPath = ModelManager.GetModelPath(model.Id);
            bool actuallyExists = File.Exists(modelPath);
            
            // Update the model's downloaded status
            model.IsDownloaded = actuallyExists;
            
            if (!actuallyExists)
            {
                var result = MessageBox.Show(
                    string.Format("'{0}' is not downloaded yet ({1}).\n\nDownload now?", model.DisplayName, model.Size),
                    "Download Model",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    DownloadModel(model);
                }
                else
                {
                    // Revert selection to a downloaded model
                    int downloadedIndex = _availableModels.FindIndex(m => m.IsDownloaded);
                    if (downloadedIndex >= 0 && downloadedIndex != modelSelector.SelectedIndex)
                    {
                        modelSelector.SelectedIndex = downloadedIndex;
                    }
                }
            }
            else
            {
                // Switch to this model
                _client.SwitchModel(model.Id);
                UpdateStatus("Using: " + model.DisplayName);
            }
        }

        private void DownloadModel(ModelInfo model)
        {
            if (_isDownloading) return;

            _isDownloading = true;
            UpdateStatus("Downloading " + model.DisplayName + "...");
            percentLabel.Visible = true;
            modelSelector.Enabled = false;
            sendButton.Enabled = false;

            _downloadThread = new Thread(() =>
            {
                try
                {
                    // Ensure TLS 1.2 is enabled for this thread
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                    string modelsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".gide", "models");

                    if (!Directory.Exists(modelsDir))
                        Directory.CreateDirectory(modelsDir);

                    string modelPath = Path.Combine(modelsDir, model.Id + ".gguf");
                    string tempPath = modelPath + ".tmp";

                    if (File.Exists(tempPath))
                        File.Delete(tempPath);

                    var request = (HttpWebRequest)WebRequest.Create(model.DownloadUrl);
                    request.UserAgent = "GIDE/0.6.0";
                    request.AllowAutoRedirect = true;

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        long totalBytes = response.ContentLength;
                        long receivedBytes = 0;
                        long lastBytes = 0;
                        DateTime lastUpdate = DateTime.Now;
                        byte[] buffer = new byte[65536];

                        using (var responseStream = response.GetResponseStream())
                        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                        {
                            int bytesRead;
                            while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                fileStream.Write(buffer, 0, bytesRead);
                                receivedBytes += bytesRead;

                                int percent = totalBytes > 0
                                    ? (int)((double)receivedBytes / totalBytes * 100)
                                    : 0;

                                if ((DateTime.Now - lastUpdate).TotalMilliseconds >= 500)
                                {
                                    double elapsed = (DateTime.Now - lastUpdate).TotalSeconds;
                                    long deltaBytes = receivedBytes - lastBytes;
                                    double mbps = elapsed > 0 ? (double)deltaBytes / (1024 * 1024) / elapsed : 0;

                                    int capturedPercent = percent;
                                    double capturedMbps = mbps;
                                    long capturedReceived = receivedBytes;

                                    this.Invoke(new Action(() =>
                                    {
                                        percentLabel.Text = capturedPercent + "%";

                                        string receivedMB = (capturedReceived / (1024.0 * 1024.0)).ToString("F0");
                                        string totalMB = totalBytes > 0 ? (totalBytes / (1024.0 * 1024.0)).ToString("F0") : "?";
                                        UpdateStatus(string.Format("Downloading... {0}% ({1}/{2} MB) | {3:F1} MB/s",
                                            capturedPercent, receivedMB, totalMB, capturedMbps));
                                    }));

                                    lastBytes = receivedBytes;
                                    lastUpdate = DateTime.Now;
                                }
                            }
                        }
                    }

                    // Verify and move
                    FileInfo fi = new FileInfo(tempPath);
                    if (fi.Length < 100 * 1024)
                    {
                        File.Delete(tempPath);
                        throw new Exception("Download failed - file too small");
                    }

                    if (File.Exists(modelPath))
                        File.Delete(modelPath);
                    File.Move(tempPath, modelPath);

                    model.IsDownloaded = true;

                    this.Invoke(new Action(() =>
                    {
                        UpdateModelSelector();
                        modelSelector.SelectedIndex = _availableModels.IndexOf(model);
                        UpdateStatus(model.DisplayName + " ready!");
                        _client.SwitchModel(model.Id);
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke(new Action(() =>
                    {
                        UpdateStatus("Download failed: " + ex.Message);
                        MessageBox.Show("Failed to download model:\n" + ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    _isDownloading = false;
                    this.Invoke(new Action(() =>
                    {
                        percentLabel.Visible = false;
                        percentLabel.Text = "";
                        modelSelector.Enabled = true;
                        sendButton.Enabled = true;
                    }));
                }
            });

            _downloadThread.IsBackground = true;
            _downloadThread.Start();
        }

        private void ProjectButton_Click(object sender, EventArgs e)
        {
            UpdateStatus("Opening folder dialog...");

            // Run dialog on separate thread to prevent UI freeze
            Thread dialogThread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Title = "Select Project Folder";
                        dialog.CheckFileExists = false;
                        dialog.CheckPathExists = true;
                        dialog.FileName = "[Select Folder]";
                        dialog.Filter = "Folders|*.";
                        dialog.ValidateNames = false;

                        if (dialog.ShowDialog() == DialogResult.OK)
                        {
                            string folderPath = Path.GetDirectoryName(dialog.FileName);
                            if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                            {
                                this.Invoke(new Action(() =>
                                {
                                    UpdateStatus("Loading project: " + folderPath);
                                    LoadProject(folderPath);
                                }));
                            }
                        }
                        else
                        {
                            this.Invoke(new Action(() =>
                            {
                                UpdateStatus("Folder selection cancelled");
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.Invoke(new Action(() =>
                    {
                        MessageBox.Show("Error opening folder:\n" + ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatus("Error: " + ex.Message);
                    }));
                }
            });

            dialogThread.SetApartmentState(ApartmentState.STA);
            dialogThread.Start();
        }

        private void LoadProject(string path)
        {
            try
            {
                _currentProjectPath = path;
                _history = new HistoryManager(path);
                projectPathLabel.Text = path;

                if (projectTree.InvokeRequired)
                {
                    projectTree.Invoke(new Action(() => LoadProjectTree(path)));
                }
                else
                {
                    LoadProjectTree(path);
                }

                // Update GIDE's working directory
                try
                {
                    Environment.CurrentDirectory = path;
                }
                catch { }

                UpdateStatus("Project loaded: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading project:\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadProjectTree(string path)
        {
            projectTree.Nodes.Clear();
            var rootNode = new TreeNode(Path.GetFileName(path), 0, 0);
            rootNode.Tag = path;
            rootNode.Nodes.Add("Loading...");
            projectTree.Nodes.Add(rootNode);
            rootNode.Expand();
        }

        private void ProjectTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode node = e.Node;
            string path = node.Tag as string;

            if (path == null) return;

            // Clear dummy node
            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "Loading...")
            {
                node.Nodes.Clear();

                try
                {
                    string[] dirs = Directory.GetDirectories(path);
                    string[] files = Directory.GetFiles(path);

                    foreach (string dir in dirs.OrderBy(d => d))
                    {
                        string name = Path.GetFileName(dir);
                        if (name.StartsWith(".")) continue; // Skip hidden

                        TreeNode dirNode = new TreeNode(name, 0, 0);
                        dirNode.Tag = dir;
                        dirNode.Nodes.Add("Loading...");
                        node.Nodes.Add(dirNode);
                    }

                    foreach (string file in files.OrderBy(f => f))
                    {
                        string name = Path.GetFileName(file);
                        if (name.StartsWith(".")) continue;

                        TreeNode fileNode = new TreeNode(name, 1, 1);
                        fileNode.Tag = file;
                        node.Nodes.Add(fileNode);
                    }
                }
                catch (Exception ex)
                {
                    node.Nodes.Add("Error: " + ex.Message);
                }
            }
        }

        private void ProjectTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            string path = e.Node.Tag as string;
            if (path == null) return;

            if (File.Exists(path))
            {
                // For now, just show file info in chat
                try
                {
                    string content = File.ReadAllText(path);
                    if (content.Length > 2000)
                        content = content.Substring(0, 2000) + "\n... [truncated]";

                    inputBox.Text = "Please review this file:\n\n```\n" + content + "\n```";
                    inputBox.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not read file: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
            {
                e.SuppressKeyPress = true;
                SendMessage();
            }
            else if (e.KeyCode == Keys.Enter && e.Shift)
            {
                // Allow Shift+Enter for new line
                e.SuppressKeyPress = false;
            }
        }

        private void SendButton_Click(object sender, EventArgs e)
        {
            SendMessage();
        }

        private void SendMessage()
        {
            string message = inputBox.Text.Trim();
            if (string.IsNullOrEmpty(message) || _isProcessing) return;

            if (_client == null)
            {
                AddMessage("Error", "Client not initialized", Theme.ErrorBubble);
                return;
            }

            inputBox.Clear();
            AddMessage("You", message, Theme.UserBubble);
            _history.AddUserMessage(message);

            _isProcessing = true;
            sendButton.Enabled = false;
            // Don't show progress bar for generation - only for downloads
            UpdateStatus("Processing...");

            bool hadError = false;
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (_client == null)
                    {
                        this.Invoke(new Action(() =>
                        {
                            AddMessage("Error", "Client not initialized", Theme.ErrorBubble);
                            UpdateStatus("Error: Client not initialized");
                        }));
                        return;
                    }

                    this.Invoke(new Action(() =>
                    {
                        UpdateStatus("Initializing AI engine (first run downloads components)...");
                    }));

                    // Get the currently selected model
                    var selectedModel = _availableModels[modelSelector.SelectedIndex];
                    var modelInfo = ModelManager.GetModelInfo(selectedModel.Id);
                    _client.SetModel(modelInfo);

                    // Start a status polling timer so the user sees progress
                    System.Threading.Timer statusTimer = null;
                    statusTimer = new System.Threading.Timer((_) =>
                    {
                        try
                        {
                            string status = _client.GetStatusMessage();
                            if (!string.IsNullOrEmpty(status))
                            {
                                this.Invoke(new Action(() => { UpdateStatus(status); }));
                            }
                        }
                        catch { }
                    }, null, 500, 1000);

                    bool initResult = _client.InitializeLocalEngine();

                    // Stop polling
                    statusTimer.Dispose();

                    if (!initResult)
                    {
                        hadError = true;
                        string errorMsg = _client.GetLastError();
                        if (string.IsNullOrEmpty(errorMsg))
                            errorMsg = "Model not downloaded or engine failed";
                        
                        this.Invoke(new Action(() =>
                        {
                            // Detect DLL/Bad Image errors and show a dedicated dialog
                            if (errorMsg.Contains("DLL_LOAD_ERROR") || errorMsg.Contains("Bad Image") ||
                                errorMsg.Contains("0xc000012f") || errorMsg.Contains("vcruntime") ||
                                errorMsg.Contains("not designed to run"))
                            {
                                ShowDllErrorDialog(errorMsg);
                            }

                            ShowChatInline();
                            AddMessage("Error", "Model engine failed:\n" + errorMsg + "\n\nModel: " + selectedModel.Id + "\nPath: " + ModelManager.GetModelPath(selectedModel.Id), Theme.ErrorBubble);
                            UpdateStatus("Error - see chat for details");
                        }));
                        return;
                    }

                    this.Invoke(new Action(() =>
                    {
                        UpdateStatus("Generating...");
                    }));

                    // Build full conversation history for context
                    var allMessages = _history.GetMessages();
                    // Ensure current message is included (it was already added to history above)
                    if (allMessages.Count == 0 || allMessages[allMessages.Count - 1].Content != message)
                    {
                        allMessages.Add(new ChatMessage { Role = "user", Content = message });
                    }

                    string systemPrompt = GetSystemPrompt();

                    // Trim conversation to fit context window
                    // Budget: ~28K chars total for system prompt + messages
                    int systemLen = systemPrompt.Length;
                    int maxMessageChars = Math.Max(4000, 28000 - systemLen);
                    var messages = new List<ChatMessage>();
                    int charCount = 0;
                    // Work backwards from most recent, trimming long assistant responses
                    for (int i = allMessages.Count - 1; i >= 0; i--)
                    {
                        string content = allMessages[i].Content;
                        // Trim long assistant messages to keep context manageable
                        if (allMessages[i].Role == "assistant" && content.Length > 1500)
                            content = content.Substring(0, 1500) + "\n... [trimmed for context]";

                        if (charCount + content.Length > maxMessageChars && messages.Count > 0)
                            break;
                        var trimmed = new ChatMessage { Role = allMessages[i].Role, Content = content };
                        messages.Insert(0, trimmed);
                        charCount += content.Length;
                    }

                    this.Invoke(new Action(() =>
                    {
                        UpdateStatus("Calling model...");
                    }));

                    string response = _client.Generate(messages, systemPrompt);

                    this.Invoke(new Action(() =>
                    {
                        ShowChatInline();

                        if (string.IsNullOrEmpty(response))
                        {
                            hadError = true;
                            AddMessage("Error", "Model returned empty response. Check if model file exists and server is running.", Theme.ErrorBubble);
                            UpdateStatus("Empty response - model not working");
                        }
                        else
                        {
                            // Show the model's response (strip tool markers for display)
                            string displayResponse = ToolParser.StripTools(response);
                            if (!string.IsNullOrWhiteSpace(displayResponse))
                                AddMessage("GIDE", displayResponse, Theme.AiBubble);
                            _history.AddAssistantMessage(response);

                            // Try to extract tool calls (model used markers correctly)
                            var toolCalls = ToolParser.Parse(response);

                            // If no tool markers, try converting markdown code blocks
                            if (toolCalls.Count == 0)
                            {
                                string converted = ToolParser.TryConvertMarkdownToTools(response, _currentProjectPath ?? Environment.CurrentDirectory);
                                if (converted != null)
                                    toolCalls = ToolParser.Parse(converted);
                            }

                            // If we found file operations, ask for confirmation before executing
                            if (toolCalls.Count > 0 && toolCalls.Exists(t => t.Type == "WRITE" || t.Type == "PATCH" || t.Type == "DELETE" || t.Type == "RENAME"))
                            {
                                ApplyWithConfirmation(toolCalls);
                            }
                            else if (ToolParser.HasUnwrittenCode(response))
                            {
                                // Model showed code but we couldn't determine target files.
                                // Auto-nudge: ask the model to emit proper tool markers.
                                NudgeModelToWrite(messages, systemPrompt);
                            }

                            UpdateStatus("Ready");
                        }
                    }));
                }
                catch (Exception ex)
                {
                    hadError = true;
                    this.Invoke(new Action(() =>
                    {
                        AddMessage("Error", "Exception: " + ex.Message + "\n\nStack: " + ex.StackTrace, Theme.ErrorBubble);
                        UpdateStatus("Error: " + ex.Message);
                    }));
                }
                finally
                {
                    _isProcessing = false;
                    this.Invoke(new Action(() =>
                    {
                        sendButton.Enabled = true;
                        // Only set Ready if there was no error
                        if (!hadError)
                        {
                            UpdateStatus("Ready");
                        }
                    }));
                }
            });
        }

        /// <summary>
        /// Shows a confirmation dialog listing files to be written/deleted/renamed, then executes if approved.
        /// </summary>
        private void ApplyWithConfirmation(List<ToolCall> toolCalls)
        {
            var writes = toolCalls.FindAll(t => t.Type == "WRITE");
            var patches = toolCalls.FindAll(t => t.Type == "PATCH");
            var deletes = toolCalls.FindAll(t => t.Type == "DELETE");
            var renames = toolCalls.FindAll(t => t.Type == "RENAME");
            int totalOps = writes.Count + patches.Count + deletes.Count + renames.Count;

            if (totalOps == 0) return;

            // Build summary of what will happen
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("GIDE wants to perform " + totalOps + " file operation(s):\n");

            foreach (var w in writes)
            {
                string path = w.Path ?? "(unknown)";
                int lines = string.IsNullOrEmpty(w.Content) ? 0 : w.Content.Split('\n').Length;
                bool exists = false;
                try
                {
                    string fullPath = Path.Combine(_currentProjectPath ?? Environment.CurrentDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                    exists = File.Exists(fullPath);
                }
                catch { }
                summary.AppendLine("  " + (exists ? "[MODIFY] " : "[CREATE] ") + path + " (" + lines + " lines)");
            }

            foreach (var p in patches)
            {
                string path = p.Path ?? "(unknown)";
                int oldLines = string.IsNullOrEmpty(p.OldText) ? 0 : p.OldText.Split('\n').Length;
                int newLines = string.IsNullOrEmpty(p.NewText) ? 0 : p.NewText.Split('\n').Length;
                summary.AppendLine("  [PATCH]  " + path + " (" + oldLines + " lines -> " + newLines + " lines)");
            }

            foreach (var d in deletes)
            {
                summary.AppendLine("  [DELETE] " + (d.Path ?? "(unknown)"));
            }

            foreach (var r in renames)
            {
                summary.AppendLine("  [RENAME] " + (r.Path ?? "?") + " -> " + (r.Destination ?? "?"));
            }

            var result = MessageBox.Show(
                summary.ToString() + "\n\nApply these changes?",
                "GIDE - Confirm File Changes",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                var executor = new ToolExecutor(_currentProjectPath ?? Environment.CurrentDirectory);
                var results = new System.Text.StringBuilder();

                foreach (var tool in toolCalls)
                {
                    // Only execute file operations (skip READ/LIST - those are informational)
                    if (tool.Type == "WRITE" || tool.Type == "PATCH" || tool.Type == "DELETE" || tool.Type == "RENAME" || tool.Type == "RUN")
                    {
                        string execResult = executor.Execute(tool);
                        results.AppendLine(execResult);
                    }
                }

                AddMessage("Applied", results.ToString().Trim(), Theme.AiBubble);
                UpdateStatus("Changes applied (" + totalOps + " operations)");

                // Refresh project tree if visible
                if (projectPanel.Visible && !string.IsNullOrEmpty(_currentProjectPath))
                    LoadProjectTree(_currentProjectPath);
            }
            else
            {
                AddMessage("GIDE", "Changes not applied. Let me know if you want me to adjust anything.", Theme.AiBubble);
                UpdateStatus("Changes rejected by user");
            }
        }

        /// <summary>
        /// When the model showed code in markdown but didn't use tool markers,
        /// send a follow-up prompt asking it to emit proper WRITE tool calls.
        /// </summary>
        private void NudgeModelToWrite(List<ChatMessage> messages, string systemPrompt)
        {
            // Add a nudge message to the conversation
            var nudgeMsg = new ChatMessage
            {
                Role = "user",
                Content = "You showed code above but didn't write it to disk. " +
                    "Please write ALL the code you just showed to the correct files using " +
                    "<<<TOOL:WRITE>>> format. Output the complete file contents. Do not explain, just write."
            };

            var nudgeMessages = new List<ChatMessage>(messages);
            nudgeMessages.Add(nudgeMsg);

            UpdateStatus("Writing to files...");

            // Run on background thread to avoid blocking UI
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string nudgeResponse = _client.Generate(nudgeMessages, systemPrompt);

                    this.Invoke(new Action(() =>
                    {
                        if (string.IsNullOrEmpty(nudgeResponse))
                        {
                            AddMessage("GIDE", "Couldn't write files automatically. Try saying 'write it' or 'apply the changes'.", Theme.AiBubble);
                            UpdateStatus("Ready");
                            return;
                        }

                        _history.AddAssistantMessage(nudgeResponse);

                        // Parse tool calls from the nudge response
                        var toolCalls = ToolParser.Parse(nudgeResponse);
                        if (toolCalls.Count == 0)
                        {
                            string converted = ToolParser.TryConvertMarkdownToTools(nudgeResponse, _currentProjectPath ?? Environment.CurrentDirectory);
                            if (converted != null)
                                toolCalls = ToolParser.Parse(converted);
                        }

                        if (toolCalls.Count > 0 && toolCalls.Exists(t => t.Type == "WRITE"))
                        {
                            ApplyWithConfirmation(toolCalls);
                        }
                        else
                        {
                            // Still couldn't get tool markers - show what we got
                            string display = ToolParser.StripTools(nudgeResponse);
                            if (!string.IsNullOrWhiteSpace(display))
                                AddMessage("GIDE", display, Theme.AiBubble);
                            AddMessage("GIDE", "I couldn't format the writes automatically. Try being more specific about what file to create/modify.", Theme.AiBubble);
                        }

                        UpdateStatus("Ready");
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke(new Action(() =>
                    {
                        AddMessage("Error", "Nudge failed: " + ex.Message, Theme.ErrorBubble);
                        UpdateStatus("Ready");
                    }));
                }
            });
        }

        private string GetSystemPrompt()
        {
            string fileTree = "";
            if (!string.IsNullOrEmpty(_currentProjectPath) && Directory.Exists(_currentProjectPath))
            {
                fileTree = GetProjectFileTree();
            }

            return "You are GIDE, a local AI coding assistant that writes code directly to files.\n\n" +
                "CRITICAL RULE: When you write or modify code, you MUST use tool markers to write to disk.\n" +
                "NEVER just show code in markdown - ALWAYS write it to the actual file.\n\n" +
                "FORMAT FOR WRITING FILES (you MUST use this every time you produce code):\n" +
                "<<<TOOL:WRITE>>>\n" +
                "relative/path/to/file.cs\n" +
                "<<<CONTENT>>>\n" +
                "entire file content here\n" +
                "<<<END_CONTENT>>>\n" +
                "<<<END_TOOL>>>\n\n" +
                "FORMAT FOR READING FILES:\n" +
                "<<<TOOL:READ>>>\n" +
                "relative/path/to/file.cs\n" +
                "<<<END_TOOL>>>\n\n" +
                "FORMAT FOR PARTIAL EDITS (use when changing a small part of a large file):\n" +
                "<<<TOOL:PATCH>>>\n" +
                "relative/path/to/file.cs\n" +
                "<<<OLD>>>\n" +
                "exact existing code to find (include 2-3 context lines)\n" +
                "<<<NEW>>>\n" +
                "replacement code\n" +
                "<<<END_PATCH>>>\n" +
                "<<<END_TOOL>>>\n\n" +
                "FORMAT FOR RUNNING COMMANDS:\n" +
                "<<<TOOL:RUN>>>\n" +
                "command here\n" +
                "<<<END_TOOL>>>\n\n" +
                "FORMAT FOR DELETING FILES:\n" +
                "<<<TOOL:DELETE>>>\n" +
                "relative/path/to/file.cs\n" +
                "<<<END_TOOL>>>\n\n" +
                "FORMAT FOR RENAMING/MOVING FILES:\n" +
                "<<<TOOL:RENAME>>>\n" +
                "old/path/to/file.cs\n" +
                "new/path/to/file.cs\n" +
                "<<<END_TOOL>>>\n\n" +
                "EXAMPLE - User says 'add a hello world endpoint':\n" +
                "I'll add a hello world endpoint to the API controller.\n\n" +
                "<<<TOOL:WRITE>>>\n" +
                "src/HelloController.cs\n" +
                "<<<CONTENT>>>\n" +
                "using Microsoft.AspNetCore.Mvc;\n\n" +
                "[ApiController]\n[Route(\"api/[controller]\")]\n" +
                "public class HelloController : ControllerBase\n{\n" +
                "    [HttpGet]\n    public string Get() => \"Hello, World!\";\n}\n" +
                "<<<END_CONTENT>>>\n" +
                "<<<END_TOOL>>>\n\n" +
                "RULES:\n" +
                "- For NEW files or MAJOR rewrites: use <<<TOOL:WRITE>>> with complete file content\n" +
                "- For SMALL EDITS to existing files: use <<<TOOL:PATCH>>> (much faster, saves tokens)\n" +
                "- PATCH requires the OLD text to match exactly - include 2-3 unchanged lines for context\n" +
                "- You can use multiple PATCH blocks on the same file for multiple changes\n" +
                "- If you need to see a file first, use <<<TOOL:READ>>> before modifying it\n" +
                "- The user does NOT need to tell you filenames - figure it out from the project structure\n" +
                "- If you explain something, explain BRIEFLY then write the code\n" +
                "- NEVER use markdown code blocks (```) for code that should be written to files\n\n" +
                "Current project: " + (_currentProjectPath ?? "(none)") + "\n" +
                "Project files:\n" + fileTree + "\n";
        }

        private string GetProjectFileTree()
        {
            if (string.IsNullOrEmpty(_currentProjectPath)) return "(No project loaded)";

            try
            {
                var lines = new List<string>();
                BuildFileTree(lines, _currentProjectPath, "", 0, 3);
                return string.Join("\n", lines.ToArray());
            }
            catch { return "(Unable to read project)"; }
        }

        private void BuildFileTree(List<string> lines, string path, string indent, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            try
            {
                string[] dirs = Directory.GetDirectories(path);
                string[] files = Directory.GetFiles(path);

                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".") || name == "bin" || name == "obj" || name == "node_modules") continue;
                    lines.Add(indent + name + "/");
                    BuildFileTree(lines, dir, indent + "  ", depth + 1, maxDepth);
                }

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(".")) continue;
                    lines.Add(indent + name);
                }
            }
            catch { }
        }

        private void ShowChatInline()
        {
            // Hide welcome panel, show chat history
            foreach (Control c in centerArea.Controls)
            {
                if (c is Panel && (c.Tag as string) == "welcomePanel")
                    c.Visible = false;
                if (c is Panel && (c.Tag as string) == "chatMarginPanel")
                    c.Visible = true;
            }
            chatHistory.Visible = true;
        }

        private void AddMessage(string sender, string message, Color color)
        {
            chatHistory.SelectionStart = chatHistory.TextLength;
            chatHistory.SelectionLength = 0;

            // Sender name
            chatHistory.SelectionColor = color;
            chatHistory.SelectionFont = Theme.Font(10f, FontStyle.Bold);
            chatHistory.AppendText(sender + "\n");

            // Message
            chatHistory.SelectionColor = Theme.Text;
            chatHistory.SelectionFont = Theme.FontMono(10.5f);
            chatHistory.AppendText(message + "\n\n");

            chatHistory.ScrollToCaret();
        }

        private void NewChatButton_Click(object sender, EventArgs e)
        {
            chatHistory.Clear();
            _client = new GIDEClient();
            _history.Clear();
            UpdateStatus("New chat started");
        }

        private void OpenModelManager()
        {
            var form = new GIDESettingsForm();
            form.ShowDialog(this);
            InitializeModels(); // Refresh
            UpdateModelSelector();
        }

        /// <summary>
        /// Checks if a path is a protected system location where writes would fail.
        /// </summary>
        private static bool IsProtectedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string lower = path.ToLowerInvariant().Replace('/', '\\');
            if (lower.StartsWith("c:\\program files")) return true;
            if (lower.StartsWith("c:\\windows")) return true;
            if (lower.StartsWith("c:\\programdata")) return true;
            return false;
        }

        /// <summary>
        /// Shows a user-friendly dialog when a DLL/Bad Image error is detected.
        /// Offers to auto-install the VC++ Redistributable or provides manual instructions.
        /// </summary>
        private void ShowDllErrorDialog(string errorDetails)
        {
            string title = "Missing Component - Visual C++ Redistributable";
            string message =
                "GIDE's AI engine could not start because a required system component is missing or corrupted.\n\n" +
                "This is usually caused by:\n" +
                "- Visual C++ Redistributable not installed\n" +
                "- A conflicting DLL from Python or Anaconda in your PATH\n\n" +
                "Would you like GIDE to download and install the Visual C++ Redistributable now?\n\n" +
                "(This requires administrator permission)";

            var result = MessageBox.Show(this, message, title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                UpdateStatus("Installing Visual C++ Redistributable...");
                var installResult = RuntimeDependencyManager.EnsureVCRedistInstalled();

                if (installResult.Success)
                {
                    string successMsg = "Visual C++ Redistributable installed successfully!\n\n" +
                                       "Please click Send again to retry, or restart GIDE.";
                    if (installResult.NeedsRestart)
                    {
                        successMsg = "Visual C++ Redistributable installed, but a restart may be needed.\n\n" +
                                     "Please restart GIDE and try again.";
                    }
                    MessageBox.Show(this, successMsg, "Installation Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatus("Ready - please retry");
                }
                else
                {
                    string failMsg = "Automatic installation did not succeed.\n\n" +
                                     installResult.Message + "\n\n" +
                                     "You can install it manually by downloading from:\n" +
                                     "https://aka.ms/vs/17/release/vc_redist.x64.exe";
                    MessageBox.Show(this, failMsg, "Installation Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatus("Error - manual install required");
                }
            }
            else
            {
                // User declined - show manual instructions
                string manualMsg =
                    "To fix this manually:\n\n" +
                    "1. Download the Visual C++ Redistributable (x64) from:\n" +
                    "   https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                    "2. Run the installer\n\n" +
                    "3. Restart GIDE\n\n" +
                    "If the error persists, check if Python or Anaconda is in your\n" +
                    "system PATH and try repairing that installation.";
                MessageBox.Show(this, manualMsg, "Manual Fix Instructions",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UpdateStatus(string text)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<string>(UpdateStatus), text);
                return;
            }
            statusLabel.Text = text;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isProcessing || _isDownloading)
            {
                var result = MessageBox.Show(
                    "Processing in progress. Close anyway?",
                    "Confirm",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            if (_client != null)
                _client.Shutdown();

            base.OnFormClosing(e);
        }
    }
}
