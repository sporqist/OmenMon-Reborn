  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OmenMon.Library;

namespace OmenMon.AppGui {

    // The small dialogs of Graphics apps, built in code: a name, a confirmation that
    // names what happens, a question with two answers, picking running apps and picking
    // a profile. A confirmation and a question default to the answer that changes nothing.
    internal static class GuiGraphicsDialogs {

        private static Form NewDialog(string title, int width, int height) {
            return new Form {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(width, height), Font = new Font("Segoe UI", 9F) };
        }

        // A name, or null if cancelled; check says why a name cannot be taken (null: it can)
        public static string AskName(IWin32Window owner, string title, string prompt, string initial, Func<string, string> check) {
            using(Form f = NewDialog(title, 420, 132)) {
                Label lbl = new Label { Text = prompt, Location = new Point(12, 12), Size = new Size(396, 20) };
                TextBox txt = new TextBox { Text = initial ?? "", Location = new Point(12, 34), Size = new Size(396, 24) };
                Label err = new Label { Location = new Point(12, 62), Size = new Size(396, 30), ForeColor = Color.Firebrick };
                Button ok = new Button { Text = "OK", Location = new Point(236, 98), Size = new Size(84, 26) };
                Button cancel = new Button { Text = "Cancel", Location = new Point(324, 98), Size = new Size(84, 26), DialogResult = DialogResult.Cancel };
                ok.Click += (s, e) => {
                    string why = check?.Invoke(txt.Text);
                    if(why != null)
                        err.Text = why;
                    else
                        f.DialogResult = DialogResult.OK;
                };
                f.Controls.AddRange(new Control[] { lbl, txt, err, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += (s, e) => txt.SelectAll();
                return f.ShowDialog(owner) == DialogResult.OK ? txt.Text : null;
            }
        }

        // A text that may be long, with a button named for what it does and Cancel,
        // which is the default. True if the user clicked the first one.
        public static bool Confirm(IWin32Window owner, string title, string text, string doIt) {
            using(Form f = NewDialog(title, 640, 380)) {
                TextBox txt = new TextBox {
                    Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                    Location = new Point(12, 12), Size = new Size(616, 318), BackColor = SystemColors.Window };
                Button yes = new Button { Text = doIt, Location = new Point(12, 342), Size = new Size(Math.Max(120, TextRenderer.MeasureText(doIt, f.Font).Width + 24), 26),
                    DialogResult = DialogResult.OK };
                Button no = new Button { Text = "Cancel", Location = new Point(538, 342), Size = new Size(90, 26), DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { txt, yes, no });
                f.CancelButton = no;
                f.AcceptButton = no;
                f.Shown += (s, e) => { no.Focus(); txt.SelectionLength = 0; };
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }

        // A question with two answers; true for the first. The second is the default.
        public static bool Ask(IWin32Window owner, string title, string text, string first, string second) {
            using(Form f = NewDialog(title, 520, 190)) {
                Label lbl = new Label { Text = text, Location = new Point(12, 12), Size = new Size(496, 132) };
                Button a = new Button { Text = first, Location = new Point(12, 154), Size = new Size(150, 26), DialogResult = DialogResult.Yes };
                Button b = new Button { Text = second, Location = new Point(338, 154), Size = new Size(170, 26), DialogResult = DialogResult.No };
                f.Controls.AddRange(new Control[] { lbl, a, b });
                f.AcceptButton = b;
                f.CancelButton = b;
                f.ControlBox = false;
                f.Shown += (s, e) => b.Focus();
                return f.ShowDialog(owner) == DialogResult.Yes;
            }
        }

        // The keys of the running apps the user checks (empty if cancelled)
        public static List<string> PickRunning(IWin32Window owner, List<RunningApp> apps, string profile) {
            using(Form f = NewDialog("Add running app", 640, 380)) {
                Label lbl = new Label {
                    Text = "Running apps with a window. Check the ones to add to " + profile + ":",
                    Location = new Point(12, 12), Size = new Size(616, 20) };
                CheckedListBox lst = new CheckedListBox { Location = new Point(12, 36), Size = new Size(616, 296), CheckOnClick = true, IntegralHeight = false };
                foreach(RunningApp a in apps)
                    lst.Items.Add(a, false);
                Button ok = new Button { Text = "Add checked", Location = new Point(444, 342), Size = new Size(90, 26), DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "Cancel", Location = new Point(538, 342), Size = new Size(90, 26), DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lbl, lst, ok, cancel });
                f.CancelButton = cancel;
                if(f.ShowDialog(owner) != DialogResult.OK)
                    return new List<string>();
                return lst.CheckedItems.Cast<RunningApp>().Select(a => a.Key).ToList();
            }
        }

        // One of the choices, or null if cancelled; note: the line shown under the choice
        public static string PickProfile(IWin32Window owner, string title, string prompt, List<string> choices, int selected,
            Func<string, string> note) {
            using(Form f = NewDialog(title, 440, 160)) {
                Label lbl = new Label { Text = prompt, Location = new Point(12, 12), Size = new Size(416, 20) };
                ComboBox cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 36), Size = new Size(416, 24) };
                cmb.Items.AddRange(choices.Cast<object>().ToArray());
                Label lblNote = new Label { Location = new Point(12, 66), Size = new Size(416, 52), ForeColor = SystemColors.GrayText };
                cmb.SelectedIndexChanged += (s, e) => lblNote.Text = note?.Invoke(cmb.SelectedItem as string) ?? "";
                cmb.SelectedIndex = Math.Max(0, Math.Min(selected, choices.Count - 1));
                Button ok = new Button { Text = "Add and apply", Location = new Point(244, 126), Size = new Size(100, 26), DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "Cancel", Location = new Point(348, 126), Size = new Size(80, 26), DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { lbl, cmb, lblNote, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? cmb.SelectedItem as string : null;
            }
        }

    }

    // The running apps of the interactive user that have a visible top-level window,
    // named the way the GPU preference store names them (AUMID or executable path).
    // Reads only: nothing is opened for more than querying.
    internal static class GuiRunningApps {

        public static List<RunningApp> Find(ICollection<string> holders, GraphicsProfile profile) {
            int own, session;
            using(Process p = Process.GetCurrentProcess()) {
                own = p.Id;
                session = p.SessionId;
            }
            List<RunningApp> found = new List<RunningApp>();
            foreach(int pid in ProcessHandles.MainWindows().Keys) {
                if(pid == own || ProcessHandles.SessionOf(pid) != session)
                    continue;
                try {
                    AppIdentity.Resolve(pid, out string key, out string path);
                    if(key != null)
                        found.Add(new RunningApp { Pid = pid, Key = key, Path = path, Name = GraphicsLists.Name(key) });
                } catch { }
            }
            return GraphicsLists.Running(found, own, AppIdentity.IsSystemComponent, holders, profile);
        }

    }

    // Signing out and restarting Windows, never forced (see Os.EndSession)
    internal sealed class WindowsSessionControl : ISessionControl {

        public bool End(SessionEnd how, out string error) {
            error = null;
            if(Os.EndSession(how == SessionEnd.RestartWindows, out int code))
                return true;
            error = "Windows refused (error " + code + ")";
            return false;
        }

    }

}
