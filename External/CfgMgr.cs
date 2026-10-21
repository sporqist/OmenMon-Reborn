  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Runtime.InteropServices;

namespace OmenMon.External {

    // Configuration Manager (cfgmgr32.dll): reads a device's current power state from
    // PnP without touching the device itself, so a runtime-suspended GPU stays asleep
    public static class CfgMgr {

        public const string DllName = "cfgmgr32.dll";
        public const int CR_SUCCESS = 0;
        private const uint DEVPROP_TYPE_BINARY = 0x00001003;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY {
            public Guid fmtid;
            public uint pid;
        }

        // DEVPKEY_Device_PowerData: a CM_POWER_DATA structure
        private static readonly DEVPROPKEY DEVPKEY_Device_PowerData = new DEVPROPKEY {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 32 };

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport(DllName, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key,
            out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);

        // The device's most recent power state: 1 = D0 (on) ... 4 = D3 (off), 0 = unknown;
        // -1 if the device or the property cannot be read
        public static int GetDevicePowerState(string instanceId) {
            if(string.IsNullOrEmpty(instanceId))
                return -1;
            try {
                if(CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS)
                    return -1;
                DEVPROPKEY key = DEVPKEY_Device_PowerData;
                byte[] buffer = new byte[64];
                uint size = (uint) buffer.Length;
                if(CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0) != CR_SUCCESS
                    || type != DEVPROP_TYPE_BINARY || size < 8)
                    return -1;
                // CM_POWER_DATA: ULONG PD_Size, DEVICE_POWER_STATE PD_MostRecentPowerState, ...
                return BitConverter.ToInt32(buffer, 4);
            } catch {
                return -1;
            }
        }

    }

}
