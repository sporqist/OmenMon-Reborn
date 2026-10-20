  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // Power & Efficiency window: what draws power right now and why, plus Eco+. The apps
    // keeping the discrete GPU awake can be added to a graphics profile as power saving
    // (the active one unless the user picks another; Graphics apps keeps their originals,
    // and Windows default lives there).
    // Readings run on a worker thread every 3 s (WMI and NVML calls can take a moment);
    // the window only renders them. NVML is read far less often (NvmlSchedule), so the
    // window does not keep the discrete GPU awake. Nothing here reads the EC.
    public class GuiFormPower : Form {

        private static GuiFormPower instance;

        private TextBox TxtInfo;
        private CheckBox ChkEco, ChkRefresh;
        private Label LblEco, LblGpuNote;
        private CheckedListBox LstApps;
        private Button BtnRelease, BtnClose;

        private readonly EcoPlus Eco;
        private readonly GraphicsApps GraphicsApps;
        private Thread Worker;
        private volatile bool ClosingDown;
        private PowerSnapshot Last;
        private readonly Dictionary<string, GpuHolder> Listed = new Dictionary<string, GpuHolder>(StringComparer.OrdinalIgnoreCase);
        private bool Building;

        // Shows the window, creating it if needed (one at a time)
        public static void ShowWindow(EcoPlus eco, GraphicsApps graphics) {
            if(instance == null || instance.IsDisposed) {
                instance = new GuiFormPower(eco, graphics);
                instance.Show();
            } else {
                if(instance.WindowState == FormWindowState.Minimized)
                    instance.WindowState = FormWindowState.Normal;
                instance.Activate();
            }
        }

        private GuiFormPower(EcoPlus eco, GraphicsApps graphics) {
            this.Eco = eco;
            this.GraphicsApps = graphics;
            BuildUi();
            this.FormClosing += (s, e) => this.ClosingDown = true;
            this.Shown += (s, e) => StartWorker();
        }

        private void BuildUi() {
            this.Building = true;
            this.Text = "Power & Efficiency";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(600, 640);
            this.Font = new Font("Segoe UI", 9F);

            TxtInfo = new TextBox {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(12, 12), Size = new Size(576, 330),
                Font = new Font("Consolas", 9F), Text = "Reading..." };

            GroupBox grpEco = new GroupBox { Text = "Eco+ (on battery only)", Location = new Point(12, 350), Size = new Size(576, 100) };
            ChkEco = new CheckBox {
                Text = "Turbo boost off on battery (plugged in stays as it is)",
                Location = new Point(12, 22), Size = new Size(550, 22),
                Checked = Config.EcoPlusEnabled };
            ChkRefresh = new CheckBox {
                Text = "Refresh rate " + Config.PresetRefreshRateLow + " Hz on battery (back to the previous rate when plugged in)",
                Location = new Point(12, 46), Size = new Size(550, 22),
                Checked = Config.EcoPlusRefreshRate > 0, Enabled = Config.EcoPlusEnabled };
            LblEco = new Label { Location = new Point(12, 72), Size = new Size(550, 20), ForeColor = SystemColors.GrayText };
            ChkEco.CheckedChanged += (s, e) => { if(!this.Building) EventEcoChanged(); };
            ChkRefresh.CheckedChanged += (s, e) => { if(!this.Building) EventEcoChanged(); };
            grpEco.Controls.AddRange(new Control[] { ChkEco, ChkRefresh, LblEco });

            GroupBox grpGpu = new GroupBox { Text = "Apps keeping the discrete GPU awake", Location = new Point(12, 456), Size = new Size(576, 142) };
            LstApps = new CheckedListBox { Location = new Point(12, 22), Size = new Size(552, 58), CheckOnClick = true, IntegralHeight = false };
            BtnRelease = new Button { Text = "Add to a profile as power saving…",Location = new Point(12, 86), Size = new Size(300, 26) };
            LblGpuNote = new Label {
                Text = "Apps pick the change up when they restart. Originals are kept (Windows default puts them back).",
                Location = new Point(12, 116), Size = new Size(552, 20), ForeColor = SystemColors.GrayText };
            BtnRelease.Click += (s, e) => EventRelease();
            grpGpu.Controls.AddRange(new Control[] { LstApps, BtnRelease, LblGpuNote });

            BtnClose = new Button { Text = "Close", Location = new Point(498, 606), Size = new Size(90, 26) };
            BtnClose.Click += (s, e) => Close();

            this.Controls.AddRange(new Control[] { TxtInfo, grpEco, grpGpu, BtnClose });
            this.Building = false;
        }

        private void StartWorker() {
            this.Worker = new Thread(() => {
                using(PowerReader reader = new PowerReader()) {
                    reader.Read();   // primes the CPU counters
                    while(!this.ClosingDown) {
                        PowerSnapshot s = null;
                        try { s = reader.Read(); } catch { }
                        if(s != null && !this.ClosingDown) {
                            try { BeginInvoke((Action) (() => Render(s))); } catch { }
                        }
                        for(int i = 0; i < 30 && !this.ClosingDown; i++)
                            Thread.Sleep(100);
                    }
                }
            }) { IsBackground = true, Name = "OmenMonPowerReader" };
            this.Worker.Start();
        }

        private void Render(PowerSnapshot s) {
            if(this.IsDisposed)
                return;
            this.Last = s;
            int top = TxtInfo.SelectionStart;
            TxtInfo.Text = PowerMath.Format(s).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            TxtInfo.SelectionStart = top;

            // The app list changes only when the set of apps does, so checks are kept
            // (Windows components such as dwm are shown in the text only, never offered)
            List<GpuHolder> offered = s.GpuHolders.Where(h => h.AppKey != null && !AppIdentity.IsSystemComponent(h.Path)).ToList();
            HashSet<string> keys = new HashSet<string>(offered.Select(h => h.AppKey), StringComparer.OrdinalIgnoreCase);
            if(!keys.SetEquals(this.Listed.Keys)) {
                this.Listed.Clear();
                LstApps.Items.Clear();
                foreach(GpuHolder h in offered) {
                    if(this.Listed.ContainsKey(h.AppKey))
                        continue;
                    this.Listed[h.AppKey] = h;
                    // Apps set to high performance on purpose are left unchecked
                    bool check = RegistryGpuPreferences.Describe(h.Preference) != "high performance"
                        && RegistryGpuPreferences.Describe(h.Preference) != "power saving";
                    LstApps.Items.Add(new AppItem(h), check);
                }
            }
            BtnRelease.Enabled = LstApps.Items.Count > 0;
        }

        private void EventEcoChanged() {
            Config.EcoPlusEnabled = ChkEco.Checked;
            Config.EcoPlusRefreshRate = ChkEco.Checked && ChkRefresh.Checked ? Config.PresetRefreshRateLow : 0;
            ChkRefresh.Enabled = ChkEco.Checked;
            try { Config.Save(); } catch { }
            string what = null;
            try { what = this.Eco?.Maintain(SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline); } catch(Exception ex) { what = "failed: " + ex.Message; }
            if(what != null)
                EcoPlusLog.Append(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, EcoPlusLog.FileName),
                    DateTime.Now, "Power window: " + what);
            if(what == null && this.Eco?.LastError != null)
                what = "Not changed: " + this.Eco.LastError;
            if(what == null && this.Eco != null && this.Eco.RefreshPending)
                what = "On. The refresh rate changes once the power source has been stable for "
                    + Config.EcoPlusRefreshHold + " s.";
            LblEco.Text = what ?? (ChkEco.Checked ? "On." : "Off.");
        }

        // Adds the checked apps to a graphics profile as power saving and applies it;
        // restarts nothing. With more than one profile the user picks it (the active one
        // first), else it is the active one ("Power saving", made if none is active).
        // Nothing is added while the configuration file did not load.
        private void EventRelease() {
            List<string> apps = LstApps.CheckedItems.Cast<AppItem>().Select(i => i.Holder.AppKey).ToList();
            if(apps.Count == 0 || this.GraphicsApps == null)
                return;
            if(Config.LoadFailed) {
                LblGpuNote.Text = "Not added: OmenMon.xml did not load, so no profile is changed.";
                return;
            }
            string active = Config.GraphicsProfileActive;
            string target = active;
            List<string> choices = GraphicsPowerTarget.Choices(Config.GraphicsProfiles, active, out int selected);
            if(choices != null) {
                string choice = GuiGraphicsDialogs.PickProfile(this, "Add to a profile as power saving",
                    "Add the " + apps.Count + " checked app(s) as power saving to:", choices, selected,
                    c => GraphicsPowerTarget.Note(c, active));
                if(choice == null)
                    return;
                target = GraphicsPowerTarget.AddTarget(choice);
            }
            GraphicsResult result;
            GraphicsProfile profile;
            try {
                profile = GraphicsProfile.AddApps(Config.GraphicsProfiles, target,
                    apps, GpuChoice.PowerSaving, this.GraphicsApps.State.Managed);
                result = this.GraphicsApps.Apply(profile, "Power window");
            } catch(Exception ex) {
                LblGpuNote.Text = "Failed: " + ex.Message;
                return;
            }
            if(result.Error == null)
                Config.GraphicsProfileActive = profile.Name;
            try { Config.Save(); } catch { }
            int n = result.Restart.Count;
            if(result.Error != null)
                LblGpuNote.Text = n + " app(s) set to power saving; the rest left as they are: " + result.Error;
            else
                LblGpuNote.Text = n + " app(s) set to power saving in " + profile.Name + ". They use the integrated GPU after a restart.";
            this.Listed.Clear();   // re-list with the new preferences
        }

        private sealed class AppItem {
            public readonly GpuHolder Holder;
            public AppItem(GpuHolder h) { this.Holder = h; }
            public override string ToString() {
                return this.Holder.Name + "  (" + RegistryGpuPreferences.Describe(this.Holder.Preference) + ")  " + this.Holder.AppKey;
            }
        }

    }

}
