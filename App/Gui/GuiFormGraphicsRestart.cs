  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // The restart dialog, after a profile was applied: the running apps whose GPU
    // changed, one row each, checked if they can be restarted (greyed with the reason
    // if not). Nothing is closed, ended or restarted, and the session is not ended,
    // without a click here: "Restart checked apps" closes each checked app politely and
    // starts it again unelevated (RestartCoordinator, on a worker thread); an app still
    // running after the polite close is ended only if the user says so. Signing out and
    // restarting Windows each ask once more, naming what happens.
    internal class GuiFormGraphicsRestart : Form {

        private readonly RestartCoordinator Coordinator;
        private readonly List<RestartRow> Rows;
        private readonly string Profile;
        private readonly Action<string> Log;
        private readonly ISessionControl Session;

        private Label LblIntro, LblProgress;
        private ListView LstApps;
        private ProgressBar BarProgress;
        private TextBox TxtResults;
        private Button BtnRestart, BtnLater, BtnSignOut, BtnReboot;

        private volatile bool Running;
        private bool Ran, Answered;

        // Called (on the UI thread) when a restart run has finished
        public event Action Finished;

        // entries: RestartCoordinator.Plan (null: the running apps could not be listed);
        // appKeys: the apps whose GPU changed; log: a line for the graphics log
        public GuiFormGraphicsRestart(RestartCoordinator coordinator, List<RestartEntry> entries, List<string> appKeys,
            string profile, Action<string> log, ISessionControl session) {
            this.Coordinator = coordinator;
            this.Rows = GraphicsRestartRows.Build(entries, appKeys);
            this.Profile = profile;
            this.Log = log ?? (s => { });
            this.Session = session;
            BuildUi(entries == null, appKeys.Count - this.Rows.Count(r => r.Entry != null));
            this.FormClosing += EventClosing;
        }

        private void BuildUi(bool cannotList, int notRunning) {
            this.Text = "Restart apps for " + this.Profile;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(760, 480);
            this.Font = new Font("Segoe UI", 9F);

            string intro = this.Profile + " is applied. A running app uses its new GPU only once it starts again. "
                + "Nothing is closed until you click \"Restart checked apps\": each checked app is asked to close and "
                + "given 15 s, then started again (never as administrator); if it is still running, you decide.";
            if(cannotList)
                intro = this.Profile + " is applied, but the running apps could not be listed: restart the apps below yourself"
                    + " if they run, or sign out or restart Windows.";
            else if(notRunning > 0)
                intro += " " + notRunning + " app(s) whose GPU changed are not running: they use it when they start.";
            LblIntro = new Label { Text = intro, Location = new Point(12, 10), Size = new Size(736, 52) };

            LstApps = new ListView {
                View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = true, MultiSelect = false,
                Location = new Point(12, 66), Size = new Size(736, 190) };
            LstApps.Columns.Add("App", 200);
            LstApps.Columns.Add("Processes", 70);
            LstApps.Columns.Add("Note", 440);
            foreach(RestartRow r in this.Rows) {
                ListViewItem item = new ListViewItem(new[] { r.Name, r.Entry != null ? r.Processes.ToString() : "?", r.Note }) {
                    Tag = r, Checked = r.Checked, ToolTipText = r.AppKey };
                if(!r.Checkable)
                    item.ForeColor = SystemColors.GrayText;
                LstApps.Items.Add(item);
            }
            LstApps.ShowItemToolTips = true;
            LstApps.ItemCheck += EventItemCheck;

            LblProgress = new Label { Location = new Point(12, 262), Size = new Size(736, 20) };
            BarProgress = new ProgressBar { Location = new Point(12, 284), Size = new Size(736, 14), Maximum = Math.Max(1, this.Rows.Count) };
            TxtResults = new TextBox {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                Location = new Point(12, 304), Size = new Size(736, 130), Font = new Font("Consolas", 9F) };

            BtnRestart = new Button { Text = "Restart checked apps", Location = new Point(12, 444), Size = new Size(150, 26),
                Enabled = this.Rows.Any(r => r.Checkable) };
            BtnLater = new Button { Text = "Don't restart now (apps switch at their next start)", Location = new Point(168, 444), Size = new Size(320, 26) };
            BtnSignOut = new Button { Text = "Sign out…", Location = new Point(536, 444), Size = new Size(100, 26) };
            BtnReboot = new Button { Text = "Restart Windows…", Location = new Point(640, 444), Size = new Size(108, 26) };
            BtnRestart.Click += (s, e) => EventRestart();
            BtnLater.Click += (s, e) => Close();
            BtnSignOut.Click += (s, e) => EventEndSession(SessionEnd.SignOut);
            BtnReboot.Click += (s, e) => EventEndSession(SessionEnd.RestartWindows);

            this.Controls.AddRange(new Control[] { LblIntro, LstApps, LblProgress, BarProgress, TxtResults, BtnRestart, BtnLater, BtnSignOut, BtnReboot });
            this.CancelButton = BtnLater;
        }

        // Only an app that can be restarted can be checked, and nothing changes while
        // (or after) restarting
        private void EventItemCheck(object sender, ItemCheckEventArgs e) {
            RestartRow r = LstApps.Items[e.Index].Tag as RestartRow;
            if(r == null || !r.Checkable || this.Running || this.Ran)
                e.NewValue = e.CurrentValue;
        }

        private void EventClosing(object sender, FormClosingEventArgs e) {
            if(this.Running) {
                e.Cancel = true;
                LblProgress.Text = "Restarting: this window closes once it is done (up to 15 s per app to close).";
                return;
            }
            if(!this.Ran && !this.Answered) {
                this.Answered = true;
                this.Log("not restarted now: " + string.Join(", ", this.Rows.Select(r => r.Name))
                    + " switch at their next start");
            }
        }

        private void EventRestart() {
            foreach(ListViewItem item in LstApps.Items)
                if(item.Tag is RestartRow r)
                    r.Checked = item.Checked && r.Checkable;
            List<RestartEntry> confirmed = GraphicsRestartRows.Confirmed(this.Rows);
            if(confirmed.Count == 0) {
                LblProgress.Text = "No app is checked.";
                return;
            }
            int unchecked_ = this.Rows.Count(r => r.Checkable && !r.Checked);
            if(unchecked_ > 0)
                this.Log(unchecked_ + " app(s) left unchecked: they switch at their next start");

            this.Running = true;
            this.Answered = true;
            SetButtons();
            BarProgress.Maximum = confirmed.Count;
            BarProgress.Value = 0;
            TxtResults.Text = "";
            Thread worker = new Thread(() => {
                List<RestartResult> results;
                try {
                    results = GraphicsRestartRun.Run(confirmed, this.Coordinator.Restart, this.Coordinator.Decide,
                        AskEnd, Progress, this.Log);
                } catch(Exception ex) {
                    this.Log("restart failed: " + ex.Message);
                    results = new List<RestartResult>();
                }
                try { BeginInvoke((Action) (() => Done(results))); } catch { }
            }) { IsBackground = true, Name = "OmenMonGraphicsRestart" };
            worker.Start();
        }

        // On the worker thread: the user's answer for an app still running (asked on
        // the UI thread, which waits for it)
        private bool AskEnd(RestartResult r) {
            return (bool) Invoke((Func<bool>) (() => GuiGraphicsDialogs.Ask(this, "Still running",
                GraphicsUiText.DecisionQuestion(r), "End it", "Leave it running")));
        }

        // On the worker thread
        private void Progress(int done, int count, RestartEntry e) {
            try {
                BeginInvoke((Action) (() => {
                    BarProgress.Value = Math.Min(done, BarProgress.Maximum);
                    LblProgress.Text = e != null ? "Restarting " + e.DisplayName + " (" + (done + 1) + " of " + count + ")…" : "Done.";
                }));
            } catch { }
        }

        private void Done(List<RestartResult> results) {
            this.Running = false;
            this.Ran = true;
            StringBuilder sb = new StringBuilder();
            foreach(RestartResult r in results)
                sb.Append(RestartText.Describe(r)).Append("\r\n");
            TxtResults.Text = sb.ToString();
            int restarted = results.Count(r => r.Outcome == RestartOutcome.Restarted);
            LblProgress.Text = "Done: " + restarted + " of " + results.Count + " app(s) restarted. Every result is in "
                + GraphicsState.LogName + ".";
            BtnLater.Text = "Close";
            SetButtons();
            try { Finished?.Invoke(); } catch { }
        }

        private void SetButtons() {
            BtnRestart.Enabled = !this.Running && !this.Ran && this.Rows.Any(r => r.Checkable);
            BtnLater.Enabled = !this.Running;
            BtnSignOut.Enabled = BtnReboot.Enabled = !this.Running;
        }

        private void EventEndSession(SessionEnd how) {
            if(this.Running)
                return;
            string title = how == SessionEnd.SignOut ? "Sign out" : "Restart Windows";
            string doIt = how == SessionEnd.SignOut ? "Sign out now" : "Restart Windows now";
            if(GraphicsSession.Run(how, this.Profile, text => GuiGraphicsDialogs.Confirm(this, title, text, doIt),
                this.Session, this.Log, out string error)) {
                this.Answered = true;
                LblProgress.Text = how == SessionEnd.SignOut ? "Signing out…" : "Restarting Windows…";
            } else if(error != null)
                MessageBox.Show(this, title + " did not start: " + error, Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

    }

}
