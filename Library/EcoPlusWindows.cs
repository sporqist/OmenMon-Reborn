  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Collections.Generic;
using System.Linq;
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

    // Display refresh rate of the internal panel, changed dynamically (not saved as the
    // display's mode). The panel is found in the display configuration by its output
    // technology (internal, embedded DisplayPort, LVDS or UDI) and named by its monitor
    // device path, which stays the same when displays are added or removed, while the
    // GDI name it is reached through (\\.\DISPLAYn) can change. If the display
    // configuration cannot be read, the primary display stands in, named by its GDI name.
    public sealed class DisplayRate : IDisplayRate {

        public string Panel() {
            List<Target> targets = Targets();
            if(targets == null)
                return PrimaryGdiName();
            Target panel = targets.FirstOrDefault(t => t.Internal);
            if(panel == null)
                return null;
            return !string.IsNullOrEmpty(panel.DevicePath) ? panel.DevicePath : panel.GdiName;
        }

        public int Current(string display) {
            string gdi = Resolve(display);
            if(gdi == null)
                return 0;
            User32.DEVMODE d = NewDevMode();
            return User32.EnumDisplaySettings(gdi, User32.ENUM_CURRENT_SETTINGS, ref d) != 0 ? d.dmDisplayFrequency : 0;
        }

        public bool SetDynamic(string display, int hz) {
            try {
                string gdi = Resolve(display);
                if(gdi == null)
                    return false;
                User32.DEVMODE d = NewDevMode();
                if(User32.EnumDisplaySettings(gdi, User32.ENUM_CURRENT_SETTINGS, ref d) == 0)
                    return false;
                if(d.dmDisplayFrequency == hz)
                    return true;
                d.dmDisplayFrequency = hz;
                d.dmFields |= DM_DISPLAYFREQUENCY;
                // Anything but success is a failure (a mode the display does not have
                // gives DISP_CHANGE_BADMODE, not DISP_CHANGE_FAILED)
                if(ChangeDisplaySettingsEx(gdi, ref d, IntPtr.Zero, User32.CDS_TEST, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL)
                    return false;
                return ChangeDisplaySettingsEx(gdi, ref d, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
            } catch {
                return false;
            }
        }

        // How many displays are active: changes when one is plugged in or out (a refresh
        // rate change does not), which is when Windows puts the saved modes back
        public static int ActiveCount() {
            List<Target> targets = Targets();
            return targets != null ? targets.Count : -1;
        }

        // The GDI name a display is reached through now; null if it is not active
        private static string Resolve(string display) {
            if(string.IsNullOrEmpty(display))
                return null;
            if(display.StartsWith(@"\\.\", StringComparison.Ordinal)) {
                User32.DEVMODE d = NewDevMode();
                return User32.EnumDisplaySettings(display, User32.ENUM_CURRENT_SETTINGS, ref d) != 0 ? display : null;
            }
            List<Target> targets = Targets();
            Target t = targets?.FirstOrDefault(x => string.Equals(x.DevicePath, display, StringComparison.OrdinalIgnoreCase));
            return t?.GdiName;
        }

        private static User32.DEVMODE NewDevMode() {
            User32.DEVMODE d = new User32.DEVMODE();
            d.dmDeviceName = new string(new char[32]);
            d.dmFormName = new string(new char[32]);
            d.dmSize = (short) Marshal.SizeOf(d);
            return d;
        }

        private static string PrimaryGdiName() {
            try {
                DISPLAY_DEVICE dev = new DISPLAY_DEVICE();
                dev.cb = Marshal.SizeOf(dev);
                for(uint i = 0; EnumDisplayDevices(null, i, ref dev, 0); i++) {
                    if((dev.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                        return dev.DeviceName;
                    dev.cb = Marshal.SizeOf(dev);
                }
            } catch { }
            return null;
        }

        private sealed class Target {
            public string GdiName, DevicePath;
            public bool Internal;
        }

        // The active display paths; null if the display configuration cannot be read
        private static List<Target> Targets() {
            try {
                for(int attempt = 0; attempt < 3; attempt++) {
                    if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                        return null;
                    DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                    DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                    int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                    if(r == ERROR_INSUFFICIENT_BUFFER)
                        continue;   // the configuration changed in between
                    if(r != 0)
                        return null;
                    List<Target> targets = new List<Target>();
                    for(int i = 0; i < pathCount && i < paths.Length; i++) {
                        DISPLAYCONFIG_SOURCE_DEVICE_NAME source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                        source.header.size = (uint) Marshal.SizeOf(source);
                        source.header.adapterId = paths[i].sourceAdapterId;
                        source.header.id = paths[i].sourceId;
                        if(DisplayConfigGetDeviceInfo(ref source) != 0)
                            continue;
                        DISPLAYCONFIG_TARGET_DEVICE_NAME target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                        target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                        target.header.size = (uint) Marshal.SizeOf(target);
                        target.header.adapterId = paths[i].targetAdapterId;
                        target.header.id = paths[i].targetId;
                        bool named = DisplayConfigGetDeviceInfo(ref target) == 0;
                        uint tech = paths[i].outputTechnology;
                        targets.Add(new Target {
                            GdiName = source.viewGdiDeviceName,
                            DevicePath = named ? target.monitorDevicePath : null,
                            Internal = tech == OUTPUT_TECHNOLOGY_INTERNAL || tech == OUTPUT_TECHNOLOGY_LVDS
                                || tech == OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED || tech == OUTPUT_TECHNOLOGY_UDI_EMBEDDED });
                    }
                    return targets;
                }
            } catch { }
            return null;
        }

#region Display configuration (user32.dll)
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DM_DISPLAYFREQUENCY = 0x00400000;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const uint OUTPUT_TECHNOLOGY_LVDS = 6;
        private const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
        private const uint OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
        private const uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID {
            public uint LowPart;
            public int HighPart;
        }

        // DISPLAYCONFIG_PATH_INFO with its source and target info inlined (72 bytes)
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO {
            public LUID sourceAdapterId;
            public uint sourceId, sourceModeInfoIdx, sourceStatusFlags;
            public LUID targetAdapterId;
            public uint targetId, targetModeInfoIdx, outputTechnology, rotation, scaling;
            public uint refreshNumerator, refreshDenominator, scanLineOrdering;
            public int targetAvailable;
            public uint targetStatusFlags;
            public uint flags;
        }

        // Only the header is read; the union after it makes the structure 64 bytes
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO {
            public uint infoType, id;
            public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER {
            public uint type, size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags, outputTechnology;
            public ushort edidManufactureId, edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DISPLAY_DEVICE {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport(User32.DllName)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport(User32.DllName)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
            ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

        [DllImport(User32.DllName)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

        [DllImport(User32.DllName)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

        // ANSI, like User32.DEVMODE and EnumDisplaySettings
        [DllImport(User32.DllName, CharSet = CharSet.Ansi, EntryPoint = "ChangeDisplaySettingsExA")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref User32.DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

        [DllImport(User32.DllName, CharSet = CharSet.Ansi, EntryPoint = "EnumDisplayDevicesA")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);
#endregion

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
