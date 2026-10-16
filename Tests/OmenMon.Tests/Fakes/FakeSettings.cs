  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;

namespace OmenMon.Tests.Fakes {

    // System information for an 8BAC on AC power; GPU power writes are recorded
    public sealed class FakeSettings : ISettings {

        public bool FullPower { get; set; } = true;
        public List<BiosData.GpuPowerData> GpuPowerWrites { get; } = new List<BiosData.GpuPowerData>();

        private BiosData.GpuPowerData gpuPower = new BiosData.GpuPowerData(BiosData.GpuPowerLevel.Max);

        public bool IsFullPower() { return this.FullPower; }
        public bool IsFullPowerConfirmed() { return this.FullPower; }

        public BiosData.GpuMode? GpuMode { get { return BiosData.GpuMode.Hybrid; } }
        public BiosData.GpuPowerData? GpuPower { get { return this.gpuPower; } }
        public BiosData.SystemData? SystemData { get { return null; } }

        public BiosData.AdapterStatus GetAdapterStatus() { return BiosData.AdapterStatus.MeetsRequirement; }
        public string GetBornDate() { return "20230101"; }
        public byte GetDefaultCpuPowerLimit4() { return 0; }
        public BiosData.SystemData GetSystemData() { return default(BiosData.SystemData); }
        public BiosData.Throttling GetThrottling() { return BiosData.Throttling.Default; }

        public BiosData.GpuMode GetGpuMode(bool forceUpdate = false) { return BiosData.GpuMode.Hybrid; }
        public bool GetGpuModeSupport() { return false; }
        public void SetGpuMode(BiosData.GpuMode value) { }
        public BiosData.GpuCustomTgp GetGpuCustomTgp(bool forceUpdate = false) { return this.gpuPower.CustomTgp; }
        public BiosData.GpuDState GetGpuDState(bool forceUpdate = false) { return this.gpuPower.DState; }
        public BiosData.GpuPpab GetGpuPpab(bool forceUpdate = false) { return this.gpuPower.Ppab; }
        public BiosData.GpuPowerData GetGpuPower(bool forceUpdate = false) { return this.gpuPower; }
        public void SetGpuPower(BiosData.GpuPowerData value) { this.gpuPower = value; this.GpuPowerWrites.Add(value); }

        public BiosData.Backlight GetKbdBacklight() { return BiosData.Backlight.Off; }
        public bool GetKbdBacklightSupport() { return false; }
        public void SetKbdBacklight(bool flag) { }
        public void SetKbdBacklight(BiosData.Backlight value) { }
        public BiosData.ColorTable GetKbdColor() { return default(BiosData.ColorTable); }
        public bool GetKbdColorSupport() { return false; }
        public void SetKbdColor(BiosData.ColorTable value) { }
        public BiosData.KbdType GetKbdType() { return BiosData.KbdType.Standard; }

        public Dictionary<string, string> BaseBoard { get; } = new Dictionary<string, string> {
            ["Manufacturer"] = "HP", ["Product"] = "8BAC", ["SerialNumber"] = "TEST", ["Version"] = "F.30" };

        public string GetManufacturer() { return "HP"; }
        public string GetProduct() { return "8BAC"; }
        public string GetSerial() { return "TEST"; }
        public string GetVersion() { return "F.30"; }

    }

}
