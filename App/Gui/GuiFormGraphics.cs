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

    // Graphics apps window: the profiles of per-app GPU preferences, the apps of the
    // selected one with the GPU each is to use and its registry value now, applying a
    // profile (or Windows default) and the restart dialog after it, and a status area:
    // the discrete GPU's state and the apps holding it, and whether the registry is as
    // the active profile has it.
    //
    // The decisions are in GraphicsUi.cs (GraphicsProfileEditor saves every edit at
    // once, and nothing while OmenMon.xml did not load); this only shows them. The GPU
    // is read on a worker thread when the window opens, on Refresh, and 30 s after an
    // apply or a restart; never on a timer, so the window does not keep it awake.
    // Nothing here touches the EC or the BIOS.
    public class GuiFormGraphics : Form {

        private static GuiFormGraphics instance;

        private ListBox LstProfiles;
        private Button BtnNew, BtnDuplicate, BtnRename, BtnDelete, BtnApply, BtnRefresh, BtnClose;
        private GroupBox GrpApps;
        private DataGridView Grid;
        private ComboBox CmbAddAs;
        private Button BtnAddExe, BtnAddRunning, BtnRemove, BtnApplyAgain;
        private Label LblAppsNote, LblActive, LblDrift, LblGpu, LblLast;

        private readonly GraphicsApps Graphics;
        private readonly GraphicsDrift Startup;
        private readonly GraphicsProfileEditor Editor;
        private readonly RegistryGpuPreferences Prefs = new RegistryGpuPreferences();

        private string Selected = GraphicsProfile.WindowsDefaultName;
        private string Rendered;            // what the lists show (see Signature)
        private bool Rendering;
        private bool Reading;               // a GPU reading is under way
        private PowerSnapshot LastGpu;      // the last GPU reading (holders for "Add running app")
        private int CheckGeneration;        // the latest scheduled check after an apply wins

        private const int ColName = 0, ColChoice = 1, ColNow = 2, ColKey = 3;

        // Shows the window, creating it if needed (one at a time)
        public static void ShowWindow(GraphicsApps graphics, GraphicsDrift startup) {
            if(graphics == null)
                throw new InvalidOperationException("Graphics apps did not start");
            if(instance == null || instance.IsDisposed) {
                instance = new GuiFormGraphics(graphics, startup);
                instance.Show();
            } else {
                if(instance.WindowState == FormWindowState.Minimized)
                    instance.WindowState = FormWindowState.Normal;
                instance.Activate();
            }
        }

        private GuiFormGraphics(GraphicsApps graphics, GraphicsDrift startup) {
            this.Graphics = graphics;
            this.Startup = startup;
            this.Editor = new GraphicsProfileEditor(
                () => Config.GraphicsProfiles, () => Config.GraphicsProfileActive, a => Config.GraphicsProfileActive = a ?? "",
                () => Config.LoadFailed, Config.SaveCore);
            string active = this.Editor.Active;
            if(this.Editor.Find(active) != null)
                this.Selected = this.Editor.Find(active).Name;
            BuildUi();
            RenderAll();
            RenderDrift();
            if(!this.Editor.CanEdit)
                LblLast.Text = GraphicsProfileEditor.LoadFailedText;
            this.Shown += (s, e) => ReadGpu();
            // The Power window can add apps to a profile: shown when this one is used again
            this.Activated += (s, e) => { if(this.Rendered != Signature()) RenderAll(); };
        }

#region Layout
        private void BuildUi() {
            this.Text = "Graphics apps";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(880, 612);
            this.Font = new Font("Segoe UI", 9F);

            GroupBox grpProfiles = new GroupBox { Text = "Profiles", Location = new Point(12, 12), Size = new Size(236, 400) };
            LstProfiles = new ListBox { Location = new Point(10, 22), Size = new Size(216, 300), IntegralHeight = false };
            LstProfiles.SelectedIndexChanged += (s, e) => EventProfileSelected();
            BtnNew = new Button { Text = "New…", Location = new Point(10, 330), Size = new Size(104, 26) };
            BtnDuplicate = new Button { Text = "Duplicate…", Location = new Point(122, 330), Size = new Size(104, 26) };
            BtnRename = new Button { Text = "Rename…", Location = new Point(10, 362), Size = new Size(104, 26) };
            BtnDelete = new Button { Text = "Delete…", Location = new Point(122, 362), Size = new Size(104, 26) };
            BtnNew.Click += (s, e) => EventNew();
            BtnDuplicate.Click += (s, e) => EventDuplicate();
            BtnRename.Click += (s, e) => EventRename();
            BtnDelete.Click += (s, e) => EventDelete();
            grpProfiles.Controls.AddRange(new Control[] { LstProfiles, BtnNew, BtnDuplicate, BtnRename, BtnDelete });

            GrpApps = new GroupBox { Text = "Apps", Location = new Point(256, 12), Size = new Size(612, 400) };
            Grid = new DataGridView {
                Location = new Point(10, 22), Size = new Size(592, 300),
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter, BackgroundColor = SystemColors.Window,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                // The fixed default height clipped the header text (Marius, 2026-10-21)
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
            Grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "App", Width = 140, ReadOnly = true });
            DataGridViewComboBoxColumn choice = new DataGridViewComboBoxColumn {
                HeaderText = "GPU in this profile", Width = 150, FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
            choice.Items.AddRange(GraphicsChoiceLabel.All.Cast<object>().ToArray());
            Grid.Columns.Add(choice);
            Grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Registry now", Width = 110, ReadOnly = true });
            Grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Path or app ID", ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            Grid.CurrentCellDirtyStateChanged += (s, e) => {
                if(Grid.IsCurrentCellDirty && Grid.CurrentCell != null && Grid.CurrentCell.ColumnIndex == ColChoice)
                    Grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            Grid.CellValueChanged += EventChoiceChanged;
            Grid.DataError += (s, e) => e.ThrowException = false;
            Grid.SelectionChanged += (s, e) => SetButtons();

            Label lblAddAs = new Label { Text = "Add as", Location = new Point(10, 333), Size = new Size(48, 20) };
            CmbAddAs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(58, 330), Size = new Size(140, 24) };
            CmbAddAs.Items.AddRange(GraphicsChoiceLabel.All.Cast<object>().ToArray());
            CmbAddAs.SelectedIndex = 0;
            BtnAddExe = new Button { Text = "Add exe…", Location = new Point(206, 329), Size = new Size(110, 26) };
            BtnAddRunning = new Button { Text = "Add running app…", Location = new Point(322, 329), Size = new Size(140, 26) };
            BtnRemove = new Button { Text = "Remove", Location = new Point(492, 329), Size = new Size(110, 26) };
            BtnAddExe.Click += (s, e) => EventAddExe();
            BtnAddRunning.Click += (s, e) => EventAddRunning();
            BtnRemove.Click += (s, e) => EventRemove();
            LblAppsNote = new Label { Location = new Point(10, 362), Size = new Size(592, 32), ForeColor = SystemColors.GrayText };
            GrpApps.Controls.AddRange(new Control[] { Grid, lblAddAs, CmbAddAs, BtnAddExe, BtnAddRunning, BtnRemove, LblAppsNote });

            BtnApply = new Button { Location = new Point(12, 420), Size = new Size(236, 30) };
            BtnApply.Click += (s, e) => EventApply(false);
            BtnRefresh = new Button { Text = "Refresh status", Location = new Point(12, 456), Size = new Size(236, 26) };
            BtnRefresh.Click += (s, e) => { RenderAll(); RenderDrift(); ReadGpu(); };

            GroupBox grpStatus = new GroupBox { Text = "Status", Location = new Point(256, 416), Size = new Size(612, 156) };
            LblActive = new Label { Location = new Point(10, 22), Size = new Size(592, 20) };
            LblDrift = new Label { Location = new Point(10, 44), Size = new Size(452, 34) };
            BtnApplyAgain = new Button { Text = "Apply again…", Location = new Point(472, 44), Size = new Size(130, 26), Visible = false };
            BtnApplyAgain.Click += (s, e) => EventApply(true);
            LblGpu = new Label { Location = new Point(10, 80), Size = new Size(592, 34), Text = "Discrete GPU: not read yet" };
            LblLast = new Label { Location = new Point(10, 116), Size = new Size(592, 34), ForeColor = SystemColors.GrayText };
            grpStatus.Controls.AddRange(new Control[] { LblActive, LblDrift, BtnApplyAgain, LblGpu, LblLast });

            BtnClose = new Button { Text = "Close", Location = new Point(778, 578), Size = new Size(90, 26) };
            BtnClose.Click += (s, e) => Close();

            this.Controls.AddRange(new Control[] { grpProfiles, GrpApps, BtnApply, BtnRefresh, grpStatus, BtnClose });
        }
