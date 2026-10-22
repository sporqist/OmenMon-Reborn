  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.Tests.Fakes {

    // What the GUI does to the hardware, without the GUI, over a TestRig: AutoConfig
    // (GuiOp.AutoConfig), the monitor pass (GuiMonitor.Pass and Sample: program tick,
    // constant-speed maintenance, every read the snapshot takes, thermal panic, fan
    // supervision, the 0x10 heartbeat), and the user's actions through the same
    // FanActions the main form and the tray menu call. Exit is FanHandBack's
    // ReleaseIfHeld. Only the dialogs and the hardware lock are left out.
    public sealed class GuiSession {

        public TestRig Rig { get; }
        public FanProgram Program { get; }
        public ThermalPanicState Panic { get; } = new ThermalPanicState();

        public bool ConstantSpeedActive;
        public bool PanicEnabled = true;
        public byte PanicThreshold = 90;
        public byte PanicHysteresis = 5;
        public TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        // What the last pass sampled (as the snapshot would hold it)
        public int[] FanSpeed { get; } = new int[2];
        public int[] FanRate { get; } = new int[2];
        public int[] FanLevel { get; } = new int[2];
        public BiosData.FanMode? Mode { get; private set; }
        public int Countdown { get; private set; }
        public bool Off { get; private set; }
        public bool Max { get; private set; }
        public byte MaxTemperature { get; private set; }

        private DateTime lastBeat = DateTime.MinValue;

        public GuiSession(TestRig rig) {
            this.Rig = rig;
            this.Program = new FanProgram(rig.Platform, (severity, message) => { });
        }

        private IFanArray Fans { get { return this.Rig.Platform.Fans; } }

        // GuiOp.AutoConfig's hardware part
        public void AutoConfig(string program) {
            this.Rig.Platform.System.SetGpuPower(new BiosData.GpuPowerData(
                (BiosData.GpuPowerLevel) Enum.Parse(typeof(BiosData.GpuPowerLevel), Config.GpuPowerDefault)));
            this.Program.Run(program);
        }

        // One monitor pass (GuiMonitor.Pass with a full sample); programTick: the
        // program interval is up
        public void Pass(bool programTick) {
            if(programTick) {
                if(this.Program.IsEnabled)
                    this.Program.Update();
                else if(Config.FanCountdownExtendAlways)
                    this.Program.UpdateCountdown(false, true);
            }
            if(this.ConstantSpeedActive)
                FanActions.MaintainConstantSpeed(this.Fans);
            Sample();
            this.Panic.Check(this.Rig.Platform, this.PanicEnabled, this.PanicThreshold, this.PanicHysteresis, out _);
            this.Fans.Supervise();
            DateTime now = this.Rig.Bios.Now;
            if(now - this.lastBeat >= this.HeartbeatInterval) {
                this.lastBeat = now;
                if(this.Fans.HeartbeatAllowed)
                    try { this.Rig.BiosCtl.GetFanCount(); } catch { }
            }
        }

        // Every read GuiMonitor.Sample makes for a full snapshot
        private void Sample() {
            Platform plat = this.Rig.Platform;
            plat.UpdateTemperature();
            for(int i = 0; i < plat.Temperature.Length; i++) {
                plat.Temperature[i].GetValue();
                plat.Temperature[i].GetValueTrend();
            }
            this.MaxTemperature = plat.GetMaxTemperature(false);
            this.Mode = plat.Fans.GetMode() ?? plat.Fans.LastModeSent;
            for(int i = 0; i < 2; i++) {
                this.FanSpeed[i] = plat.Fans.Fan[i].GetSpeed();
                this.FanLevel[i] = plat.Fans.Fan[i].GetLevel();
                this.FanRate[i] = plat.Fans.Fan[i].GetRate();
            }
            this.Countdown = plat.Fans.GetCountdown();
            this.Max = plat.Fans.GetMax();
            this.Off = plat.Fans.GetOff();
        }

        // Runs passes for the given simulated time, one every few seconds, with a program
        // tick every Config.UpdateProgramInterval passes (as the monitor counts them)
        public void Run(int passes, int secondsPerPass = 3) {
            for(int i = 0; i < passes; i++) {
                Pass((i + 1) % Math.Max(1, Config.UpdateProgramInterval) == 0);
                this.Rig.Advance(secondsPerPass);
            }
        }

        // The main form's "Set" (EventActionFanSet), with the states read just before
        public void Set(FanActions.Request request, string? program = null, byte level0 = 0, byte level1 = 0,
            bool bothMin = false, bool bothMax = false, PerformancePreset preset = PerformancePreset.Default) {
            bool isFanMax = this.Fans.GetMax();
            bool isFanOff = this.Fans.GetOff();
            this.ConstantSpeedActive = false;
            FanActions.Apply(request, this.Fans, this.Program, isFanMax, isFanOff, program,
                level0, level1, bothMin, bothMax, () => ApplyPreset(preset));
            this.ConstantSpeedActive = request == FanActions.Request.Constant;
        }

        // GuiOp.ApplyPreset (the refresh rate left out: not hardware)
        public void ApplyPreset(PerformancePreset preset) {
            FanActions.ApplyPreset(this.Fans, this.Rig.Platform.System, PerformancePresets.Resolve(preset));
        }

        // The tray menu's toggles
        public void TrayFanOff(bool off) {
            FanActions.ToggleOff(this.Fans, off);
        }

        public void TrayMax(bool on) {
            FanActions.ToggleMax(this.Fans, on);
        }

        // Exit: the last-chance hand-back (FanHandBack runs ReleaseIfHeld)
        public void Exit() {
            this.Program.Terminate();
            this.Fans.ReleaseIfHeld();
        }

    }

}
