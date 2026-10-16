  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using OmenMon.External;

namespace OmenMon.Library {

    // Active power scheme and its processor boost setting (powrprof.dll)
    public sealed class WindowsPowerPlan : IPowerPlan {

        public Guid ActiveScheme() {
            try {
                if(PowrProf.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
                    return Guid.Empty;
                try {
                    return (Guid) Marshal.PtrToStructure(ptr, typeof(Guid));
                } finally {
                    PowrProf.LocalFree(ptr);
                }
            } catch {
                return Guid.Empty;
            }
        }

        public string FriendlyName(Guid scheme) {
            try {
                uint size = 0;
                PowrProf.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if(size == 0)
                    return null;
                byte[] buffer = new byte[size];
                if(PowrProf.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                    return null;
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            } catch {
                return null;
            }
        }

        public uint? ReadBoostAc(Guid scheme) {
            Guid sub = PowrProf.GUID_PROCESSOR_SETTINGS_SUBGROUP, setting = PowrProf.GUID_PROCESSOR_PERF_BOOST_MODE;
            try {
                return PowrProf.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == 0 ? v : (uint?) null;
            } catch {
                return null;
            }
        }

        public uint? ReadBoostDc(Guid scheme) {
            Guid sub = PowrProf.GUID_PROCESSOR_SETTINGS_SUBGROUP, setting = PowrProf.GUID_PROCESSOR_PERF_BOOST_MODE;
            try {
                return PowrProf.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == 0 ? v : (uint?) null;
            } catch {
                return null;
            }
        }

        public void WriteBoostDc(Guid scheme, uint value) {
            Guid sub = PowrProf.GUID_PROCESSOR_SETTINGS_SUBGROUP, setting = PowrProf.GUID_PROCESSOR_PERF_BOOST_MODE;
            try {
                PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
                // A changed value of the active scheme takes effect once it is set active again
                if(ActiveScheme() == scheme)
                    PowrProf.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            } catch { }
        }

        // PERFBOOSTMODE values
        public static string BoostModeName(uint? value) {
            switch(value) {
                case 0: return "disabled";
                case 1: return "enabled";
                case 2: return "aggressive";
                case 3: return "efficient enabled";
                case 4: return "efficient aggressive";
                case 5: return "aggressive at guaranteed";
                case 6: return "efficient aggressive at guaranteed";
                case null: return "?";
                default: return value.ToString();
            }
        }

    }

    // Per-app GPU preference, as set in Windows Settings > Display > Graphics
    // (HKCU\Software\Microsoft\DirectX\UserGpuPreferences: "GpuPreference=1;" power
    // saving, "=2;" high performance). OmenMon runs elevated as the same user, so
    // HKCU is that user's hive.
    public sealed class RegistryGpuPreferences : IGpuPreferences {

        public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        public string Get(string app) {
            try {
                using(RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key?.GetValue(app) as string;
            } catch {
                return null;
            }
        }

        public void Set(string app, string value) {
            using(RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(app, value, RegistryValueKind.String);
        }

        public void Remove(string app) {
            using(RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                key?.DeleteValue(app, false);
        }

        // A readable form of a preference value
        public static string Describe(string value) {
            if(value == null) return "default";
            if(value.Contains("GpuPreference=1;")) return "power saving";
            if(value.Contains("GpuPreference=2;")) return "high performance";
            if(value.Contains("GpuPreference=0;")) return "let Windows decide";
            return value;
        }

    }

    // Display refresh rate: changed dynamically (not saved as the display's mode)
    public sealed class DisplayRate : IDisplayRate {

        public int Current() {
            return Os.GetRefreshRate();
        }

        public bool SetDynamic(int hz) {
            try {
                User32.DEVMODE d = new User32.DEVMODE();
                d.dmDeviceName = new string(new char[32]);
                d.dmFormName = new string(new char[32]);
                d.dmSize = (short) Marshal.SizeOf(d);
                if(User32.EnumDisplaySettings(null, User32.ENUM_CURRENT_SETTINGS, ref d) == 0)
                    return false;
                if(d.dmDisplayFrequency == hz)
                    return true;
                d.dmDisplayFrequency = hz;
                if(User32.ChangeDisplaySettings(ref d, User32.CDS_TEST) == User32.DISP_CHANGE_FAILED)
                    return false;
                return User32.ChangeDisplaySettings(ref d, 0) == 0;
            } catch {
                return false;
            }
        }

    }

    // Identifies a process the way the GPU preference store names apps: the
    // Application User Model ID for packaged apps (e.g. Windows Terminal), else the
    // full executable path
    public static class AppIdentity {

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

        // Windows components (dwm.exe, csrss.exe, ...) hold a GPU context on behalf of the
        // apps they compose for; a GPU preference on them does nothing useful, so they are
        // never offered. Explorer lives in the Windows folder itself and is a normal app.
        public static bool IsSystemComponent(string path) {
            if(string.IsNullOrEmpty(path))
                return true;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if(string.IsNullOrEmpty(windows))
                windows = @"C:\Windows";
            string p = path.Replace('/', '\\');
            return p.StartsWith(windows + @"\System32\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(windows + @"\SysWOW64\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(windows + @"\SystemApps\", StringComparison.OrdinalIgnoreCase);
        }

        // (preference key, executable path); nulls if the process cannot be opened
        public static void Resolve(int pid, out string key, out string path) {
            key = null;
            path = null;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if(h == IntPtr.Zero)
                return;
            try {
                StringBuilder sb = new StringBuilder(1024);
                uint size = (uint) sb.Capacity;
                if(QueryFullProcessImageNameW(h, 0, sb, ref size))
                    path = sb.ToString();
                StringBuilder id = new StringBuilder(256);
                uint length = (uint) id.Capacity;
                key = GetApplicationUserModelId(h, ref length, id) == 0 && id.Length > 0 ? id.ToString() : path;
            } finally {
                CloseHandle(h);
            }
        }

    }

}