#endregion

#region Rendering
        private bool SelectedIsDefault {
            get { return this.Selected == GraphicsProfile.WindowsDefaultName; }
        }

        private GraphicsProfile SelectedProfile {
            get { return this.SelectedIsDefault ? null : this.Editor.Find(this.Selected); }
        }

        // What the lists show: the profiles, their apps and the active one
        private string Signature() {
            return string.Join("|", this.Editor.Profiles.Select(p => p.Key + ":"
                + string.Join(",", p.Value.Apps.Select(a => a.Key + "=" + (int) a.Choice)))) + "|" + this.Editor.Active;
        }

        private void RenderAll() {
            this.Rendering = true;
            try {
                List<GraphicsProfileItem> items = GraphicsLists.Profiles(this.Editor.Profiles, this.Editor.Active);
                if(!items.Any(i => string.Equals(i.Name, this.Selected, StringComparison.OrdinalIgnoreCase)))
                    this.Selected = GraphicsProfile.WindowsDefaultName;
                LstProfiles.BeginUpdate();
                LstProfiles.Items.Clear();
                foreach(GraphicsProfileItem i in items)
                    LstProfiles.Items.Add(i);
                LstProfiles.SelectedIndex = Math.Max(0, items.FindIndex(i => string.Equals(i.Name, this.Selected, StringComparison.OrdinalIgnoreCase)));
                LstProfiles.EndUpdate();
                this.Selected = items[LstProfiles.SelectedIndex].Name;
            } finally {
                this.Rendering = false;
            }
            RenderApps();
            RenderActive();
            this.Rendered = Signature();
        }

        private void RenderApps() {
            this.Rendering = true;
            try {
                GraphicsProfile p = SelectedProfile;
                List<GraphicsAppRow> rows = this.SelectedIsDefault
                    ? GraphicsLists.ForWindowsDefault(this.Graphics.State.Managed, this.Prefs.Get)
                    : GraphicsLists.ForProfile(p, this.Prefs.Get);
                Grid.Rows.Clear();
                foreach(GraphicsAppRow r in rows) {
                    int n = Grid.Rows.Add();
                    DataGridViewRow row = Grid.Rows[n];
                    row.Tag = r.Key;
                    row.Cells[ColName].Value = r.Name;
                    if(r.Editable)
                        row.Cells[ColChoice].Value = r.Choice;
                    else
                        row.Cells[ColChoice] = new DataGridViewTextBoxCell { Value = r.Choice };
                    row.Cells[ColChoice].ReadOnly = !r.Editable || !this.Editor.CanEdit;
                    row.Cells[ColNow].Value = r.Now;
                    row.Cells[ColKey].Value = r.Key;
                }
                Grid.ClearSelection();
                Grid.Columns[ColChoice].HeaderText = this.SelectedIsDefault ? "Gets back" : "GPU in this profile";
                GrpApps.Text = this.SelectedIsDefault
                    ? GraphicsProfile.WindowsDefaultName + ": the apps OmenMon manages (" + rows.Count + ")"
                    : "Apps of " + this.Selected + " (" + rows.Count + ")";
                LblAppsNote.Text = this.SelectedIsDefault
                    ? "Built in: every app OmenMon manages gets its original GPU preference back (also what uninstalling uses)."
                    : "Changes are saved at once and reach the registry when the profile is applied."
                        + " An app not listed keeps its preference.";
                BtnApply.Text = "Apply " + Shorten(this.Selected, 26) + "…";
            } finally {
                this.Rendering = false;
            }
            SetButtons();
        }

        private static string Shorten(string text, int max) {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        private void RenderActive() {
            string active = this.Editor.Active;
            if(string.IsNullOrEmpty(active))
                LblActive.Text = "Active profile: none (" + GraphicsProfile.WindowsDefaultName + ")";
            else
                LblActive.Text = "Active profile: " + active + (this.Editor.Find(active) == null ? " (no such profile)" : "");
        }

        // Whether the registry is as the active profile has it (Plan changes nothing)
        private void RenderDrift() {
            GraphicsDrift now = null;
            try {
                now = GraphicsStartupCheck.Check(this.Editor.Profiles, this.Editor.Active, this.Graphics.Plan);
            } catch { }
            string text = now != null ? Capital(now.Text) + "." : "";
            if(this.Startup != null && (now == null || now.Text != this.Startup.Text))
                text += (text.Length > 0 ? " " : "") + "At OmenMon's start: " + this.Startup.Text + ".";
            LblDrift.Text = text;
            BtnApplyAgain.Visible = now != null && now.Apps > 0;
        }

        private static string Capital(string text) {
            return string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private void SetButtons() {
            bool edit = this.Editor.CanEdit, profile = !this.SelectedIsDefault && SelectedProfile != null;
            BtnNew.Enabled = edit;
            BtnDuplicate.Enabled = BtnRename.Enabled = BtnDelete.Enabled = edit && profile;
            BtnAddExe.Enabled = BtnAddRunning.Enabled = CmbAddAs.Enabled = edit && profile;
            BtnRemove.Enabled = edit && profile && Grid.SelectedRows.Count > 0;
        }
#endregion

#region Profiles
        private void EventProfileSelected() {
            if(this.Rendering || !(LstProfiles.SelectedItem is GraphicsProfileItem item))
                return;
            this.Selected = item.Name;
            RenderApps();
        }

        // Shows why an edit was not done, if it was not; true if it was
        private bool Done(string why) {
            if(why == null)
                return true;
            MessageBox.Show(this, why, "Graphics apps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void EventNew() {
            string name = GuiGraphicsDialogs.AskName(this, "New profile", "Name of the new profile:",
                this.Editor.FreeName("New profile"), n => this.Editor.CheckName(n));
            if(name != null && Done(this.Editor.New(name))) {
                this.Selected = name;
                RenderAll();
            }
        }

        private void EventDuplicate() {
            GraphicsProfile p = SelectedProfile;
            if(p == null)
                return;
            string name = GuiGraphicsDialogs.AskName(this, "Duplicate " + p.Name, "Name of the copy:",
                this.Editor.FreeName(p.Name + " copy"), n => this.Editor.CheckName(n));
            if(name != null && Done(this.Editor.Duplicate(p.Name, name))) {
                this.Selected = name;
                RenderAll();
            }
        }

        private void EventRename() {
            GraphicsProfile p = SelectedProfile;
            if(p == null)
                return;
            string name = GuiGraphicsDialogs.AskName(this, "Rename " + p.Name, "New name:", p.Name, n => this.Editor.CheckName(n, p.Name));
            if(name != null && Done(this.Editor.Rename(p.Name, name))) {
                this.Selected = name;
                RenderAll();
                RenderDrift();
            }
        }

        private void EventDelete() {
            GraphicsProfile p = SelectedProfile;
            if(p == null)
                return;
            // The active profile is refused straight away, without asking first
            if(string.Equals(p.Name, this.Editor.Active, StringComparison.OrdinalIgnoreCase)) {
                Done(this.Editor.Delete(p.Name));
                return;
            }
            if(!GuiGraphicsDialogs.Confirm(this, "Delete " + p.Name, GraphicsUiText.DeleteConfirmation(p), "Delete the profile"))
                return;
            if(Done(this.Editor.Delete(p.Name)))
                RenderAll();
        }
#endregion

#region Apps
        private GpuChoice AddAs {
            get { return GraphicsChoiceLabel.Parse(CmbAddAs.SelectedItem as string) ?? GpuChoice.PowerSaving; }
        }

        private void AddApps(IList<string> keys) {
            GraphicsProfile p = SelectedProfile;
            if(p == null || keys.Count == 0)
                return;
            if(!Done(this.Editor.AddApps(p.Name, keys, AddAs, out int added)))
                return;
            RenderAll();
            LblLast.Text = added + " app(s) added to " + p.Name + " as " + GpuValue.Name(AddAs)
                + (keys.Count > added ? "; " + (keys.Count - added) + " in it already, left as they are" : "")
                + ". Apply the profile to set them.";
        }

        private void EventAddExe() {
            using(OpenFileDialog dialog = new OpenFileDialog {
                Title = "Add apps to " + this.Selected, Filter = "Programs (*.exe)|*.exe", Multiselect = true,
                CheckFileExists = true, DereferenceLinks = true }) {
                if(dialog.ShowDialog(this) == DialogResult.OK)
                    AddApps(dialog.FileNames);
            }
        }

        private void EventAddRunning() {
            GraphicsProfile p = SelectedProfile;
            if(p == null)
                return;
            List<RunningApp> apps;
            try {
                Cursor = Cursors.WaitCursor;
                List<string> holders = this.LastGpu?.GpuHolders.Where(h => h.AppKey != null).Select(h => h.AppKey).ToList();
                apps = GuiRunningApps.Find(holders, p);
            } catch(Exception ex) {
                Done("The running apps could not be listed: " + ex.Message);
                return;
            } finally {
                Cursor = Cursors.Default;
            }
            if(apps.Count == 0) {
                Done("No running app with a window was found.");
                return;
            }
            AddApps(GuiGraphicsDialogs.PickRunning(this, apps, p.Name));
        }

        private void EventRemove() {
            GraphicsProfile p = SelectedProfile;
            if(p == null)
                return;
            List<string> keys = Grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as string).Where(k => k != null).ToList();
            foreach(string key in keys)
                if(!Done(this.Editor.RemoveApp(p.Name, key)))
                    break;
            RenderAll();
        }

        private void EventChoiceChanged(object sender, DataGridViewCellEventArgs e) {
            if(this.Rendering || e.RowIndex < 0 || e.ColumnIndex != ColChoice)
                return;
            GraphicsProfile p = SelectedProfile;
            string key = Grid.Rows[e.RowIndex].Tag as string;
            GpuChoice? choice = GraphicsChoiceLabel.Parse(Grid.Rows[e.RowIndex].Cells[ColChoice].Value as string);
            if(p == null || key == null || !choice.HasValue)
                return;
            string why = this.Editor.SetChoice(p.Name, key, choice.Value);
            if(why == null) {
                this.Rendered = Signature();
                LblLast.Text = GraphicsLists.Name(key) + ": " + GpuValue.Name(choice.Value) + " in " + p.Name
                    + ". Apply the profile to set it.";
                return;
            }
            // Put the value back, but not while the grid is still in its event: that
            // would be a reentrant call
            BeginInvoke((Action) (() => {
                Done(why);
                Grid.EndEdit();
                RenderApps();
            }));
        }
#endregion

#region Applying
        private void Log(string text) {
            ChangeLog.Append(this.Graphics.State.LogPath, DateTime.Now, "Graphics apps: " + text);
        }

        // Applies the selected profile or Windows default (again: the active profile),
        // after showing what it changes; then the restart dialog
        private void EventApply(bool again) {
            GraphicsProfile target;
            if(again) {
                target = this.Editor.Find(this.Editor.Active);
                if(target == null)
                    return;
            } else {
                target = SelectedProfile;
                if(target == null && !this.SelectedIsDefault)
                    return;
            }

            GraphicsPlan plan;
            try {
                plan = this.Graphics.Plan(target);
            } catch(Exception ex) {
                Done("Could not read the GPU preferences: " + ex.Message);
                return;
            }
            if(!GuiGraphicsDialogs.Confirm(this, "Apply " + plan.TargetName, GraphicsUiText.PlanSummary(plan), "Apply " + Shorten(plan.TargetName, 30)))
                return;

            GraphicsResult result;
            try {
                result = this.Graphics.Apply(target, "Graphics apps window");
            } catch(Exception ex) {
                Done("Applying failed: " + ex.Message);
                return;
            }
            string note = this.Editor.AfterApply(target, result);
            string line = GraphicsUiText.Applied(plan, result, note);
            Log(line);
            LblLast.Text = line;
            RenderAll();
            RenderDrift();
            if(result.Error != null)
                Done("Not all of " + plan.TargetName + " was applied: " + result.Error);
            ScheduleCheck("applying " + plan.TargetName);

            List<string> restart = result.Restart;
            if(restart.Count > 0)
                OfferRestart(restart, plan.TargetName, line);
        }

        private RestartCoordinator NewCoordinator() {
            string logPath = this.Graphics.State.LogPath;
            // RestartText.Describe starts each line with "Graphics apps: "
            return new RestartCoordinator(new WindowsProcessCatalog(), new WindowsProcessCloser(), new UnelevatedLauncher(),
                log: text => ChangeLog.Append(logPath, DateTime.Now, text));
        }

        // Lists the running apps whose GPU changed (WMI: on a worker thread) and shows
        // the restart dialog if any runs
        private void OfferRestart(List<string> apps, string profile, string line) {
            BtnApply.Enabled = BtnApplyAgain.Enabled = false;
            LblLast.Text = line + " Looking for the running apps whose GPU changed…";
            Thread worker = new Thread(() => {
                RestartCoordinator coordinator = NewCoordinator();
                List<RestartEntry> entries = null;
                try {
                    entries = coordinator.Plan(apps);
                } catch { }
                if(entries == null)
                    Log("the running apps could not be listed: none restarted");
                try { BeginInvoke((Action) (() => ShowRestart(coordinator, entries, apps, profile, line))); } catch { }
            }) { IsBackground = true, Name = "OmenMonGraphicsList" };
            worker.Start();
        }

        private void ShowRestart(RestartCoordinator coordinator, List<RestartEntry> entries, List<string> apps, string profile, string line) {
            BtnApply.Enabled = BtnApplyAgain.Enabled = true;
            if(!GraphicsRestartRows.Show(entries, apps)) {
                LblLast.Text = line + " None of the apps whose GPU changed runs: they use it when they start.";
                Log("none of the " + apps.Count + " app(s) whose GPU changed runs");
                return;
            }
            LblLast.Text = line;
            using(GuiFormGraphicsRestart dialog = new GuiFormGraphicsRestart(coordinator, entries, apps, profile, Log, new WindowsSessionControl())) {
                dialog.Finished += () => ScheduleCheck("restarting apps");
                dialog.ShowDialog(this);
            }
            RenderApps();
        }
#endregion

#region Discrete GPU
        // Reads the discrete GPU on a worker thread (PnP state; NVML only while it is on)
        // and shows it
        private void ReadGpu() {
            if(this.Reading)
                return;
            this.Reading = true;
            LblGpu.Text = "Discrete GPU: reading…";
            Thread worker = new Thread(() => {
                string text = Read(out PowerSnapshot s);
                ShowGpu(text, s);
            }) { IsBackground = true, Name = "OmenMonGraphicsGpu" };
            worker.Start();
        }

        private static string Read(out PowerSnapshot s) {
            s = null;
            try {
                using(PowerReader reader = new PowerReader())
                    s = reader.ReadGpuOnly();
                return GraphicsUiText.GpuStatus(s.GpuPowerState, s.GpuDetailsTime.HasValue, s.GpuHolders, AppIdentity.IsSystemComponent);
            } catch(Exception e) {
                return "Discrete GPU: could not be read (" + e.Message + ")";
            }
        }

        // From a worker thread
        private void ShowGpu(string text, PowerSnapshot s) {
            try {
                if(this.IsDisposed || !this.IsHandleCreated)
                    return;
                BeginInvoke((Action) (() => {
                    this.Reading = false;
                    LblGpu.Text = text;
                    if(s != null)
                        this.LastGpu = s;
                }));
            } catch { }
        }

        // ~30 s after an apply (or a restart run), the GPU's state and the apps holding
        // it are shown and logged; a later apply replaces an earlier one still waiting.
        // It is logged even if the window has been closed by then.
        private void ScheduleCheck(string after) {
            int generation = ++this.CheckGeneration;
            Thread worker = new Thread(() => {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                if(Volatile.Read(ref this.CheckGeneration) != generation)
                    return;
                string text = Read(out PowerSnapshot s);
                ChangeLog.Append(this.Graphics.State.LogPath, DateTime.Now, "Graphics apps: 30 s after " + after + ": " + text);
                ShowGpu(text + " (30 s after " + after + ")", s);
            }) { IsBackground = true, Name = "OmenMonGraphicsCheck" };
            worker.Start();
        }
#endregion

    }

}
