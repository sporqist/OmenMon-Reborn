// SPIKE S5 (tray half): the smallest WinForms tray app, to measure what the unelevated tray
// costs while only its icon is shown (PLAN.md section 2: target <= 30 MB private). The plan
// keeps WinForms for the tray first, because the existing UI is WinForms.
//
// It touches no hardware, opens no pipe and starts nothing. Exit from the icon's menu.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace OmenMon.Spikes.Tray {

    internal static class Program {

        [STAThread]
        private static void Main() {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.Run(new TrayContext());
        }

    }

    internal sealed class TrayContext : ApplicationContext {

        private readonly NotifyIcon icon;

        public TrayContext() {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            this.icon = new NotifyIcon {
                Icon = SystemIcons.Application,
                Text = "OmenCtrl spike S5 (tray only)",
                ContextMenuStrip = menu,
                Visible = true };
        }

        protected override void ExitThreadCore() {
            this.icon.Visible = false;
            this.icon.Dispose();
            base.ExitThreadCore();
        }

    }

}
