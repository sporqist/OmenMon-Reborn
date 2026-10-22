  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Linq;
using Microsoft.Management.Infrastructure;
using OmenMon.External;
using OmenMon.Hardware.Platform;

namespace OmenMon.Library {

    // The NVIDIA dGPU's temperature from NVML, for a board without a GPU register (an
    // unknown board running BIOS-only). NVML is only asked while PnP reports the GPU in
    // D0: NVML would wake a parked GPU (the same rule as the Power window, PowerInfo).
    // It is opened and closed around each reading, at most every MinInterval; in between
    // the last reading is given again while the GPU stays awake.
    public sealed class NvmlGpuTemperature : IGpuTemperatureSource {

        public static readonly NvmlGpuTemperature Instance = new NvmlGpuTemperature();

        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

        private readonly object sync = new object();
        private bool lookedUp;
        private string instanceId;
        private bool failed;
        private DateTime lastAt;
        private int last;

        private NvmlGpuTemperature() { }

        public bool TryRead(out int celsius) {
            celsius = 0;
            lock(this.sync) {
                if(this.failed)
                    return false;
                if(!this.lookedUp) {
                    this.lookedUp = true;
                    try {
                        using(CimSession cim = CimSession.Create(null)) {
                            CimInstance d = cim.QueryInstances(@"root\cimv2", "WQL",
                                "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND DeviceID LIKE 'PCI\\\\VEN_10DE%'").FirstOrDefault();
                            this.instanceId = d?.CimInstanceProperties["DeviceID"].Value as string;
                        }
                    } catch { }
                }
                if(this.instanceId == null)
                    return false;

                // Parked (or the state cannot be read): no reading
                if(CfgMgr.GetDevicePowerState(this.instanceId) != 1)
                    return false;

                DateTime now = DateTime.UtcNow;
                if(this.last > 0 && now - this.lastAt < MinInterval) {
                    celsius = this.last;
                    return true;
                }

                bool up = false;
                try {
                    if(Nvml.Init() != Nvml.NVML_SUCCESS) {
                        this.failed = true;
                        return false;
                    }
                    up = true;
                    if(Nvml.DeviceGetHandleByIndex(0, out IntPtr device) != Nvml.NVML_SUCCESS)
                        return false;
                    if(Nvml.DeviceGetTemperature(device, Nvml.NVML_TEMPERATURE_GPU, out uint t) != Nvml.NVML_SUCCESS || t == 0 || t > 255)
                        return false;
                    this.last = (int) t;
                    this.lastAt = now;
                    celsius = this.last;
                    return true;
                } catch(DllNotFoundException) {
                    this.failed = true;
                    return false;
                } catch(EntryPointNotFoundException) {
                    this.failed = true;
                    return false;
                } catch {
                    return false;
                } finally {
                    if(up)
                        try { Nvml.Shutdown(); } catch { }
                }
            }
        }

    }

}
