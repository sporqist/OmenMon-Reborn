  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.Runtime.InteropServices;

namespace OmenMon.External {

    // NVIDIA Management Library (nvml.dll, installed with the display driver in System32).
    // Used read-only for the power panel: board power, performance state and the
    // processes that hold a context on the GPU. Any NVML call wakes a GPU that is
    // runtime-suspended (D3cold), so callers check the PnP power state first
    // (CfgMgr.GetDevicePowerState) and only query NVML when the GPU is already on.
    public static class Nvml {

        public const string DllName = "nvml.dll";
        public const int NVML_SUCCESS = 0;
        public const int NVML_ERROR_INSUFFICIENT_SIZE = 7;
        public const int NVML_TEMPERATURE_GPU = 0;

        // nvmlProcessInfo_t (v2/v3 layout): 24 bytes
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInfo {
            public uint Pid;
            public ulong UsedGpuMemory;
            public uint GpuInstanceId;
            public uint ComputeInstanceId;
        }

        [DllImport(DllName, EntryPoint = "nvmlInit_v2")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int Init();

        [DllImport(DllName, EntryPoint = "nvmlShutdown")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int Shutdown();

        [DllImport(DllName, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetHandleByIndex(uint index, out IntPtr device);

        // Board power draw [mW]
        [DllImport(DllName, EntryPoint = "nvmlDeviceGetPowerUsage")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetPowerUsage(IntPtr device, out uint milliwatts);

        // Performance state: 0 (P0, maximum) to 15 (P15, minimum); 32 = unknown
        [DllImport(DllName, EntryPoint = "nvmlDeviceGetPerformanceState")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetPerformanceState(IntPtr device, out int pstate);

        [DllImport(DllName, EntryPoint = "nvmlDeviceGetTemperature")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetTemperature(IntPtr device, int sensor, out uint celsius);

        [DllImport(DllName, EntryPoint = "nvmlDeviceGetComputeRunningProcesses_v3")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetComputeRunningProcesses(IntPtr device, ref uint count, [Out] ProcessInfo[] infos);

        [DllImport(DllName, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses_v3")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int DeviceGetGraphicsRunningProcesses(IntPtr device, ref uint count, [Out] ProcessInfo[] infos);

    }

}
