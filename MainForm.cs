using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// The settings window: a status card on top, a segmented tab bar, and three pages (Games, Settings, Log).
    /// Layout values are in 96-DPI pixels; WinForms scales them for the actual display DPI.
    /// Closing or minimizing hides it to the tray; the tray context disposes it a few seconds later to free memory.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int CardWidth = 680;   // initial width of full-width cards (the form is 720 wide with 20 px padding)

        private readonly HdrEngine _engine;
        private readonly Action<bool> _setPaused;
        private readonly Action<HdrOverride, TimeSpan?> _setOverride;
        private readonly Action _applyHotkeys;
        private readonly SegmentedTabs _hotkeyChoice = new() { Size = new Size(240, 34) };
        private static readonly string[] HotkeyModes = { HotkeySettings.WinAlt, HotkeySettings.CtrlAlt, HotkeySettings.Off };
        private readonly Icon _windowIcon = AppIcons.Create(IconStyle.Vivid, 48);
        private readonly ToolTip _toolTip = new();

        // Status card
        private readonly StatusCard _statusCard = new() { Radius = 12, Dock = DockStyle.Fill, Margin = new Padding(0) };
        private readonly Label _hdrLabel = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text };
        private readonly Label _detailLabel = new() { AutoSize = false, AutoEllipsis = true, Font = Theme.Body, ForeColor = Theme.Muted };
        private readonly ModernButton _pauseButton = new() { Glyph = Theme.GlyphPause, Text = "Pause automation" };
        private readonly ModernButton _trayButton = new() { Glyph = Theme.GlyphMinimize };

        // Tabs
        private readonly SegmentedTabs _tabs = new() { Size = new Size(330, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 16, 0, 0) };
        private readonly Panel _content = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        private readonly List<Control> _pages = new();

        // Games page
        private readonly TextBox _search = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Search games…" };
        private readonly GameList _games = new() { Dock = DockStyle.Fill };
        private readonly Label _gamesCount = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(12, 0, 2, 0) };
        private readonly ModernButton _scanButton = new() { Primary = true, Glyph = Theme.GlyphRefresh, Text = "Scan games", AutoSize = true };

        // Settings page
        private readonly ToggleSwitch _swAutostart = new();
        private readonly ToggleSwitch _swStartupScan = new();
        private readonly ToggleSwitch _swShowOthers = new();
        private readonly Stepper _minimumHdr = new() { Minimum = 0, Maximum = 120, Step = 5, ZeroText = "Off" };
        private readonly List<(MonitorInfo Monitor, ToggleSwitch Switch)> _monitorSwitches = new();
        private readonly ToggleSwitch _swBrowserWindowed = new();
        private readonly ToggleSwitch _swKeepBrowserHdr = new();
        private readonly ToggleSwitch _swColorProfile = new();
        private readonly ToggleSwitch _swRefreshApps = new();
        private readonly Stepper _fallback = new() { Minimum = 0, Maximum = 30 };

        // Video & RTX HDR
        private readonly ToggleSwitch _swVideoAuto = new();
        private readonly SegmentedTabs _videoMode = new() { Size = new Size(240, 34) };
        private static readonly string[] VideoModes = { VideoHdrModes.Smart, VideoHdrModes.NativeOnly, VideoHdrModes.Always };
        private readonly SegmentedTabs _videoTrigger = new() { Size = new Size(200, 34) };
        private static readonly string[] VideoTriggerModes = { VideoTriggers.Fullscreen, VideoTriggers.Any };
        private readonly ToggleSwitch _swVideoBrowsers = new();
        private readonly ToggleSwitch _swVideoPlayers = new();
        private readonly Stepper _videoRevert = new() { Minimum = 0, Maximum = 30 };
        private readonly ToggleSwitch _swVideoBattery = new();

        // Log page
        private readonly TextBox _log = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
            BorderStyle = BorderStyle.None
        };

        private bool _loadingSettings;
        private bool _reallyExit;
        private int _logCursor;
        private int _lastState = -1;
        private bool _scanInFlight;

        public event EventHandler? HiddenToTray;

        public MainForm(HdrEngine engine, Action<bool> setPaused, Action<HdrOverride, TimeSpan?> setOverride, Action applyHotkeys)
        {
            _engine = engine;
            _setPaused = setPaused;
            _setOverride = setOverride;
            _applyHotkeys = applyHotkeys;
            _hotkeyChoice.AddTab("Win+Alt");
            _hotkeyChoice.AddTab("Ctrl+Alt");
            _hotkeyChoice.AddTab("Off");
            _videoMode.AddTab("Smart");
            _videoMode.AddTab("HDR only");
            _videoMode.AddTab("Always");
            _videoTrigger.AddTab("Full screen");
            _videoTrigger.AddTab("Any");

            SuspendLayout();

            Text = "Smart HDR Manager";
            Icon = _windowIcon;
            Font = Theme.Body;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            BuildStatusCard();
            root.Controls.Add(_statusCard, 0, 0);

            _tabs.AddTab("Games");
            _tabs.AddTab("Settings");
            _tabs.AddTab("Log");
            root.Controls.Add(_tabs, 0, 1);

            AddPage(BuildGamesPage());
            AddPage(BuildSettingsPage());
            AddPage(BuildLogPage());
            root.Controls.Add(_content, 0, 2);

            // Designer-style auto scaling: everything above is in 96-DPI pixels
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 660);
            MinimumSize = new Size(660, 560);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            LayoutStatusCard();
            ShowPage(0);
            WireEvents();
            LoadSettings();
            RebuildGames();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _engine.LibraryChanged -= OnLibraryChanged;
            base.Dispose(disposing);
            if (disposing)
            {
                _toolTip.Dispose();
                _windowIcon.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
        private void BuildStatusCard()
        {
            const int h = 104;
            _statusCard.Size = new Size(CardWidth, h);
            _statusCard.IndicatorX = 28;

            // Initial bounds (96-DPI pixels); the real positions are set in LayoutStatusCard on every resize,
            // because edge anchoring combined with DPI scaling can stretch the label over the buttons.
            _hdrLabel.Location = new Point(52, 18);
            _trayButton.SetBounds(CardWidth - 18 - 40, (h - 34) / 2, 40, 34);
            _pauseButton.SetBounds(_trayButton.Left - 8 - 196, (h - 34) / 2, 196, 34);
            _detailLabel.SetBounds(54, 60, _pauseButton.Left - 16 - 54, 22);
            _toolTip.SetToolTip(_trayButton, "Minimize to tray");

            // Transparent so the animated HDR sweep on the card shows behind the text and around the buttons
            _hdrLabel.BackColor = Color.Transparent;
            _detailLabel.BackColor = Color.Transparent;
            _pauseButton.BackColor = Color.Transparent;
            _trayButton.BackColor = Color.Transparent;

            // Buttons first = on top of the labels
            _statusCard.Controls.Add(_trayButton);
            _statusCard.Controls.Add(_pauseButton);
            _statusCard.Controls.Add(_hdrLabel);
            _statusCard.Controls.Add(_detailLabel);
            // Layout (not Resize): runs after WinForms has finished DPI-scaling the child controls,
            // so the positions set here are not scaled a second time.
            _statusCard.Layout += (_, _) => LayoutStatusCard();
        }

        private void LayoutStatusCard()
        {
            int S(int v) => _statusCard.LogicalToDeviceUnits(v);
            int w = _statusCard.ClientSize.Width;
            int h = _statusCard.ClientSize.Height;

            _trayButton.Location = new Point(w - S(18) - _trayButton.Width, (h - _trayButton.Height) / 2);
            _pauseButton.Location = new Point(_trayButton.Left - S(8) - _pauseButton.Width, (h - _pauseButton.Height) / 2);

            // Title + detail as one block, vertically centred
            int detailHeight = _detailLabel.PreferredHeight;
            int top = Math.Max(0, (h - _hdrLabel.Height - S(2) - detailHeight) / 2);
            int left = S(52);
            _hdrLabel.Location = new Point(left, top);
            _detailLabel.SetBounds(left + S(2), _hdrLabel.Bottom + S(2), Math.Max(0, _pauseButton.Left - S(16) - left), detailHeight);
        }

        private void AddPage(Control page)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages.Add(page);
            _content.Controls.Add(page);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;
        }

        private Control BuildGamesPage()
        {
            var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // hint + count
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));    // search
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // list
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));    // buttons

            // Hint + count
            var hintRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var hint = new Label
            {
                Text = "Turn a game on to use HDR while it runs",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 0, 0, 0)
            };
            var othersLabel = new Label
            {
                Text = "Other programs", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(16, 0, 6, 0)
            };
            _swShowOthers.Anchor = AnchorStyles.Right;
            _swShowOthers.Margin = new Padding(0);
            _toolTip.SetToolTip(_swShowOthers, "Also list programs found by the drive scan that are not in a game library");
            _toolTip.SetToolTip(othersLabel, "Also list programs found by the drive scan that are not in a game library");
            hintRow.Controls.Add(hint, 0, 0);
            hintRow.Controls.Add(_gamesCount, 1, 0);
            hintRow.Controls.Add(othersLabel, 2, 0);
            hintRow.Controls.Add(_swShowOthers, 3, 0);
            page.Controls.Add(hintRow, 0, 0);

            // Search box: rounded field with a search icon
            var searchCard = new RoundedPanel
            {
                Radius = 8, BackColor = Theme.Input, BorderColor = Theme.Border,
                Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 10), Size = new Size(CardWidth, 34)
            };
            var searchIcon = new Label
            {
                Text = Theme.GlyphSearch, Font = Theme.IconFont, ForeColor = Theme.Muted, BackColor = Theme.Input,
                TextAlign = ContentAlignment.MiddleCenter, AutoSize = false
            };
            searchIcon.SetBounds(8, 0, 26, 34);
            _search.BackColor = Theme.Input;
            _search.ForeColor = Theme.Text;
            _search.Font = Theme.Body;
            _search.SetBounds(38, (34 - _search.PreferredHeight) / 2, CardWidth - 50, _search.PreferredHeight);
            searchCard.Controls.Add(searchIcon);
            searchCard.Controls.Add(_search);
            searchCard.Layout += (_, _) =>   // after DPI scaling, see LayoutStatusCard
            {
                // explicit layout instead of anchoring (DPI-safe), text box vertically centred
                int S(int v) => searchCard.LogicalToDeviceUnits(v);
                int ch = searchCard.ClientSize.Height;
                searchIcon.SetBounds(S(8), S(1), S(26), Math.Max(0, ch - S(2)));
                int x = S(40);
                _search.SetBounds(x, Math.Max(0, (ch - _search.Height) / 2), Math.Max(0, searchCard.ClientSize.Width - x - S(12)), _search.Height);
            };
            page.Controls.Add(searchCard, 0, 1);

            // Game list card
            var listCard = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(6) };
            listCard.Controls.Add(_games);
            page.Controls.Add(listCard, 0, 2);

            // Actions
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(0) };
            var addExe = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add program", AutoSize = true };
            var addRunning = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add running", AutoSize = true };
            var exclude = new ModernButton { Glyph = Theme.GlyphCancel, Text = "Never HDR", AutoSize = true };
            var options = new ModernButton { Glyph = Theme.GlyphSettings, Text = "Options", AutoSize = true };
            _toolTip.SetToolTip(options, "Per-game options for the selected game: delay before HDR, keep HDR after it closes");
            options.Click += (_, _) => OnGameOptions();
            _toolTip.SetToolTip(addExe, "Pick a game's .exe file");
            _toolTip.SetToolTip(addRunning, "Pick from programs that are running right now");
            _toolTip.SetToolTip(exclude, "Remove the selected game from the list and never use HDR for it");
            addExe.Click += (_, _) => OnAddExe();
            addRunning.Click += (_, _) => OnAddRunning();
            exclude.Click += (_, _) => OnExclude();
            _scanButton.Click += OnScan;

            _toolTip.SetToolTip(_scanButton, "Re-read the Steam, Epic, GOG and Xbox libraries and scan your game folders");
            foreach (var b in new Control[] { _scanButton, addExe, addRunning, options, exclude })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                buttons.Controls.Add(b);
            }
            page.Controls.Add(buttons, 0, 3);

            return page;
        }

        private Control BuildSettingsPage()
        {
            var page = new SmoothScrollTable { ColumnCount = 1, Margin = new Padding(0), AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);

            void Add(Control c, int height)
            {
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                page.Controls.Add(c, 0, page.RowStyles.Count - 1);
            }

            Add(SectionHeader("General"), 34);
            Add(SettingRow("Start with Windows", "Starts minimized in the system tray when you sign in.", _swAutostart), 68);
            Add(SettingRow("Scan drives automatically", "Looks for newly installed games at startup, at most once a week.", _swStartupScan), 68);
            Add(SettingRow("Keyboard shortcuts", "Modifier + H: HDR for the game in front  ·  + I: never HDR  ·  + O: override", _hotkeyChoice), 68);

            Add(SectionHeader("Monitors"), 42);
            foreach (var monitor in _engine.GetMonitors())
            {
                var sw = new ToggleSwitch { Enabled = monitor.HdrSupported };
                string title = monitor.IsPrimary ? $"{monitor.Name}  (primary)" : monitor.Name;
                string desc = !monitor.HdrSupported
                    ? "This display does not support HDR."
                    : $"Switch HDR on this display  ·  HDR currently {(monitor.HdrEnabled ? "on" : "off")}";
                _monitorSwitches.Add((monitor, sw));
                Add(SettingRow(title, desc, sw), 68);
            }

            Add(SectionHeader("HDR behaviour"), 42);
            Add(SettingRow("Browser videos in a window", "Allow HDR for YouTube when the video is not fullscreen.", _swBrowserWindowed), 68);
            Add(SettingRow("Keep HDR when the browser loses focus", "Stays on while an HDR video plays and another window is active.", _swKeepBrowserHdr), 68);
            Add(SettingRow("Return to SDR after", "Wait this long without HDR content before switching back.", _fallback), 68);
            Add(SettingRow("Minimum time in HDR", "Once HDR is on, keep it on at least this long. Fewer black screens.", _minimumHdr), 68);
            Add(SettingRow("Reload color profile after SDR", "Re-applies your display calibration when leaving HDR.", _swColorProfile), 68);
            Add(SettingRow("Refresh apps after SDR", "Fixes Chrome and other apps staying dimmed after leaving HDR.", _swRefreshApps), 68);

            Add(SectionHeader("RTX Video HDR"), 42);
            Add(SettingRow("Switch to HDR for videos", "Turns HDR on while a video plays, and off again afterwards.", _swVideoAuto), 68);
            Add(SettingRow("Which videos", "Smart: HDR videos, plus SDR videos for RTX Video HDR.", _videoMode), 68);
            Add(SettingRow("When", "Full screen only avoids switching for small players.", _videoTrigger), 68);
            Add(SettingRow("Web browsers", "Chrome, Edge, Brave, Firefox · uses the userscript.", _swVideoBrowsers), 68);
            Add(SettingRow("Video players", "VLC, mpv, MPC-HC, PotPlayer · while they play sound.", _swVideoPlayers), 68);
            Add(SettingRow("Back to SDR after the video stops", "Avoids black screens when skipping through a playlist.", _videoRevert), 68);
            Add(SettingRow("Turn off on battery power", "Laptops: no video HDR while unplugged.", _swVideoBattery), 68);

            var videoCard = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(16, 12, 16, 12) };
            videoCard.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted,
                Text = RtxVideoStatus.Query().Describe() + " Only the monitor with the video is switched. " +
                       "Games always come first, and videos never switch HDR while OBS is running. " +
                       "\"HDR only\": SDR videos stay in SDR. \"Always\": every video switches to HDR."
            });
            Add(videoCard, 116);

            Add(SectionHeader("Advanced"), 42);
            var advanced = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var openConfig = new ModernButton { Glyph = Theme.GlyphDocument, Text = "Open config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var reload = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "Reload config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var openLog = new ModernButton { Glyph = Theme.GlyphFolder, Text = "Open log file", AutoSize = true, Margin = new Padding(0) };
            openConfig.Click += (_, _) => OpenInNotepad(HdrEngine.ConfigPath);
            openLog.Click += (_, _) => OpenInNotepad(HdrEngine.LogPath);
            reload.Click += (_, _) =>
            {
                if (_engine.ReloadConfig()) { LoadSettings(); RebuildGames(); }
                else MessageBox.Show(this, "config.json could not be read. See the Log tab.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            advanced.Controls.Add(openConfig);
            advanced.Controls.Add(reload);
            advanced.Controls.Add(openLog);
            Add(advanced, 52);

            var infoCard = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), Padding = new Padding(16, 12, 16, 12) };
            infoCard.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted,
                Text = "Browser videos need the Tampermonkey script that came with this app. It marks the browser tab while a video " +
                       "is playing (HDR or SDR), and this app reacts to that marker. Program lists (browsers, video players, SDR-only apps, " +
                       "apps that block video HDR) and the marker texts can be changed in config.json."
            });
            Add(infoCard, 112);

            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // filler
            return page;
        }

        private static Label SectionHeader(string text) => new()
        {
            Text = text, Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(2, 0, 0, 8), AutoSize = false
        };

        private static RoundedPanel SettingRow(string title, string description, Control control)
        {
            const int w = CardWidth - 8;   // a little narrower: room for the scrollbar
            const int h = 60;

            var row = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Size = new Size(w, h) };

            int textWidth = w - 18 - control.Width - 34;
            var titleLabel = new Label { Text = title, Font = Theme.Semibold, ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true };
            titleLabel.SetBounds(18, 10, textWidth, 22);

            var descLabel = new Label { Text = description, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true };
            descLabel.SetBounds(18, 32, textWidth, 20);

            control.Location = new Point(w - 18 - control.Width, (h - control.Height) / 2);

            row.Controls.Add(control);
            row.Controls.Add(titleLabel);
            row.Controls.Add(descLabel);

            // Explicit layout (DPI-safe): control on the right, text uses the remaining width
            row.Layout += (_, _) =>   // after DPI scaling, see LayoutStatusCard
            {
                int S(int v) => row.LogicalToDeviceUnits(v);
                int rw = row.ClientSize.Width;
                int rh = row.ClientSize.Height;
                control.Location = new Point(rw - S(18) - control.Width, (rh - control.Height) / 2);

                int textWidth2 = Math.Max(0, control.Left - S(16) - S(18));
                int titleHeight = titleLabel.PreferredHeight;
                int descHeight = descLabel.PreferredHeight;
                int top = Math.Max(0, (rh - titleHeight - descHeight) / 2);
                titleLabel.SetBounds(S(18), top, textWidth2, titleHeight);
                descLabel.SetBounds(S(18), top + titleHeight, textWidth2, descHeight);
            };
            return row;
        }

        private Control BuildLogPage()
        {
            var card = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Padding = new Padding(14, 12, 6, 12), Margin = new Padding(0) };
            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.Text;
            _log.Font = Theme.Mono;
            Theme.UseDarkScrollbars(_log);
            card.Controls.Add(_log);
            return card;
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        private void WireEvents()
        {
            _tabs.SelectedIndexChanged += (_, _) => ShowPage(_tabs.SelectedIndex);
            _pauseButton.Click += (_, _) =>
            {
                // While a manual override is active this button ends it; otherwise it pauses/resumes automation
                if (_engine.OverrideMode != HdrOverride.None) _setOverride(HdrOverride.None, null);
                else _setPaused(!_engine.Paused);
                RefreshView();
            };
            _trayButton.Click += (_, _) => HideToTray();
            _search.TextChanged += (_, _) => RebuildGames();

            _games.HdrToggled += (_, item) =>
            {
                _engine.SetGameHdr(item.Processes, item.Hdr, item.Name);
                UpdateGamesCount();
            };
            _engine.LibraryChanged += OnLibraryChanged;
            _games.AnimationStats += (_, message) => _engine.Log("UI", message);   // measured scroll frame rate
            _swShowOthers.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.ShowOtherPrograms = _swShowOthers.Checked);
                RebuildGames();
            };
            _minimumHdr.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.MinimumHdrSeconds = _minimumHdr.Value); };
            _hotkeyChoice.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string mode = HotkeyModes[Math.Clamp(_hotkeyChoice.SelectedIndex, 0, HotkeyModes.Length - 1)];
                _engine.Update(c => c.HotkeyModifier = mode);
                _applyHotkeys();
            };
            foreach (var (_, sw) in _monitorSwitches)
                sw.CheckedChanged += (_, _) => OnMonitorToggled(sw);

            _swAutostart.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                if (!TrayAndStartupManager.SetAutostart(_swAutostart.Checked))
                {
                    MessageBox.Show(this, "Could not update the Windows startup entry.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _loadingSettings = true;
                    _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                    _loadingSettings = false;
                }
            };
            _swStartupScan.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.AutoScanDrivesOnStartup = _swStartupScan.Checked); };
            _swBrowserWindowed.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.EnableHdrForBrowserInWindowedMode = _swBrowserWindowed.Checked); };
            _swKeepBrowserHdr.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.KeepBrowserHdrWhenNotFocused = _swKeepBrowserHdr.Checked); };
            _swColorProfile.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.ReloadColorProfileAfterSdr = _swColorProfile.Checked); };
            _swRefreshApps.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.RefreshAppsAfterSdr = _swRefreshApps.Checked); };
            _fallback.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.SdrFallbackDelaySeconds = _fallback.Value); };

            _swVideoAuto.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.VideoHdrAutomation = _swVideoAuto.Checked);
                UpdateVideoDependents();
            };
            _videoMode.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string mode = VideoModes[Math.Clamp(_videoMode.SelectedIndex, 0, VideoModes.Length - 1)];
                _engine.Update(c => c.VideoHdrMode = mode);
            };
            _videoTrigger.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string trigger = VideoTriggerModes[Math.Clamp(_videoTrigger.SelectedIndex, 0, VideoTriggerModes.Length - 1)];
                _engine.Update(c => c.VideoHdrTrigger = trigger);
            };
            _swVideoBrowsers.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.VideoHdrInBrowsers = _swVideoBrowsers.Checked);
                UpdateVideoDependents();
            };
            _swVideoPlayers.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrInPlayers = _swVideoPlayers.Checked); };
            _videoRevert.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrRevertSeconds = _videoRevert.Value); };
            _swVideoBattery.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrDisableOnBattery = _swVideoBattery.Checked); };

            Resize += (_, _) =>
            {
                // Minimize = hide to tray (no taskbar button, not in Alt-Tab)
                if (WindowState == FormWindowState.Minimized)
                    BeginInvoke(new Action(HideToTray));
            };

            FormClosing += (_, e) =>
            {
                if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
        }

        // ------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------
        public void ShowFromTray()
        {
            if (!Visible)
            {
                LoadSettings();
                RebuildGames();
                Show();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false;   // reliably bring to front from a background process
            Activate();
            RefreshView();
        }

        public void HideToTray()
        {
            if (IsDisposed) return;
            WindowState = FormWindowState.Normal;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        public void AllowExit() => _reallyExit = true;

        // ------------------------------------------------------------------
        // Refresh (called every 500 ms by the tray context while the window is visible)
        // ------------------------------------------------------------------
        public void RefreshView()
        {
            var s = _engine.Status;

            // Visual state: 0 off, 1 on, 2 paused; +10 / +20 while a manual override (HDR / SDR) is active
            int state = (s.Paused && s.Override == HdrOverride.None ? 2 : s.HdrOn ? 1 : 0) + (int)s.Override * 10;
            if (state != _lastState)
            {
                _lastState = state;

                _statusCard.SetState(state == 2 ? HdrVisualState.Paused : s.HdrOn ? HdrVisualState.On : HdrVisualState.Off);
                _hdrLabel.Text = s.Override switch
                {
                    HdrOverride.ForceHdr => "HDR forced on",
                    HdrOverride.ForceSdr => "SDR forced",
                    _ => state == 2 ? (s.HdrOn ? "HDR on · paused" : "Automation paused")
                         : s.HdrOn ? "HDR is on" : "HDR is off"
                };

                if (s.Override != HdrOverride.None)
                {
                    _pauseButton.Text = "Back to automatic";
                    _pauseButton.Glyph = Theme.GlyphPlay;
                    _pauseButton.Primary = true;
                }
                else if (state == 2)
                {
                    _pauseButton.Text = "Resume automation";
                    _pauseButton.Glyph = Theme.GlyphPlay;
                    _pauseButton.Primary = true;
                }
                else
                {
                    _pauseButton.Text = "Pause automation";
                    _pauseButton.Glyph = Theme.GlyphPause;
                    _pauseButton.Primary = false;
                }
            }

            string detail;
            if (s.Override != HdrOverride.None)
            {
                string length = s.OverrideRemainingMs >= 0
                    ? $"{Math.Max(1, (int)Math.Ceiling(s.OverrideRemainingMs / 60000.0))} min left"
                    : "until you turn it back";
                string shortcut = HotkeySettings.Describe(_engine.Read(c => c.HotkeyModifier), 'O');
                detail = shortcut.Length > 0 ? $"Manual override · {length}  ({shortcut})" : $"Manual override · {length}";
            }
            else if (s.Paused)
            {
                detail = "HDR is not switched automatically until you resume.";
            }
            else if (s.WaitingForGame != null)
            {
                detail = $"{s.WaitingForGame} started · HDR in {s.WaitingSecondsLeft} s (game option)";
            }
            else if (s.RunningGame != null)
            {
                detail = $"HDR game running: {s.RunningGame}";
            }
            else if (s.Video != null)
            {
                detail = s.HoldSecondsLeft > 0 ? $"Video stopped · back to SDR in {s.HoldSecondsLeft} s" : $"Playing: {s.Video}";
            }
            else if (s.HoldSecondsLeft > 0)
            {
                detail = $"Staying in HDR for {s.HoldSecondsLeft} s more";
            }
            else
            {
                detail = string.IsNullOrEmpty(s.ActiveProcess)
                    ? "Waiting …"
                    : $"Active window: {s.ActiveProcess}{(s.IsFullscreen ? " (fullscreen)" : "")}";
            }
            if (_detailLabel.Text != detail) _detailLabel.Text = detail;

            bool scanning = _scanInFlight || _engine.IsScanning;
            if (_scanButton.Enabled == scanning)
            {
                _scanButton.Enabled = !scanning;
                _scanButton.Text = scanning ? "Scanning…" : "Scan games";
            }

            var lines = _engine.ReadLog(ref _logCursor);
            if (lines.Length > 0)
            {
                _log.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
                if (_log.TextLength > 200_000)
                    _log.Text = _log.Text[^100_000..];
            }
        }

        // ------------------------------------------------------------------
        // Games page
        // ------------------------------------------------------------------
        private void RebuildGames()
        {
            string filter = _search.Text.Trim();
            bool showOthers = _engine.Read(c => c.ShowOtherPrograms);
            var items = _engine.GetGameRows(showOthers)
                .Where(r => filter.Length == 0 ||
                            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(r => new GameItem(r))
                .ToArray();

            // Keeps the selection and scroll position
            _games.SetItems(items, _games.SelectedItem?.Key);
            UpdateGamesCount();
        }

        private void UpdateGamesCount()
        {
            if (IsDisposed) return;
            int enabled = _games.Items.Count(i => i.Hdr);
            _gamesCount.Text = $"{enabled} with HDR · {_games.Items.Count} shown";
        }

        private async void OnScan(object? sender, EventArgs e)
        {
            _scanInFlight = true;
            _scanButton.Enabled = false;
            _scanButton.Text = "Scanning…";
            try { await Task.Run(() => _engine.ScanDrives()); }
            finally
            {
                _scanInFlight = false;
                if (!IsDisposed)
                {
                    _scanButton.Text = "Scan games";
                    _scanButton.Enabled = true;
                    RebuildGames();
                }
            }
        }

        private void OnAddExe()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the game or program (.exe) that should use HDR",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = _engine.GetInitialDirectory() ?? string.Empty
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            foreach (var file in dlg.FileNames)
                _engine.AddGame(HdrEngine.CleanExeInput(file), fromHotkey: false, exePath: file);
            RebuildGames();
        }

        private void OnAddRunning()
        {
            var picked = PickRunningProcess();
            if (picked == null) return;
            _engine.AddGame(picked.Value.Name, fromHotkey: false, exePath: picked.Value.Path);
            RebuildGames();
        }

        private void OnExclude()
        {
            if (_games.SelectedItem is not GameItem item)
            {
                MessageBox.Show(this, "Select a game in the list first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _engine.IgnoreProcesses(item.Processes, item.Name);
            RebuildGames();
        }

        private void OnLibraryChanged(object? sender, EventArgs e)
        {
            // Raised on a background thread
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) RebuildGames(); })); } catch { }
        }

        /// <summary>Per-game options dialog: delay before HDR turns on, keep HDR on after the game closes.</summary>
        private void OnGameOptions()
        {
            if (_games.SelectedItem is not GameItem item)
            {
                MessageBox.Show(this, "Select a game in the list first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var current = _engine.GetGameOptions(item.Processes);
            var delay = new Stepper { Minimum = 0, Maximum = 120, Step = 5, ZeroText = "Off" };
            var keep = new Stepper { Minimum = 0, Maximum = 600, Step = 15, ZeroText = "Off" };
            delay.Value = current.HdrDelaySeconds;
            keep.Value = current.KeepHdrAfterExitSeconds;

            using var dlg = new Form
            {
                Text = $"Game options – {item.Name}",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body,
                Icon = _windowIcon, KeyPreview = true,
                Padding = new Padding(16)
            };
            dlg.HandleCreated += (_, _) => Theme.StyleWindow(dlg.Handle);
            dlg.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter && dlg.ActiveControl is not ModernButton)
                {
                    dlg.DialogResult = DialogResult.OK;
                    e.Handled = e.SuppressKeyPress = true;
                }
            };

            dlg.SuspendLayout();
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));   // hint text for the delay (wraps; scales with DPI)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            table.Controls.Add(new Label
            {
                Text = item.Name, Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(2, 0, 0, 8)
            }, 0, 0);
            table.Controls.Add(SettingRow("Delay before HDR turns on",
                "For games that flicker or crash when HDR changes during launch.", delay), 0, 1);
            table.Controls.Add(new Label
            {
                Text = "Off (0 s) is best for most games, especially ones that check HDR the moment they start " +
                       "(Unreal Engine, RE Engine, Windows Auto HDR, NVIDIA RTX HDR). " +
                       "Use 3–5 s only if a game crashes or flickers while the display switches to HDR.",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft, Margin = new Padding(18, 0, 18, 8)
            }, 0, 2);
            table.Controls.Add(SettingRow("Keep HDR on after the game closes",
                "Avoids a second black screen when you restart the game quickly.", keep), 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            var save = new ModernButton { Text = "Save", Primary = true, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            var cancel = new ModernButton { Text = "Cancel", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            var reset = new ModernButton { Text = "Reset", AutoSize = true, Margin = new Padding(0) };
            save.Click += (_, _) => dlg.DialogResult = DialogResult.OK;
            cancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            reset.Click += (_, _) => { delay.Value = 0; keep.Value = 0; };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(reset);
            table.Controls.Add(buttons, 0, 4);

            dlg.Controls.Add(table);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(620, 328);
            dlg.ResumeLayout(false);

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _engine.SetGameOptions(item.Processes,
                new GameOptions { HdrDelaySeconds = delay.Value, KeepHdrAfterExitSeconds = keep.Value }, item.Name);
            RebuildGames();
        }

        // ---- monitors ----
        private void OnMonitorToggled(ToggleSwitch changed)
        {
            if (_loadingSettings) return;

            var selected = _monitorSwitches.Where(m => m.Switch.Checked && m.Monitor.HdrSupported).Select(m => m.Monitor.DevicePath).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "At least one HDR-capable monitor has to stay selected.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _loadingSettings = true;
                changed.Checked = true;
                _loadingSettings = false;
                return;
            }

            var connected = new HashSet<string>(_monitorSwitches.Select(m => m.Monitor.DevicePath), StringComparer.OrdinalIgnoreCase);
            _engine.Update(c =>
            {
                // keep selections of monitors that are not connected right now
                var keepDisconnected = (c.SelectedMonitors ?? new List<string>()).Where(p => !connected.Contains(p));
                c.SelectedMonitors = keepDisconnected.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private (string Name, string? Path)? PickRunningProcess()
        {
            var rows = new List<(string Name, string Title, string? Path)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    {
                        string? path = ProcessPaths.TryGetExePath(p.Id);   // limited access only, never reads process memory
                        rows.Add((p.ProcessName.ToLowerInvariant(), p.MainWindowTitle, path));
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            rows = rows.GroupBy(r => r.Name).Select(g => g.First()).OrderBy(r => r.Name).ToList();

            using var dlg = new Form
            {
                Text = "Add a running program", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                Padding = new Padding(12)
            };
            dlg.SuspendLayout();
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var r in rows)
            {
                string t = r.Title.Length > 60 ? r.Title[..57] + "..." : r.Title;
                list.Items.Add($"{r.Name}   —   {t}");
            }
            var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dlg.DialogResult = DialogResult.OK; };
            dlg.Controls.Add(list);
            dlg.Controls.Add(bottom);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(560, 420);
            dlg.ResumeLayout(false);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            dlg.Icon = _windowIcon;
            Theme.ApplyForm(dlg);

            if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return null;
            return (rows[list.SelectedIndex].Name, rows[list.SelectedIndex].Path);
        }

        // ------------------------------------------------------------------
        // Settings page
        // ------------------------------------------------------------------
        private void LoadSettings()
        {
            _loadingSettings = true;
            try
            {
                _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                _swStartupScan.Checked = _engine.Read(c => c.AutoScanDrivesOnStartup);
                _swShowOthers.Checked = _engine.Read(c => c.ShowOtherPrograms);
                _minimumHdr.Value = _engine.Read(c => c.MinimumHdrSeconds);
                _hotkeyChoice.SelectedIndex = Math.Max(0, Array.IndexOf(HotkeyModes, _engine.Read(c => HotkeySettings.Normalize(c.HotkeyModifier))));

                // Monitors: explicit selection if set, otherwise "primary only" / "all HDR monitors"
                var selection = _engine.Read(c => (c.SelectedMonitors ?? new List<string>()).ToList());
                bool onlyPrimary = _engine.Read(c => c.OnlyPrimaryMonitor);
                foreach (var (monitor, sw) in _monitorSwitches)
                {
                    sw.Checked = monitor.HdrSupported && (selection.Count > 0
                        ? selection.Contains(monitor.DevicePath, StringComparer.OrdinalIgnoreCase)
                        : !onlyPrimary || monitor.IsPrimary);
                }
                _swBrowserWindowed.Checked = _engine.Read(c => c.EnableHdrForBrowserInWindowedMode);
                _swKeepBrowserHdr.Checked = _engine.Read(c => c.KeepBrowserHdrWhenNotFocused);
                _swColorProfile.Checked = _engine.Read(c => c.ReloadColorProfileAfterSdr);
                _swRefreshApps.Checked = _engine.Read(c => c.RefreshAppsAfterSdr);
                _fallback.Value = _engine.Read(c => c.SdrFallbackDelaySeconds);

                _swVideoAuto.Checked = _engine.Read(c => c.VideoHdrAutomation);
                _videoMode.SelectedIndex = Math.Max(0, Array.IndexOf(VideoModes, _engine.Read(c => VideoHdrModes.Normalize(c.VideoHdrMode))));
                _videoTrigger.SelectedIndex = Math.Max(0, Array.IndexOf(VideoTriggerModes, _engine.Read(c => VideoTriggers.Normalize(c.VideoHdrTrigger))));
                _swVideoBrowsers.Checked = _engine.Read(c => c.VideoHdrInBrowsers);
                _swVideoPlayers.Checked = _engine.Read(c => c.VideoHdrInPlayers);
                _videoRevert.Value = _engine.Read(c => c.VideoHdrRevertSeconds);
                _swVideoBattery.Checked = _engine.Read(c => c.VideoHdrDisableOnBattery);
                UpdateVideoDependents();
            }
            finally { _loadingSettings = false; }
        }

        /// <summary>With video automation for browsers on, the two older YouTube switches no longer apply.</summary>
        private void UpdateVideoDependents()
        {
            bool videoHandlesBrowsers = _swVideoAuto.Checked && _swVideoBrowsers.Checked;
            _swBrowserWindowed.Enabled = !videoHandlesBrowsers;
            _swKeepBrowserHdr.Enabled = !videoHandlesBrowsers;
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) { MessageBox.Show(this, "File does not exist yet:\n" + path, Text); return; }
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
