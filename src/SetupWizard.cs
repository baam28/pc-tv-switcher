using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TVMode
{
    static class SetupSession
    {
        static readonly Mutex gate = new Mutex(false, "TVMode_DisplaySetup_7b3f");

        public static bool TryEnter()
        {
            try { return gate.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }

        public static void Exit() { gate.ReleaseMutex(); }
    }

    class SetupWizard : ThemedForm
    {
        enum Step { CapturePc, ChooseTv, Preview, Finish }

        const int PreviewSeconds = 15;
        readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 200 };
        readonly System.Windows.Forms.Timer identifyTimer = new System.Windows.Forms.Timer { Interval = 3500 };
        readonly Stopwatch previewClock = new Stopwatch();
        readonly HashSet<string> selected = new HashSet<string>(Settings.Tv.Keys);
        List<DisplayTarget> displays = new List<DisplayTarget>();
        List<AudioDevice> outputs = new List<AudioDevice>();
        readonly List<Form> displayNumbers = new List<Form>();
        Profile pc, tv;
        SavedLayout previousLayout;
        string previousAudio, tvAudio = Settings.Tv.AudioId;
        bool createShortcuts;
        Step step;
        Label status;
        string notice;
        bool noticeIsError;

        public string ResultMessage { get; private set; }
        public bool ResultError { get; private set; }

        public SetupWizard(int dpi) : base(dpi)
        {
            Text = Program.AppName + " setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            identifyTimer.Tick += delegate { CloseNumbers(); };
            previewTimer.Tick += delegate
            {
                if (previewClock.Elapsed.TotalSeconds >= PreviewSeconds)
                    ReturnToScreenSelection("The test timed out. Your previous display setup was restored.");
                else
                    SetNotice("Restoring in " + Math.Max(1, PreviewSeconds - (int)previewClock.Elapsed.TotalSeconds) + " seconds.", false);
            };
            BuildUi();
        }

        protected override void BuildUi()
        {
            ClearUi();
            const float width = 520;
            ClientSize = new Size(Sc.S(width), Sc.S(365));
            status = AddLabel(this, notice ?? "", 24, 260, width - 48, 54, Sc.Font(12.5f),
                noticeIsError ? Theme.Danger : Theme.Muted);

            switch (step)
            {
                case Step.CapturePc:
                    Heading("Save your PC setup", "Arrange your desk screens and select your usual Windows sound output. Then save the setup you are using now.");
                    Button("Capture PC setup", 344, 320, 152, CapturePc, true);
                    Button("Set up later", 24, 320, 112, Close);
                    break;
                case Step.ChooseTv:
                    Heading("Choose your TV screens", "Turn on the TV, then select the screens you want in TV mode. Identify numbers the screens that are already on.");
                    var list = Place(this, new CheckList { Font = Sc.Font(13), DetailFont = Sc.Font(11.5f, true), RowHeight = Sc.S(32) },
                        24, 100, width - 48, 112);
                    list.SetRows(displays.Select((d, i) => new CheckList.Row
                    {
                        Key = d.Key, Text = (i + 1) + ". " + d.DisplayName, Checked = selected.Contains(d.Key),
                        Detail = d.Active ? "ON" : "OFF"
                    }));
                    list.RowToggled += row => { if (row.Checked) selected.Add(row.Key); else selected.Remove(row.Key); };
                    Button("Identify", 24, 221, 92, Identify);
                    Button("Refresh", 124, 221, 92, RefreshDisplays);
                    Button("Back", 24, 320, 88, BackToPc);
                    Button("Test TV setup", 344, 320, 152, StartPreview, true);
                    break;
                case Step.Preview:
                    Heading("Can you see this on the TV?", "Confirm to keep testing and choose the TV sound output. The previous display setup returns automatically if you let the timer run out.");
                    Button("Restore now", 24, 320, 114, () => ReturnToScreenSelection("Your previous display setup was restored."));
                    Button("I can see this", 344, 320, 152, ConfirmPreview, true);
                    break;
                case Step.Finish:
                    Heading("Choose TV sound and shortcuts", "Choose the output to use in TV mode. Finishing saves both profiles and returns you to your previous setup.");
                    Caption(this, "TV sound output", 24, 103, width - 48);
                    var audio = Place(this, new DarkCombo { Font = Sc.Font(13), ItemHeight = Sc.S(26) }, 24, 126, width - 48, 32);
                    audio.Items.Add(new DarkCombo.Item { Text = "Don't change", Tag = "" });
                    foreach (var output in outputs)
                        audio.Items.Add(new DarkCombo.Item { Text = output.Name, Tag = output.Id });
                    audio.SelectedItem = audio.Items.Cast<DarkCombo.Item>().FirstOrDefault(i => (string)i.Tag == tvAudio) ?? audio.Items[0];
                    audio.SelectedIndexChanged += delegate { tvAudio = (string)audio.SelectedEntry.Tag; };
                    Button("Test sound output", 24, 169, 148, TestSound);
                    Button("Refresh", 180, 169, 92, RefreshAudio);
                    var shortcuts = Place(this, new Toggle { Text = "Create three desktop shortcuts", Font = Sc.Font(13), ForeColor = Theme.Fg },
                        24, 218, width - 48, 28);
                    shortcuts.SetQuietly(createShortcuts);
                    shortcuts.CheckedChanged += delegate { createShortcuts = shortcuts.Checked; };
                    Button("Back", 24, 320, 88, () => ReturnToScreenSelection(null));
                    Button("Finish and save", 344, 320, 152, Finish, true);
                    break;
            }
        }

        void Heading(string title, string description)
        {
            AddLabel(this, title, 24, 18, 472, 31, Sc.Font(20, true), Theme.Fg);
            AddLabel(this, description, 24, 52, 472, 44, Sc.Font(13), Theme.Muted);
        }

        void Button(string text, int x, int y, int width, Action action, bool primary = false)
        {
            var button = Place(this, new FlatButton { Text = text, Font = Sc.Font(13, primary),
                Style = primary ? FlatButton.Kind.Primary : FlatButton.Kind.Ghost }, x, y, width, 33);
            button.Click += delegate { action(); };
        }

        void SetNotice(string text, bool error)
        {
            notice = text;
            noticeIsError = error;
            status.Text = text ?? "";
            status.ForeColor = error ? Theme.Danger : Theme.Muted;
        }

        void CapturePc()
        {
            try
            {
                var layout = DisplayManager.CaptureLayout();
                displays = DisplayManager.GetDisplays();
                string audioId = "";
                string audioName = "";
                try
                {
                    audioId = AudioManager.GetDefaultId() ?? "";
                    var audio = AudioManager.GetOutputs().FirstOrDefault(d => d.Id == audioId);
                    if (audio != null) audioName = audio.Name;
                    else audioId = "";
                }
                catch { audioId = ""; }
                pc = new Profile { Layout = layout, Screens = ScreensFor(layout), AudioId = audioId, AudioName = audioName };
                step = Step.ChooseTv;
                SetNotice(null, false);
                BuildUi();
            }
            catch (Exception ex) { SetNotice("Couldn't capture the PC setup. " + ex.Message, true); }
        }

        List<ScreenRef> ScreensFor(SavedLayout layout)
        {
            var keys = layout.Keys;
            return displays.Where(d => keys.Contains(d.Key)).Select(d => new ScreenRef { Key = d.Key, Name = d.DisplayName }).ToList();
        }

        void BackToPc()
        {
            CloseNumbers();
            step = Step.CapturePc;
            SetNotice(null, false);
            BuildUi();
        }

        void RefreshDisplays()
        {
            try
            {
                CloseNumbers();
                displays = DisplayManager.GetDisplays();
                selected.IntersectWith(displays.Select(d => d.Key));
                SetNotice(null, false);
                BuildUi();
            }
            catch (Exception ex) { SetNotice(ex.Message, true); }
        }

        void Identify()
        {
            CloseNumbers();
            try
            {
                var names = DisplayManager.ActiveScreenNames();
                foreach (var screen in Screen.AllScreens)
                {
                    var numbers = displays.Select((d, i) => new { d, Number = i + 1 })
                        .Where(item => names.ContainsKey(item.d.Key)
                            && string.Equals(names[item.d.Key], screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Number.ToString());
                    string label = string.Join(" / ", numbers);
                    if (label.Length == 0) continue;
                    var marker = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
                        StartPosition = FormStartPosition.Manual, BackColor = Theme.Bg, ClientSize = new Size(220, 100) };
                    var area = screen.Bounds;
                    marker.Location = new Point(area.Left + (area.Width - marker.Width) / 2, area.Top + (area.Height - marker.Height) / 2);
                    marker.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 34, FontStyle.Bold), ForeColor = Theme.Fg });
                    displayNumbers.Add(marker);
                    marker.Show(this);
                }
                identifyTimer.Start();
            }
            catch (Exception ex) { CloseNumbers(); SetNotice(ex.Message, true); }
        }

        void CloseNumbers()
        {
            identifyTimer.Stop();
            foreach (var marker in displayNumbers) marker.Dispose();
            displayNumbers.Clear();
        }

        void StartPreview()
        {
            CloseNumbers();
            try
            {
                var connected = DisplayManager.GetDisplays();
                if (!selected.Any() || !selected.All(key => connected.Any(d => d.Key == key)))
                {
                    SetNotice("Select at least one connected screen. Refresh if you plugged in or unplugged a display.", true);
                    return;
                }
                previousLayout = DisplayManager.CaptureLayout();
                try { previousAudio = AudioManager.GetDefaultId(); } catch { previousAudio = null; }
                DisplayManager.Apply(d => selected.Contains(d.Key), false);
                step = Step.Preview;
                previewClock.Restart();
                previewTimer.Start();
                SetNotice("Restoring in " + PreviewSeconds + " seconds.", false);
                BuildUi();
                MoveToPrimary();
            }
            catch (Exception ex)
            {
                ReturnToScreenSelection("Couldn't test the TV setup. " + ex.Message, true);
            }
        }

        void ConfirmPreview()
        {
            if (previewClock.Elapsed.TotalSeconds >= PreviewSeconds)
            {
                ReturnToScreenSelection("The test timed out. Your previous display setup was restored.");
                return;
            }
            try
            {
                var layout = DisplayManager.CaptureLayout();
                if (!layout.Keys.SetEquals(selected))
                    throw new InvalidOperationException("The active displays did not match your selection.");
                displays = DisplayManager.GetDisplays();
                tv = new Profile { Layout = layout, Screens = ScreensFor(layout) };
                previewTimer.Stop();
                step = Step.Finish;
                SetNotice(null, false);
                RefreshAudio();
            }
            catch (Exception ex) { ReturnToScreenSelection(ex.Message, true); }
        }

        void RefreshAudio()
        {
            try { outputs = AudioManager.GetOutputs().Where(d => d.Active).ToList(); }
            catch (Exception ex) { SetNotice("Couldn't list sound outputs. " + ex.Message, true); }
            if (!outputs.Any(d => d.Id == tvAudio)) tvAudio = "";
            BuildUi();
        }

        void TestSound()
        {
            if (string.IsNullOrEmpty(tvAudio) || string.IsNullOrEmpty(previousAudio))
            {
                SetNotice("Select an output to test. Windows must have a previous default output to restore.", true);
                return;
            }
            try
            {
                string error;
                bool ok = AudioManager.SetDefault(tvAudio, 0, out error);
                SetNotice(ok ? "TV sound selected. Play something to check it, then finish to restore your previous output." : error, !ok);
            }
            catch (Exception ex) { SetNotice(ex.Message, true); }
        }

        void ReturnToScreenSelection(string message, bool error = false)
        {
            string restoreWarning;
            if (!RestorePrevious(out restoreWarning))
            {
                SetNotice(restoreWarning, true);
                return;
            }
            step = Step.ChooseTv;
            SetNotice(restoreWarning ?? message, error || restoreWarning != null);
            BuildUi();
            MoveToPrimary();
        }

        bool RestorePrevious(out string warning)
        {
            previewTimer.Stop();
            warning = null;
            if (previousLayout == null) return true;
            try
            {
                bool restored;
                try { restored = DisplayManager.ApplyLayout(previousLayout, false); }
                catch { restored = false; }
                if (!restored)
                {
                    var keys = previousLayout.Keys;
                    DisplayManager.Apply(d => keys.Contains(d.Key), false);
                    warning = "Windows restored the previous screens that are still connected using its current layout.";
                }
            }
            catch (Exception ex)
            {
                warning = "Couldn't restore the previous screens. Reconnect them and try again. " + ex.Message;
                return false;
            }
            previousLayout = null;
            if (!string.IsNullOrEmpty(previousAudio))
            {
                try
                {
                    string error;
                    if (AudioManager.GetDefaultId() != previousAudio && !AudioManager.SetDefault(previousAudio, 3000, out error))
                        warning = "Couldn't restore the previous sound output. " + error;
                }
                catch (Exception ex) { warning = "Couldn't restore the previous sound output. " + ex.Message; }
            }
            return true;
        }

        void MoveToPrimary()
        {
            var area = Native.PrimaryWorkArea();
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
            Activate();
        }

        void Finish()
        {
            string restoreWarning;
            if (!RestorePrevious(out restoreWarning))
            {
                SetNotice(restoreWarning, true);
                return;
            }
            MoveToPrimary();
            var audio = outputs.FirstOrDefault(d => d.Id == tvAudio);
            tv.AudioId = audio == null ? "" : audio.Id;
            tv.AudioName = audio == null ? "" : audio.Name;
            var oldPc = Settings.Pc;
            var oldTv = Settings.Tv;
            bool oldPending = Settings.SetupPending;
            Settings.Pc = pc;
            Settings.Tv = tv;
            Settings.SetupPending = false;
            try { Settings.Save(); }
            catch (Exception ex)
            {
                Settings.Pc = oldPc;
                Settings.Tv = oldTv;
                Settings.SetupPending = oldPending;
                SetNotice("Couldn't save the profiles. " + ex.Message, true);
                return;
            }
            ResultMessage = "PC and TV profiles saved.";
            if (createShortcuts)
            {
                try
                {
                    DesktopShortcuts.Create(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Application.ExecutablePath);
                    ResultMessage += " Desktop shortcuts created.";
                }
                catch (Exception ex) { ResultError = true; ResultMessage += " Couldn't create desktop shortcuts. " + ex.Message; }
            }
            if (restoreWarning != null) { ResultError = true; ResultMessage += " " + restoreWarning; }
            DialogResult = DialogResult.OK;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            string warning;
            if (!RestorePrevious(out warning))
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    SetNotice(warning, true);
                }
            }
            else if (warning != null)
            {
                ResultMessage = warning;
                ResultError = true;
            }
            CloseNumbers();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { previewTimer.Dispose(); CloseNumbers(); identifyTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static class DesktopShortcuts
    {
        static readonly string[] names = { "PC-TV Switcher - PC mode", "PC-TV Switcher - TV mode", "PC-TV Switcher - TV mode without apps" };
        static readonly string[] arguments = { "pc", "tv", "tv-no-apps" };

        public static void Create(string desktop, string exe)
        {
            if (!Directory.Exists(desktop)) throw new IOException("Windows could not locate your Desktop folder.");
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var created = new List<string>();
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    string path = Path.Combine(desktop, names[i] + ".lnk");
                    bool exists = File.Exists(path);
                    string draft = exists ? path : Path.Combine(desktop, Guid.NewGuid().ToString("N") + ".lnk");
                    object shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { draft });
                    try
                    {
                        var type = shortcut.GetType();
                        if (exists)
                        {
                            string target = (string)type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                            string args = (string)type.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null);
                            if (!string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) || args != arguments[i])
                                throw new IOException("A different shortcut already uses the name " + names[i] + ".");
                            continue;
                        }
                        type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                        type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments[i] });
                        type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
                        type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                        File.Move(draft, path);
                        created.Add(path);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(shortcut);
                        if (!exists && File.Exists(draft)) File.Delete(draft);
                    }
                }
            }
            catch
            {
                foreach (var path in created)
                    try { File.Delete(path); } catch { }
                throw;
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
    }
}
