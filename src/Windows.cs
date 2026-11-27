// The app's windows: main window, settings, app picker, a small text prompt, and the on-screen switch message.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TVMode
{
    // ------------------------------------------------------------------ Main window
    class MainForm : ThemedForm
    {
        const int HOTKEY_TOGGLE = 1;
        const float W = 640, H = 684;

        bool allowShow, exiting, loading, trayHintShown, hotkeyRegistered;
        Mode currentMode = Mode.Unknown;

        // controls (recreated on every BuildUi)
        Pill pill;
        FlatButton btnPc, btnTv;
        ProfileUi pcUi, tvUi;
        Label lblStatus;

        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem trayPc, trayTv;
        Icon appIcon, trayIcon;
        System.Windows.Forms.Timer refreshTimer, displayChangeTimer, statusTimer;
        SettingsDialog settingsDialog;
        bool setupOpen;
        Thread appWorker;
        volatile int switchVersion;

        class ProfileUi
        {
            public Mode Mode;
            public Card Box;
            public CheckList Screens;
            public Label Layout;
            public FlatButton Forget, Save;
            public DarkCombo Audio;
        }

        public MainForm(bool startHidden) : base(Native.PrimaryDpi())
        {
            allowShow = !startHidden;
            Text = Program.AppName;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            appIcon = LoadAppIcon(0);
            trayIcon = LoadAppIcon(SystemInformation.SmallIconSize.Width);
            Icon = appIcon;

            BuildTray();

            refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            refreshTimer.Tick += delegate { refreshTimer.Stop(); RefreshAll(); };
            // Plugging in or turning on a screen: refresh the lists once things settle.
            displayChangeTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            displayChangeTimer.Tick += delegate { displayChangeTimer.Stop(); RefreshAll(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            statusTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            statusTimer.Tick += delegate { statusTimer.Stop(); if (lblStatus != null) lblStatus.Text = ""; };

            // Keep the "Start with Windows" entry pointing at this exe if it was moved.
            try { if (Settings.StartWithWindows) Settings.StartWithWindows = true; } catch { }

            BuildUi();
        }

        static Icon LoadAppIcon(int size)
        {
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("TVMode.app.ico"))
                    if (s != null) return size > 0 ? new Icon(s, size, size) : new Icon(s);
            }
            catch { }
            return SystemIcons.Application;
        }

        // ---------------- layout

        protected override void BuildUi()
        {
            loading = true;
            string keepStatus = lblStatus != null ? lblStatus.Text : "";
            ClearUi();
            ClientSize = new Size(Sc.S(W), Sc.S(H));

            // Header
            AddLabel(this, Program.AppName, 20, 16, 184, 32, Sc.Font(21, true), Theme.Fg, ContentAlignment.MiddleLeft);
            pill = Place(this, new Pill { Font = Sc.Font(12, true) }, 210, 22, 100, 22);
            var btnSettings = IconButton(Theme.GlyphSettings, "Settings", W - 20 - 36);
            btnSettings.Click += delegate { OpenSettings(); };
            var btnRefresh = IconButton(Theme.GlyphRefresh, "Refresh", W - 20 - 36 - 42);
            btnRefresh.Click += delegate { RefreshAll(); SetStatus("Refreshed.", false); };

            // Mode buttons
            float colW = (W - 40 - 12) / 2;
            btnPc = ModeButton("PC mode", Theme.GlyphPc, 20, colW);
            btnTv = ModeButton("TV mode", Theme.GlyphTv, 20 + colW + 12, colW);
            btnPc.Click += delegate { DoSwitch(Mode.PC); };
            btnTv.Click += delegate { DoSwitch(Mode.TV); };

            // Profiles
            pcUi = BuildProfile(Mode.PC, "PC profile", 20, colW);
            tvUi = BuildProfile(Mode.TV, "TV profile", 20 + colW + 12, colW);

            // TV apps (the card grows with the number of apps, and so does the window)
            float appsH = BuildAppCard(20, 490, W - 40);

            // Status line
            float statusY = 490 + appsH + 8;
            lblStatus = AddLabel(this, keepStatus, 22, statusY, W - 44, 30, Sc.Font(12.5f), Theme.Muted, ContentAlignment.MiddleLeft);
            ClientSize = new Size(Sc.S(W), Sc.S(statusY + 38));
            KeepOnScreen();

            loading = false;
            RefreshAll();
            ActiveControl = btnPc;
        }

        FlatButton IconButton(string glyph, string tip, float x)
        {
            var b = Place(this, new FlatButton { Style = FlatButton.Kind.Icon, Glyph = glyph, GlyphFont = Sc.Icons(16), Text = tip, AccessibleName = tip }, x, 14, 36, 36);
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        FlatButton ModeButton(string title, string glyph, float x, float w)
        {
            return Place(this, new FlatButton
            {
                Style = FlatButton.Kind.Mode, Text = title, Font = Sc.Font(16, true), Glyph = glyph, GlyphFont = Sc.Icons(26),
                SubtitleFont = Sc.Font(12), Radius = 12
            }, x, 62, w, 78);
        }

        ProfileUi BuildProfile(Mode mode, string title, float x, float w)
        {
            var ui = new ProfileUi { Mode = mode };
            ui.Box = Place(this, new Card { Radius = 12 * Sc.F }, x, 152, w, 326);
            float iw = w - 32;

            AddLabel(ui.Box, title, 16, 14, iw, 24, Sc.Font(15, true), Theme.Fg);

            Caption(ui.Box, "Screens to turn on", 16, 48, iw);
            ui.Screens = Place(ui.Box, new CheckList { Font = Sc.Font(13), DetailFont = Sc.Font(11.5f, true), RowHeight = Sc.S(32) }, 16, 68, iw, 96);
            ui.Screens.RowToggled += delegate { SaveScreens(ui); };

            Caption(ui.Box, "Layout", 16, 176, 120);
            ui.Forget = Place(ui.Box, new FlatButton { Style = FlatButton.Kind.Ghost, Text = "Forget", Font = Sc.Font(12), TextColor = Theme.Muted, Radius = 6 },
                w - 16 - 64, 171, 64, 24);
            ui.Forget.Click += delegate { ForgetLayout(ui); };
            ui.Layout = AddLabel(ui.Box, "", 16, 196, iw, 34, Sc.Font(12.5f), Theme.Fg);
            ui.Save = Place(ui.Box, new FlatButton { Text = "Save current setup", Glyph = Theme.GlyphSave, GlyphFont = Sc.Icons(13), Font = Sc.Font(12.5f, true) },
                16, 232, iw, 30);
            ui.Save.Click += delegate { SaveCurrentLayout(ui); };
            new ToolTip().SetToolTip(ui.Save, "Remembers which screens are on, their resolution, refresh rate, position and main screen");

            Caption(ui.Box, "Sound", 16, 274, iw);
            ui.Audio = Place(ui.Box, new DarkCombo { Font = Sc.Font(13), ItemHeight = Sc.S(24) }, 16, 292, iw, 30);
            ui.Audio.SelectedIndexChanged += delegate { OnAudioChanged(ui); };
            return ui;
        }

        const int MaxVisibleApps = 3;
        const float AppRowH = 48;

        /// <summary>"Apps to open in TV mode": one row per app with its own close switch. Returns the card height.</summary>
        float BuildAppCard(float x, float y, float w)
        {
            var apps = Settings.TvApps;
            const float headH = 48, footH = 34;
            var workArea = (IsHandleCreated ? Screen.FromHandle(Handle) : Screen.PrimaryScreen).WorkingArea;
            float room = (workArea.Height - (Height - ClientSize.Height)) / Sc.F - y - headH - footH - 46;
            int visible = Math.Max(1, Math.Min(Math.Min(apps.Count, MaxVisibleApps), (int)(room / AppRowH)));
            float cardH = headH + visible * AppRowH + footH;
            var card = Place(this, new Card { Radius = 12 * Sc.F }, x, y, w, cardH);
            float iw = w - 32;

            AddLabel(card, "Apps to open in TV mode", 16, 16, iw - 140, 24, Sc.Font(15, true), Theme.Fg);
            var add = Place(card, new FlatButton { Text = "Add app", Glyph = "", GlyphFont = Sc.Icons(12), Font = Sc.Font(12.5f, true) },
                w - 16 - 116, 12, 116, 32);
            add.Click += delegate { ShowAddMenu(add); };

            bool scroll = apps.Count > MaxVisibleApps;
            var rows = Place(card, new Card { Fill = Theme.Card, Border = Theme.Card, BackColor = Theme.Card, Radius = 0, AutoScroll = scroll },
                8, headH, w - 16, visible * AppRowH);
            if (scroll) Native.UseDarkControlTheme(rows, false);

            if (apps.Count == 0)
            {
                AddLabel(rows, "No apps yet. Add one, like Steam Big Picture or a media player.", 8, 0, w - 32, AppRowH,
                    Sc.Font(13), Theme.Muted, ContentAlignment.MiddleLeft);
            }
            float rowW = w - 16 - (scroll ? SystemInformation.VerticalScrollBarWidth / Sc.F : 0);
            for (int i = 0; i < apps.Count; i++) BuildAppRow(rows, apps[i], i * AppRowH, rowW);

            AddLabel(card, "Apps open a moment after switching to TV mode. Only copies TV mode opened are ever closed.",
                16, headH + visible * AppRowH + 8, iw, 20, Sc.Font(12), Theme.Muted);
            return cardH;
        }

        void BuildAppRow(Card rows, TvApp app, float top, float w)
        {
            string name = AppLauncher.NameOf(app);
            float rw = w - 16;
            var row = Place(rows, new Card { Fill = Theme.Inset, BackColor = Theme.Card, Radius = 8 * Sc.F }, 8, top + 4, rw, AppRowH - 8);

            // On/off: whether this app opens automatically in TV mode (it stays in the list either way).
            var enabled = Place(row, new Toggle { Surface = Theme.Inset, Text = "", AccessibleName = "Open " + name + " in TV mode" },
                10, 7, 44, 26);
            enabled.SetQuietly(app.OpenInTv);
            new ToolTip().SetToolTip(enabled, "Open " + name + " automatically in TV mode");

            const float textX = 100;
            var icon = AppCatalog.IconFor(app.Path, Sc.S(24));
            var pic = Place(row, new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Inset }, 64, 8, 24, 24);
            row.Disposed += delegate { if (icon != null) icon.Dispose(); };

            bool canClose = AppLauncher.CanClose(app.Path);
            const float toggleW = 164, btnW = 30;
            float buttonsW = btnW * 2 + 4;
            float textW = rw - textX - toggleW - buttonsW - 24;
            var title = AddLabel(row, name, textX, 2, textW, 20, Sc.Font(13.5f, true), Theme.Fg);
            var hint = AddLabel(row, "", textX, 21, textW, 18, Sc.Font(11.5f), Theme.Muted);

            Image faded = null;
            row.Disposed += delegate { if (faded != null) faded.Dispose(); };
            Action showState = delegate
            {
                title.ForeColor = app.OpenInTv ? Theme.Fg : Theme.Muted;
                hint.Text = app.OpenInTv ? HintFor(app, canClose) : "Off - won't open in TV mode";
                if (icon != null && !app.OpenInTv && faded == null) faded = Faded(icon);
                pic.Image = app.OpenInTv ? icon : faded;
            };
            showState();
            enabled.CheckedChanged += delegate
            {
                app.OpenInTv = enabled.Checked;
                SaveQuietly();
                showState();
                SetStatus(app.OpenInTv ? name + " will open in TV mode." : name + " won't open in TV mode. It stays in the list - switch it back on any time.", false);
            };

            var close = Place(row, new Toggle { Font = Sc.Font(12.5f), ForeColor = Theme.Fg, Surface = Theme.Inset },
                rw - 12 - buttonsW - 8 - toggleW, 7, toggleW, 26);
            if (canClose)
            {
                close.Text = "Close in PC mode";
                close.SetQuietly(app.CloseOnPc);
                close.CheckedChanged += delegate
                {
                    app.CloseOnPc = close.Checked;
                    SaveQuietly();
                    SetStatus(close.Checked ? name + " will be closed when going back to PC mode." : name + " will stay open in PC mode.", false);
                };
                new ToolTip().SetToolTip(close, AppLauncher.IsSteam(app.Path)
                    ? "Leaves Big Picture when going back to PC mode, and quits Steam if TV mode started it"
                    : "Closes " + name + " when going back to PC mode (only if TV mode opened it)");
            }
            else
            {
                close.Text = "Stays open";
                close.SetQuietly(false);
                close.Enabled = false;
                new ToolTip().SetToolTip(close, "Store apps and web links can't be closed automatically");
            }

            var options = Place(row, new FlatButton { Style = FlatButton.Kind.Icon, Glyph = "", GlyphFont = Sc.Icons(13), Text = "Start options" },
                rw - 12 - btnW * 2 - 4, 5, btnW, 30);
            options.Click += delegate { EditAppOptions(app); };
            new ToolTip().SetToolTip(options, "Start options");

            var remove = Place(row, new FlatButton { Style = FlatButton.Kind.Icon, Glyph = "", GlyphFont = Sc.Icons(12), Text = "Remove " + name },
                rw - 12 - btnW, 5, btnW, 30);
            remove.Click += delegate { BeginInvoke((MethodInvoker)delegate { RemoveApp(app); }); };
            new ToolTip().SetToolTip(remove, "Remove from the list");
        }

        /// <summary>Grey, see-through copy of an icon, for apps that are switched off.</summary>
        static Image Faded(Image source)
        {
            var bmp = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var m = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.45f, 0 },
                new[] { 0f, 0, 0, 0, 1 }
            });
            using (var g = Graphics.FromImage(bmp))
            using (var attrs = new System.Drawing.Imaging.ImageAttributes())
            {
                attrs.SetColorMatrix(m);
                g.DrawImage(source, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
            }
            return bmp;
        }

        static string HintFor(TvApp app, bool canClose)
        {
            if (AppLauncher.IsSteam(app.Path)) return "Quits Steam only if TV mode started it";
            if (!canClose)
                return app.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "Store app - can't be closed automatically" : "Link - can't be closed automatically";
            return string.IsNullOrEmpty(app.Args) ? "Desktop app" : "Start options: " + app.Args;
        }

        void ShowAddMenu(Control anchor)
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Sc.Font(13), ImageScalingSize = new Size(Sc.S(18), Sc.S(18)) };
            int added = 0;
            foreach (var preset in AppCatalog.Presets(Sc.S(18)))
            {
                if (Settings.TvApps.Any(a => a.Key == preset.Path.ToLowerInvariant())) continue;
                var c = preset;
                var item = new ToolStripMenuItem(c.Name, c.Image, delegate { BeginInvoke((MethodInvoker)delegate { AddApp(c); }); });
                item.Padding = new Padding(Sc.S(2));
                menu.Items.Add(item);
                added++;
            }
            if (added > 0) menu.Items.Add(new ToolStripSeparator());
            var other = new ToolStripMenuItem("Choose another app...", null, delegate { BeginInvoke((MethodInvoker)delegate { PickApp(); }); });
            other.Padding = new Padding(Sc.S(2));
            menu.Items.Add(other);
            menu.Closed += delegate { BeginInvoke((MethodInvoker)delegate { menu.Dispose(); }); };
            menu.Show(anchor, new Point(anchor.Width - menu.PreferredSize.Width, anchor.Height + Sc.S(4)));
        }

        void PickApp()
        {
            AppChoice picked = null;
            using (var picker = new AppPicker(UiNative.DpiOf(Handle)))
                if (picker.ShowDialog(this) == DialogResult.OK) picked = picker.Result;
            if (picked != null) AddApp(picked);
        }

        void AddApp(AppChoice c)
        {
            if (Settings.TvApps.Any(a => a.Key == c.Path.Trim().ToLowerInvariant()))
            {
                SetStatus(c.Name + " is already in the list.", false);
                return;
            }
            Settings.TvApps.Add(new TvApp { Name = c.Name, Path = c.Path, Args = c.Args ?? "", CloseOnPc = AppLauncher.CanClose(c.Path) });
            SaveQuietly();
            BuildUi();
            SetStatus("TV mode will open " + c.Name + ".", false);
        }

        void RemoveApp(TvApp app)
        {
            Settings.TvApps.Remove(app);
            SaveQuietly();
            BuildUi();
            SetStatus("Removed " + AppLauncher.NameOf(app) + ".", false);
        }

        void EditAppOptions(TvApp app)
        {
            using (var dialog = new AppStartOptions(UiNative.DpiOf(Handle), app))
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            SaveQuietly();
            BuildUi();
        }

        /// <summary>After the window grows (more apps), keep it fully on its screen.</summary>
        void KeepOnScreen()
        {
            if (!IsHandleCreated || !Visible) return;
            var area = Screen.FromHandle(Handle).WorkingArea;
            int top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            if (top != Top) Top = top;
        }

        // ---------------- tray

        void BuildTray()
        {
            trayMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = true };
            float f = Native.PrimaryDpi() / 96f;
            trayMenu.Font = new Font("Segoe UI", 13 * f, GraphicsUnit.Pixel);
            trayPc = new ToolStripMenuItem("PC mode", null, delegate { DoSwitch(Mode.PC); });
            trayTv = new ToolStripMenuItem("TV mode", null, delegate { DoSwitch(Mode.TV); });
            trayMenu.Items.Add(trayPc);
            trayMenu.Items.Add(trayTv);
            trayMenu.Items.Add("TV mode without apps", null, delegate { DoSwitch(Mode.TV, false); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Open " + Program.AppName, null, delegate { ShowWindow(); });
            trayMenu.Items.Add("Settings...", null, delegate { ShowWindow(); OpenSettings(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            foreach (ToolStripItem it in trayMenu.Items) it.Padding = new Padding((int)(4 * f));
            trayMenu.Opening += delegate
            {
                trayPc.Checked = currentMode == Mode.PC;
                trayTv.Checked = currentMode == Mode.TV;
            };

            tray = new NotifyIcon { Icon = trayIcon, Text = Program.AppName, Visible = true, ContextMenuStrip = trayMenu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        }

        // ---------------- window plumbing

        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!RegisterToggleHotkey())
                SetStatus("The hotkey " + Settings.HotkeyText + " is already used by another app. Pick another one in Settings.", true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Settings.SetupPending) BeginInvoke((MethodInvoker)OpenSetup);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_TOGGLE)
            {
                Mode target = Mode.TV;
                try { target = Modes.Opposite(); } catch { }
                DoSwitch(target);
            }
            else if (m.Msg == Program.ShowWindowMessage && Program.ShowWindowMessage != 0)
            {
                ShowWindow();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RefreshAll(); e.Handled = true; }
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } // hides to tray
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, Program.AppName + " is still running",
                        "Press " + Settings.HotkeyText + " any time to switch between PC and TV mode.", ToolTipIcon.Info);
                }
                return;
            }
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            UnregisterToggleHotkey();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try { BeginInvoke((MethodInvoker)delegate { displayChangeTimer.Stop(); displayChangeTimer.Start(); }); } catch { }
        }

        void ShowWindow()
        {
            allowShow = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            RefreshAll();
        }

        // ---------------- hotkey (also used by the settings dialog)

        public bool RegisterToggleHotkey()
        {
            UnregisterToggleHotkey();
            hotkeyRegistered = Native.RegisterHotKey(Handle, HOTKEY_TOGGLE, Settings.HotkeyMods | Native.MOD_NOREPEAT, Settings.HotkeyKey);
            return hotkeyRegistered;
        }

        public void UnregisterToggleHotkey()
        {
            if (hotkeyRegistered) Native.UnregisterHotKey(Handle, HOTKEY_TOGGLE);
            hotkeyRegistered = false;
        }

        /// <summary>Tries a new hotkey; keeps the old one if the new one is taken. Returns an error message or null.</summary>
        public string TryChangeHotkey(uint mods, Keys key)
        {
            bool isFKey = key >= Keys.F1 && key <= Keys.F24;
            if ((mods & (Native.MOD_CONTROL | Native.MOD_ALT)) == 0 && !isFKey)
                return "Use Ctrl and/or Alt together with a key (an F-key on its own also works).";
            if (!Native.RegisterHotKey(Handle, 99, mods | Native.MOD_NOREPEAT, (uint)key))
                return "That combination is already used by another app. Try another one.";
            Native.UnregisterHotKey(Handle, 99);
            Settings.HotkeyMods = mods;
            Settings.HotkeyKey = (uint)key;
            SaveQuietly();
            RegisterToggleHotkey();
            UpdateModeIndicators(null);
            return null;
        }

        void OpenSettings()
        {
            if (settingsDialog != null && !settingsDialog.IsDisposed) { settingsDialog.Activate(); return; }
            settingsDialog = new SettingsDialog(this, UiNative.DpiOf(Handle));
            settingsDialog.FormClosed += delegate { settingsDialog = null; RegisterToggleHotkey(); UpdateModeIndicators(null); };
            settingsDialog.Show(this);
        }

        public void OpenSetup()
        {
            if (setupOpen) return;
            if ((appWorker != null && appWorker.IsAlive) || !SetupSession.TryEnter())
            {
                SetStatus("Wait for the current mode switch to finish before opening setup.", true);
                return;
            }
            setupOpen = true;
            try
            {
                using (var wizard = new SetupWizard(UiNative.DpiOf(Handle)))
                {
                    wizard.ShowDialog(this);
                    if (Settings.SetupPending)
                    {
                        Settings.SetupPending = false;
                        SaveQuietly();
                    }
                    RefreshAll();
                    if (!string.IsNullOrEmpty(wizard.ResultMessage))
                        SetStatus(wizard.ResultMessage, wizard.ResultError);
                }
            }
            finally
            {
                setupOpen = false;
                SetupSession.Exit();
            }
        }

        // ---------------- switching

        void DoSwitch(Mode mode, bool launchApps = true)
        {
            if (setupOpen) return;
            if (!SetupSession.TryEnter())
            {
                SetStatus("Another mode switch is in progress.", true);
                return;
            }
            string title = Modes.Name(mode) + " MODE";
            try
            {
                SetStatus("Switching to " + Modes.Name(mode) + " mode...", false);
                lblStatus.Update();
                string refreshNote;
                bool usedLayout = Modes.Switch(mode, out refreshNote);
                switchVersion++;
                string prefix = "Switched to " + Modes.Name(mode) + " mode" + (usedLayout ? " (saved layout)" : "") + ". "
                    + (refreshNote != null ? refreshNote + ". " : "");
                SetStatus(prefix, false);

                var p = Settings.For(mode);
                bool audio = !string.IsNullOrEmpty(p.AudioId);
                var appsToOpen = Settings.TvApps.Where(a => a.OpenInTv).ToList();
                bool openApps = mode == Mode.TV && launchApps && appsToOpen.Count > 0;
                bool closeApps = mode == Mode.PC && Settings.TvApps.Any(a => a.CloseOnPc);

                // Screens go blank for a moment during the switch; show the message once they're back.
                ShowOsdSoon(mode, title, usedLayout ? "Saved layout restored" : "");
                if (!launchApps) SetOsdLine(1, "App launches skipped", false);
                if (audio)
                {
                    SetOsdLine(0, "Sound: " + p.AudioName + "...", false);
                    SwitchAudioInBackground(p.AudioId, p.AudioName, prefix, true);
                }
                if (openApps)
                {
                    SetOsdLine(1, "Opening " + (appsToOpen.Count == 1 ? AppLauncher.NameOf(appsToOpen[0]) : appsToOpen.Count + " apps") + "...", false);
                    RunAppsInBackground(true);
                }
                else if (closeApps) RunAppsInBackground(false);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
                Osd.ShowMessage(Mode.Unknown, "Couldn't switch", ex.Message, true);
            }
            finally { SetupSession.Exit(); }
            RefreshAll();
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        // The message waits ~1s for the screens to come back; results arriving meanwhile are folded into it.
        // Below the title it has up to two lines: [0] sound, [1] TV app. With neither, it shows osdBase.
        System.Windows.Forms.Timer osdDelay;
        string osdTitle, osdBase;
        Mode osdMode;
        readonly string[] osdLines = new string[2];
        bool osdError;

        string OsdDetail()
        {
            var lines = osdLines.Where(l => !string.IsNullOrEmpty(l)).ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : osdBase;
        }

        void ShowOsdSoon(Mode mode, string title, string baseDetail)
        {
            if (osdDelay != null) { osdDelay.Stop(); osdDelay.Dispose(); }
            osdMode = mode;
            osdTitle = title;
            osdBase = baseDetail;
            osdLines[0] = osdLines[1] = null;
            osdError = false;
            osdDelay = new System.Windows.Forms.Timer { Interval = 900 };
            osdDelay.Tick += delegate
            {
                osdDelay.Stop();
                osdDelay.Dispose();
                osdDelay = null;
                Osd.ShowMessage(osdMode, osdTitle, OsdDetail(), osdError);
            };
            osdDelay.Start();
        }

        /// <summary>Sets one line of the message, whether it is still waiting to appear or already up. False if it already closed.</summary>
        bool SetOsdLine(int index, string text, bool error)
        {
            osdLines[index] = text;
            osdError |= error;
            if (osdDelay != null) return true;
            return Osd.UpdateDetail(OsdDetail(), osdError);
        }

        void SwitchAudioInBackground(string id, string name, string prefix, bool fromSwitch)
        {
            SetStatus(prefix + "Waiting for sound output: " + name + "...", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                bool ok;
                try { ok = AudioManager.SetDefault(id, 10000, out err); }
                catch (Exception ex) { ok = false; err = ex.Message; }
                if (err != null && !ok) err = err + " (" + name + ")";
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (ok)
                        {
                            SetStatus(prefix + "Sound: " + name, false);
                            if (fromSwitch) SetOsdLine(0, "Sound: " + name, false);
                        }
                        else
                        {
                            SetStatus(prefix + err, true);
                            if (!fromSwitch || !SetOsdLine(0, err, true))
                                Osd.ShowMessage(Mode.Unknown, "Sound not switched", err, true);
                        }
                        RefreshAudio();
                    });
                }
                catch { } // window closed meanwhile
            });
        }

        /// <summary>Opens (TV) or closes (PC) all TV apps at the same time, off the UI thread, then reports on screen.</summary>
        void RunAppsInBackground(bool open)
        {
            int version = switchVersion;
            var apps = Settings.TvApps.Select(a => new TvApp
            {
                Name = a.Name, Path = a.Path, Args = a.Args, CloseOnPc = a.CloseOnPc, OpenInTv = a.OpenInTv,
                WaitForPrimary = a.WaitForPrimary, WaitForAudio = a.WaitForAudio
            }).ToList();
            var tvKeys = Settings.Tv.Keys;
            string audioId = Settings.Tv.AudioId;
            var previous = appWorker;
            var results = new List<AppLauncher.Result>();
            appWorker = new Thread(delegate ()
            {
                if (previous != null) previous.Join();
                if (open && version != switchVersion) return;
                if (open) Thread.Sleep(1200); // let the TV finish switching so the apps open there
                AppLauncher.RunAll(open, apps, tvKeys, audioId, r =>
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (version == switchVersion) OnAppResult(open, results, r);
                        });
                    }
                    catch { } // window closed meanwhile
                }, () => open && version != switchVersion);
            });
            appWorker.IsBackground = true;
            appWorker.Start();
        }

        void OnAppResult(bool open, List<AppLauncher.Result> results, AppLauncher.Result r)
        {
            results.Add(r);
            AppendStatus(r.Message + ".", r.Error);

            // One short line on screen for all apps: the first problem, or what was opened/closed.
            var errors = results.Where(x => x.Error).ToList();
            string line;
            if (errors.Count > 0)
                line = errors[0].Message + (results.Count > 1 ? "  (+" + (results.Count - 1) + " more)" : "");
            else if (results.Count == 1)
                line = r.Message;
            else
                line = (open ? "TV apps processed: " : "Apps closed: ") + results.Count;
            if (!SetOsdLine(1, line, errors.Count > 0) && r.Error)
                Osd.ShowMessage(Mode.Unknown, open ? "App not opened" : "App still open", r.Message, true);
        }

        void SetStatus(string text, bool error)
        {
            if (lblStatus == null) return;
            lblStatus.Text = text;
            lblStatus.ForeColor = error ? Theme.Danger : Theme.Muted;
            statusTimer.Stop();
            if (!error && text.Length > 0) statusTimer.Start(); // successes fade away, errors stay
        }

        void AppendStatus(string text, bool error)
        {
            if (lblStatus == null) return;
            SetStatus((lblStatus.Text.TrimEnd() + " " + text).Trim(), error || lblStatus.ForeColor == Theme.Danger);
        }

        void SaveQuietly()
        {
            try { Settings.Save(); }
            catch (Exception ex) { SetStatus("Could not save settings: " + ex.Message, true); }
        }

        // ---------------- profile editing

        void SaveScreens(ProfileUi ui)
        {
            var p = Settings.For(ui.Mode);
            p.Screens = ui.Screens.Rows.Where(r => r.Checked).Select(r => new ScreenRef { Key = r.Key, Name = r.SaveName }).ToList();
            // A saved layout only makes sense for the exact screens it was saved with.
            bool layoutDropped = p.Layout != null && !p.Layout.Keys.SetEquals(p.Keys);
            if (layoutDropped) p.Layout = null;
            SaveQuietly();

            if (p.Screens.Count == 0)
                SetStatus("The " + Modes.Name(ui.Mode) + " profile has no screens - tick at least one.", true);
            else
                SetStatus(Modes.Name(ui.Mode) + " profile: " + string.Join(", ", p.Screens.Select(s => ShortName(s.Name)))
                    + (layoutDropped ? ". The saved layout was cleared because the screens changed." : ""), false);
            UpdateLayoutLabel(ui);
            UpdateModeIndicators(null);
        }

        void SaveCurrentLayout(ProfileUi ui)
        {
            List<DisplayTarget> displays;
            SavedLayout layout;
            try
            {
                displays = DisplayManager.GetDisplays();
                layout = DisplayManager.CaptureLayout();
            }
            catch (Exception ex) { SetStatus(ex.Message, true); return; }

            var p = Settings.For(ui.Mode);
            p.Screens = displays.Where(d => d.Active).Select(d => new ScreenRef { Key = d.Key, Name = d.DisplayName }).ToList();
            p.Layout = layout;
            SaveQuietly();
            RefreshAll();
            SetStatus("Saved to the " + Modes.Name(ui.Mode) + " profile: " + layout.Summary, false);
        }

        void ForgetLayout(ProfileUi ui)
        {
            Settings.For(ui.Mode).Layout = null;
            SaveQuietly();
            UpdateLayoutLabel(ui);
            SetStatus(Modes.Name(ui.Mode) + " profile will use the layout Windows remembers.", false);
        }

        void UpdateLayoutLabel(ProfileUi ui)
        {
            var layout = Settings.For(ui.Mode).Layout;
            ui.Forget.Visible = layout != null;
            ui.Layout.ForeColor = layout != null ? Theme.Fg : Theme.Muted;
            ui.Layout.Text = layout != null ? layout.Summary : "Not saved - Windows picks the layout.";
        }

        static string ShortName(string displayName)
        {
            int i = displayName.IndexOf("  (");
            return i > 0 ? displayName.Substring(0, i) : displayName;
        }

        void OnAudioChanged(ProfileUi ui)
        {
            if (loading) return;
            var item = ui.Audio.SelectedEntry;
            if (item == null) return;
            string id = (string)item.Tag ?? "";
            string name = id == "" ? "" : ShortAudioName(item.Text);
            var p = Settings.For(ui.Mode);
            p.AudioId = id;
            p.AudioName = name;
            SaveQuietly();

            if (id == "")
            {
                SetStatus(Modes.Name(ui.Mode) + " mode will leave the sound output as it is.", false);
                return;
            }
            // If we're already in that mode, apply the choice right away.
            if (currentMode == ui.Mode) SwitchAudioInBackground(id, name, "", false);
            else SetStatus("Saved: " + Modes.Name(ui.Mode) + " mode will play sound on " + name + ".", false);
        }

        const string NotAvailable = "  ·  not available now";
        static string ShortAudioName(string s) { return s.EndsWith(NotAvailable) ? s.Substring(0, s.Length - NotAvailable.Length) : s; }

        // ---------------- refresh

        void FillScreens(ProfileUi ui, List<DisplayTarget> displays)
        {
            var p = Settings.For(ui.Mode);
            var keys = p.Keys;
            var rows = new List<CheckList.Row>();
            foreach (var d in displays)
                rows.Add(new CheckList.Row
                {
                    Key = d.Key, SaveName = d.DisplayName, Text = ShortName(d.DisplayName) + "  ·  " + d.Connection,
                    Checked = keys.Contains(d.Key), Detail = d.Active ? "ON" : "", DetailColor = Theme.Success
                });
            // Screens saved in the profile but not plugged in right now (e.g. the TV while it's off).
            foreach (var s in p.Screens)
            {
                if (displays.Any(d => d.Key == s.Key)) continue;
                rows.Add(new CheckList.Row
                {
                    Key = s.Key, SaveName = s.Name, Text = ShortName(s.Name), Checked = true, Dim = true,
                    Detail = "NOT CONNECTED", DetailColor = Theme.Faint
                });
            }
            ui.Screens.SetRows(rows);
        }

        void FillAudio(ProfileUi ui, List<AudioDevice> outputs)
        {
            var p = Settings.For(ui.Mode);
            ui.Audio.Items.Clear();
            var keep = new DarkCombo.Item { Text = "Don't change", Muted = true, Tag = "" };
            ui.Audio.Items.Add(keep);
            DarkCombo.Item selected = string.IsNullOrEmpty(p.AudioId) ? keep : null;
            foreach (var d in outputs)
            {
                var it = new DarkCombo.Item { Text = d.Active ? d.Name : d.Name + NotAvailable, Muted = !d.Active, Tag = d.Id };
                ui.Audio.Items.Add(it);
                if (d.Id == p.AudioId) selected = it;
            }
            if (selected == null)
            {
                // e.g. the TV's HDMI sound, which only exists while the TV picture is on
                selected = new DarkCombo.Item { Text = p.AudioName + NotAvailable, Muted = true, Tag = p.AudioId };
                ui.Audio.Items.Add(selected);
            }
            ui.Audio.SelectedItem = selected;
        }

        void RefreshAudio()
        {
            if (pcUi == null) return;
            List<AudioDevice> outputs;
            try { outputs = AudioManager.GetOutputs(); }
            catch (Exception ex) { SetStatus("Could not read sound devices: " + ex.Message, true); return; }
            if (pcUi.Audio.DroppedDown || tvUi.Audio.DroppedDown) return;
            bool wasLoading = loading;
            loading = true;
            FillAudio(pcUi, outputs);
            FillAudio(tvUi, outputs);
            loading = wasLoading;
        }

        void RefreshAll()
        {
            if (pcUi == null) return;
            List<DisplayTarget> displays;
            try { displays = DisplayManager.GetDisplays(); }
            catch (Exception ex) { SetStatus(ex.Message, true); return; }

            FillScreens(pcUi, displays);
            FillScreens(tvUi, displays);
            UpdateLayoutLabel(pcUi);
            UpdateLayoutLabel(tvUi);
            RefreshAudio();
            UpdateModeIndicators(displays);
        }

        /// <param name="displays">Reuse an existing query, or null to query again.</param>
        void UpdateModeIndicators(List<DisplayTarget> displays)
        {
            try { currentMode = displays != null ? Modes.Current(displays) : Modes.Current(); }
            catch { currentMode = Mode.Unknown; }
            if (btnPc == null) return;

            string hk = Settings.HotkeyText;
            btnPc.Active = currentMode == Mode.PC;
            btnTv.Active = currentMode == Mode.TV;
            btnPc.Subtitle = btnPc.Active ? "Active now" : "Switch  ·  " + hk;
            btnTv.Subtitle = btnTv.Active ? "Active now" : "Switch  ·  " + hk;
            btnPc.Invalidate();
            btnTv.Invalidate();

            if (Settings.Tv.Screens.Count == 0) { pill.Text = "Set up your TV profile"; pill.Fill = Color.FromArgb(70, 56, 24); pill.TextFg = Theme.Warning; }
            else if (currentMode == Mode.TV) { pill.Text = "TV mode"; pill.Fill = Theme.AccentSoft; pill.TextFg = Theme.AccentHover; }
            else if (currentMode == Mode.PC) { pill.Text = "PC mode"; pill.Fill = Theme.AccentSoft; pill.TextFg = Theme.AccentHover; }
            else { pill.Text = "Custom setup"; pill.Fill = Theme.Hover; pill.TextFg = Theme.Muted; }
            pill.Fit(Sc);
            pill.Invalidate();

            string mode = currentMode == Mode.TV ? "TV mode" : currentMode == Mode.PC ? "PC mode" : "ready";
            string trayText = Program.AppName + " - " + mode + " (" + hk + ")";
            tray.Text = trayText.Length <= 63 ? trayText : Program.AppName + " - " + mode;
        }
    }

    // ------------------------------------------------------------------ Settings
    class SettingsDialog : ThemedForm
    {
        readonly MainForm main;
        HotkeyBox hotkey;
        Label hint;

        public SettingsDialog(MainForm main, int dpi) : base(dpi)
        {
            this.main = main;
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BuildUi();
        }

        protected override void BuildUi()
        {
            ClearUi();
            const float w = 460;
            ClientSize = new Size(Sc.S(w), Sc.S(348));

            Caption(this, "Switch hotkey", 24, 22, w - 48);
            hotkey = Place(this, new HotkeyBox { Font = Sc.Font(13, true), Combo = Settings.HotkeyText }, 24, 44, w - 48, 46);
            hotkey.RecordingChanged += delegate
            {
                // Release the hotkey while recording, so pressing the current combo doesn't switch screens.
                if (hotkey.Recording) main.UnregisterToggleHotkey(); else main.RegisterToggleHotkey();
                SetHint(hotkey.Recording ? "Press Ctrl and/or Alt with a key. Esc cancels." : "Toggles between PC and TV mode from anywhere. Click to change it.", false);
            };
            hotkey.Recorded += (mods, key) =>
            {
                string err = main.TryChangeHotkey(mods, key);
                if (err != null) { SetHint(err, true); return; }
                hotkey.Combo = Settings.HotkeyText;
                hotkey.SetRecording(false);
                SetHint("Saved. Press " + Settings.HotkeyText + " to switch.", false);
            };
            hint = AddLabel(this, "", 24, 96, w - 48, 20, Sc.Font(12), Theme.Muted);
            SetHint("Toggles between PC and TV mode from anywhere. Click to change it.", false);

            Caption(this, "General", 24, 138, w - 48);
            var startup = Place(this, new Toggle { Text = "Start with Windows", Font = Sc.Font(13), ForeColor = Theme.Fg }, 24, 160, w - 48, 26);
            try { startup.SetQuietly(Settings.StartWithWindows); } catch { }
            startup.CheckedChanged += delegate
            {
                try { Settings.StartWithWindows = startup.Checked; }
                catch (Exception ex) { SetHint("Could not change startup setting: " + ex.Message, true); }
            };
            AddLabel(this, "Runs quietly in the tray, so the hotkey always works.", 74, 186, w - 98, 20, Sc.Font(12), Theme.Muted);

            var refresh = Place(this, new Toggle { Text = "Laptop refresh rate follows charger / battery", Font = Sc.Font(13), ForeColor = Theme.Fg }, 24, 218, w - 48, 26);
            refresh.SetQuietly(Settings.LaptopRefreshFollowsPower);
            refresh.CheckedChanged += delegate
            {
                Settings.LaptopRefreshFollowsPower = refresh.Checked;
                try { Settings.Save(); } catch (Exception ex) { SetHint("Could not save settings: " + ex.Message, true); return; }
                if (!refresh.Checked) return;
                try
                {
                    string note = DisplayManager.ApplyLaptopRefreshForPower();
                    if (note != null) SetHint(note, false);
                }
                catch (Exception ex) { SetHint(ex.Message, true); }
            };
            AddLabel(this, "Highest rate on the charger, " + DisplayManager.BatteryRefreshHz + "Hz on battery, like G-Helper. Applied after every switch.",
                74, 244, w - 98, 36, Sc.Font(12), Theme.Muted);

            var setup = Place(this, new FlatButton { Text = "Setup wizard", Font = Sc.Font(13) }, 24, 296, 130, 34);
            setup.Click += delegate
            {
                Close();
                main.BeginInvoke((MethodInvoker)main.OpenSetup);
            };
            var done = Place(this, new FlatButton { Style = FlatButton.Kind.Primary, Text = "Done", Font = Sc.Font(13, true) }, w - 24 - 100, 296, 100, 34);
            done.Click += delegate { Close(); };
            ActiveControl = done;
        }

        void SetHint(string text, bool error)
        {
            hint.Text = text;
            hint.ForeColor = error ? Theme.Danger : Theme.Muted;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !hotkey.Recording) { Close(); e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    // ------------------------------------------------------------------ App picker
    class AppPicker : ThemedForm
    {
        List<AppChoice> all = new List<AppChoice>();
        bool loaded;
        ListView list;
        TextBox search;
        ImageList images;
        public AppChoice Result;

        public AppPicker(int dpi) : base(dpi)
        {
            Text = "Choose an app for TV mode";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BuildUi();
            Shown += delegate { search.Focus(); LoadInBackground(); };
        }

        protected override void BuildUi()
        {
            string query = search != null ? search.Text : "";
            ClearUi();
            if (images != null) images.Dispose();
            const float w = 480;
            ClientSize = new Size(Sc.S(w), Sc.S(560));

            // search field (borderless text box inside a rounded card)
            var box = Place(this, new Card { Fill = Theme.Inset, Radius = 8 * Sc.F }, 20, 20, w - 40, 38);
            search = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.Inset, ForeColor = Theme.Fg, Font = Sc.Font(14), Text = query,
                Left = Sc.S(12), Width = Sc.S(w - 40 - 24)
            };
            search.Top = (box.Height - search.PreferredHeight) / 2;
            box.Controls.Add(search);
            Native.SetPlaceholder(search, "Search apps");
            search.TextChanged += delegate { Fill(); };
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); e.Handled = true; }
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Choose(); }
            };

            int iconPx = Sc.S(20);
            images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(iconPx, iconPx) };
            images.Images.Add("blank", new Bitmap(iconPx, iconPx));
            // A taller row image gives the rows some breathing room (rows are as tall as the image list).
            var rowSpacer = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(1, Sc.S(30)) };
            list = Place(this, new ListView
            {
                View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, MultiSelect = false,
                HideSelection = false, SmallImageList = rowSpacer, BackColor = Theme.Card, ForeColor = Theme.Fg,
                BorderStyle = BorderStyle.None, Font = Sc.Font(13.5f)
            }, 20, 70, w - 40, 418);
            list.Columns.Add("App", list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            Native.UseDarkControlTheme(list, false);
            // Draw rows ourselves: Windows' own selection is a light bar that clashes with the dark theme.
            list.OwnerDraw = true;
            list.DrawColumnHeader += (s, e) => { e.DrawDefault = true; };
            list.DrawItem += (s, e) =>
            {
                bool sel = e.Item.Selected;
                var bounds = new Rectangle(0, e.Bounds.Top, list.ClientSize.Width, e.Bounds.Height);
                using (var b = new SolidBrush(sel ? Theme.AccentSoft : Theme.Card)) e.Graphics.FillRectangle(b, bounds);
                if (sel) using (var bar = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(bar, 0, bounds.Top + Sc.S(6), Sc.S(3), bounds.Height - Sc.S(12));
                int x = Sc.S(12);
                Image img = e.Item.ImageKey != null && images.Images.ContainsKey(e.Item.ImageKey) ? images.Images[e.Item.ImageKey] : null;
                if (img != null && e.Item.Tag != null)
                {
                    e.Graphics.DrawImage(img, x, bounds.Top + (bounds.Height - img.Height) / 2, img.Width, img.Height);
                    x += img.Width + Sc.S(12);
                }
                TextRenderer.DrawText(e.Graphics, e.Item.Text, list.Font, new Rectangle(x, bounds.Top, bounds.Width - x, bounds.Height),
                    e.Item.Tag != null ? Theme.Fg : Theme.Muted, sel ? Theme.AccentSoft : Theme.Card,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            list.DrawSubItem += (s, e) => { };
            list.ItemSelectionChanged += delegate { list.Invalidate(); };
            list.DoubleClick += delegate { Choose(); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Choose(); };

            var browse = Place(this, new FlatButton { Style = FlatButton.Kind.Ghost, Text = "Not listed? Browse for a file...", Font = Sc.Font(12.5f), TextAlignLeft = true, Radius = 6 },
                16, 504, 230, 34);
            browse.Click += delegate { Browse(); };
            var ok = Place(this, new FlatButton { Style = FlatButton.Kind.Primary, Text = "Choose", Font = Sc.Font(13, true) }, w - 20 - 100 - 10 - 90, 504, 100, 34);
            ok.Click += delegate { Choose(); };
            var cancel = Place(this, new FlatButton { Text = "Cancel", Font = Sc.Font(13) }, w - 20 - 90, 504, 90, 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            CancelButton = null;
            KeyPreview = true;

            if (loaded) { AddImages(); Fill(); }
            else list.Items.Add(new ListViewItem("Loading apps...") { ForeColor = Theme.Muted });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; }
            base.OnKeyDown(e);
        }

        /// <summary>Reading the Start menu and icons takes a few seconds, so do it without freezing the window.</summary>
        void LoadInBackground()
        {
            int iconPx = Sc.S(20);
            var worker = new Thread(delegate ()
            {
                List<AppChoice> apps;
                try { apps = AppCatalog.AllApps(iconPx); }
                catch { apps = new List<AppChoice>(); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        all = apps;
                        loaded = true;
                        AddImages();
                        Fill();
                    });
                }
                catch { } // picker closed before loading finished
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA); // the Shell objects used for Store apps want STA
            worker.Start();
        }

        void AddImages()
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i].Image != null) images.Images.Add("i" + i, all[i].Image);
        }

        void Fill()
        {
            if (!loaded) return;
            string q = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                if (q.Length > 0 && all[i].Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                list.Items.Add(new ListViewItem(all[i].Name, all[i].Image != null ? "i" + i : "blank") { Tag = all[i] });
            }
            if (list.Items.Count == 0)
                list.Items.Add(new ListViewItem("No apps match \"" + q + "\"") { ForeColor = Theme.Muted });
            list.EndUpdate();
            if (list.Items.Count > 0 && list.Items[0].Tag != null)
            {
                list.Items[0].Selected = true;
                list.Items[0].Focused = true;
            }
            // Start at the top. Done after layout, otherwise the list keeps the scroll position of the last added row.
            BeginInvoke((MethodInvoker)delegate
            {
                if (list.IsDisposed || list.Items.Count == 0) return;
                list.TopItem = list.Items[0];
                list.EnsureVisible(0);
            });
        }

        void Choose()
        {
            if (list.SelectedItems.Count == 0 || !(list.SelectedItems[0].Tag is AppChoice)) return;
            Result = (AppChoice)list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
        }

        void Browse()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "App to open in TV mode",
                Filter = "Apps and shortcuts (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|All files (*.*)|*.*",
                DereferenceLinks = false
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Result = new AppChoice
                {
                    Name = Path.GetFileNameWithoutExtension(dlg.FileName),
                    Path = dlg.FileName,
                    Image = AppCatalog.IconFor(dlg.FileName, Sc.S(20))
                };
                DialogResult = DialogResult.OK;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && images != null) images.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ App start options
    class AppStartOptions : ThemedForm
    {
        TextBox box;
        Toggle primary, audio;
        readonly TvApp app;
        readonly bool hasArguments;
        string value;
        bool waitForPrimary, waitForAudio;

        public AppStartOptions(int dpi, TvApp app) : base(dpi)
        {
            this.app = app;
            hasArguments = AppLauncher.CanClose(app.Path) && !AppLauncher.IsSteam(app.Path);
            value = app.Args;
            waitForPrimary = app.WaitForPrimary;
            waitForAudio = app.WaitForAudio;
            Text = "Start options for " + AppLauncher.NameOf(app);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BuildUi();
        }

        protected override void BuildUi()
        {
            if (box != null) value = box.Text;
            if (primary != null) waitForPrimary = primary.Checked;
            if (audio != null) waitForAudio = audio.Checked;
            ClearUi();
            const float w = 470;
            float y = 20;
            if (hasArguments)
            {
                AddLabel(this, "Command-line arguments", 20, y, w - 40, 20, Sc.Font(12.5f), Theme.Muted);
                var frame = Place(this, new Card { Fill = Theme.Inset, Radius = 8 * Sc.F }, 20, y + 26, w - 40, 36);
                box = new TextBox
                {
                    BorderStyle = BorderStyle.None, BackColor = Theme.Inset, ForeColor = Theme.Fg, Font = Sc.Font(13.5f),
                    Text = value ?? "", Left = Sc.S(10), Width = Sc.S(w - 60)
                };
                box.Top = (frame.Height - box.PreferredHeight) / 2;
                frame.Controls.Add(box);
                Native.SetPlaceholder(box, "For example, --fullscreen");
                y += 76;
            }

            primary = Place(this, new Toggle { Text = "Wait for a TV profile screen to become the main display",
                Font = Sc.Font(12.5f), ForeColor = Theme.Fg, Surface = BackColor }, 20, y, w - 40, 27);
            primary.SetQuietly(waitForPrimary);
            audio = Place(this, new Toggle { Text = "Wait for the TV sound output to become the default",
                Font = Sc.Font(12.5f), ForeColor = Theme.Fg, Surface = BackColor }, 20, y + 32, w - 40, 27);
            audio.SetQuietly(waitForAudio);
            AddLabel(this, "Apps are skipped after 12 seconds if a selected check is still pending.",
                20, y + 64, w - 40, 22, Sc.Font(12), Theme.Muted);
            AddLabel(this, "The sound check needs an output selected in the TV profile.",
                20, y + 86, w - 40, 22, Sc.Font(12), Theme.Muted);
            y += 120;
            ClientSize = new Size(Sc.S(w), Sc.S(y + 52));
            var ok = Place(this, new FlatButton { Style = FlatButton.Kind.Primary, Text = "Save", Font = Sc.Font(13, true) }, w - 210, y, 90, 34);
            ok.Click += delegate { SaveOptions(); };
            var cancel = Place(this, new FlatButton { Text = "Cancel", Font = Sc.Font(13) }, w - 110, y, 90, 34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
            ActiveControl = (Control)box ?? primary;
        }

        void SaveOptions()
        {
            if (box != null) app.Args = box.Text.Trim();
            app.WaitForPrimary = primary.Checked;
            app.WaitForAudio = audio.Checked;
            DialogResult = DialogResult.OK;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { SaveOptions(); e.Handled = e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    // ------------------------------------------------------------------ On-screen message
    /// <summary>A large message shown briefly in the middle of the main screen, then faded out. Click-through, never steals focus.</summary>
    class Osd : Form
    {
        static Osd current;

        readonly Label lblDetail;
        readonly System.Windows.Forms.Timer timer;
        int elapsed;
        const int HoldMs = 2200, TickMs = 30;

        public static void ShowMessage(Mode mode, string title, string detail, bool error)
        {
            if (current != null && !current.IsDisposed) current.Close();
            current = new Osd(mode, title, detail, error);
            current.Show();
        }

        /// <summary>Updates the second line if the message is still up. Returns false if it already closed.</summary>
        public static bool UpdateDetail(string detail, bool error)
        {
            if (current == null || current.IsDisposed) return false;
            current.lblDetail.Text = detail;
            if (error) current.lblDetail.ForeColor = Theme.Danger;
            current.elapsed = 0;
            current.Opacity = 0.97;
            return true;
        }

        Osd(Mode mode, string title, string detail, bool error)
        {
            var sc = new Scaler(Native.PrimaryDpi()); // sized for the main screen, where it appears
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            Opacity = 0.97;
            Size = new Size(sc.S(380), sc.S(196));

            var area = Native.PrimaryWorkArea();
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            Region = Region.FromHrgn(Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, sc.S(24), sc.S(24)));

            Color accent = error ? Theme.Danger : Theme.Accent;
            Controls.Add(new Panel { Left = 0, Top = 0, Width = Width, Height = sc.S(4), BackColor = accent });

            string glyph = error ? Theme.GlyphWarning : mode == Mode.TV ? Theme.GlyphTv : mode == Mode.PC ? Theme.GlyphPc : Theme.GlyphRefresh;
            Controls.Add(new Label
            {
                Text = glyph, Left = 0, Top = sc.S(22), Width = Width, Height = sc.S(48), TextAlign = ContentAlignment.MiddleCenter,
                Font = sc.Icons(38), ForeColor = accent, BackColor = Theme.Card, UseMnemonic = false
            });
            Controls.Add(new Label
            {
                Text = title, Left = 0, Top = sc.S(74), Width = Width, Height = sc.S(42), TextAlign = ContentAlignment.MiddleCenter,
                Font = sc.Font(28, true), ForeColor = Theme.Fg, BackColor = Theme.Card, UseMnemonic = false
            });
            lblDetail = new Label
            {
                Text = detail, Left = sc.S(20), Top = sc.S(122), Width = Width - sc.S(40), Height = sc.S(60), TextAlign = ContentAlignment.TopCenter,
                Font = sc.Font(14.5f), ForeColor = error ? Theme.Danger : Theme.Muted, BackColor = Theme.Card, UseMnemonic = false
            };
            Controls.Add(lblDetail);

            timer = new System.Windows.Forms.Timer { Interval = TickMs };
            timer.Tick += delegate
            {
                elapsed += TickMs;
                if (elapsed < HoldMs) return;
                Opacity -= 0.07;
                if (Opacity <= 0.05) { timer.Stop(); Close(); }
            };
            timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiNative.RoundCorners(Handle);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80        // WS_EX_TOOLWINDOW: no taskbar / Alt+Tab entry
                            | 0x08000000  // WS_EX_NOACTIVATE: never takes focus from a game or video
                            | 0x20        // WS_EX_TRANSPARENT: clicks go through
                            | 0x8;        // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
