  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.Tests.Fakes {

    // Tests in this collection touch process-wide state (Config, Hw); run them serially
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class HardwareStateCollection {
        public const string Name = "Hardware state";
    }

    // An 8BAC assembled from simulated parts: the production Platform, FanArray, Fan,
    // EcComponent and WmiBiosTemperatureComponent classes over Fake8BacBios and
    // FakeEcRegisters. Sensor layout as on the real board: CPUT at EC 0xB2, GPTM at
    // EC 0xB4, plus the BIOS 0x23 sensor. With ecDriver, CPUT and GPTM read through the
    // production EC path (HwEcRegisters, Hw.TryEcGetByte, EmbeddedControllerAbstract) over
    // FakeEmbeddedController, which is then Hw.Ec until the rig is disposed. With
    // fanArrayBios, the fan array calls the BIOS through what it returns for BiosCtl
    // (a recorder in SafeLevelBoundaryTests); everything else uses BiosCtl directly.
    // With preset, the fan array is the one production builds for that model
    // (Platform.CreateFanArray, e.g. a BIOS-only model) and the platform carries it.
    public sealed class TestRig : IDisposable {

        public Fake8BacBios Bios { get; } = new Fake8BacBios();
        public FakeEcRegisters Ec { get; } = new FakeEcRegisters();
        public FakeSettings Settings { get; } = new FakeSettings();
        public FakeEmbeddedController? Driver { get; }
        public BiosCtl BiosCtl { get; }
        public FanArray Fans { get; }
        public Platform Platform { get; }

        // Before anything else: this process must never open the BIOS or the EC driver
        static TestRig() {
            Hw.Isolated = true;
        }

        private readonly IEmbeddedController? savedEc;
        private readonly int savedEcWaitSpinCount;

        public TestRig(bool ecDriver = false, Func<IBiosCtl, IBiosCtl>? fanArrayBios = null, PlatformPreset? preset = null) {

            Hw.Isolated = true;
            Config.LocaleInit();

            IEcRegisters sensors = this.Ec;
            if(ecDriver) {
                this.Driver = new FakeEmbeddedController(this.Ec);
                this.savedEc = Hw.Ec;
                this.savedEcWaitSpinCount = Config.EcWaitSpinCount;
                Hw.Ec = this.Driver;
                Config.EcWaitSpinCount = Config.EcWaitLimit; // a failing wait spins, no 1 ms sleeps
                sensors = HwEcRegisters.Instance;
            }

            // Flat floor only, unless a test sets the warm curve itself
            Config.FanSafetyWarmCurve = "none";

            // Sensor timestamps follow the simulated clock
            PlatformClock.Source = () => this.Bios.Now;

            this.BiosCtl = new BiosCtl(this.Bios);

            PlatformPreset p = PlatformPreset.Default;
            const PlatformData.AccessType R = PlatformData.AccessType.Read;
            const PlatformData.AccessType W = PlatformData.AccessType.Write;
            const PlatformData.AccessType RW = R | W;
            const PlatformData.DataSize B = PlatformData.DataSize.Byte;

            IFan cpu = new Fan(BiosData.FanType.Cpu,
                new EcComponent(p.FanLevelReg0, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanRateReadReg0, R, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanRateWriteReg0, W, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanSpeedReg0, R, PlatformData.DataSize.Word, int.MaxValue, this.Ec),
                this.BiosCtl);

            IFan gpu = new Fan(BiosData.FanType.Gpu,
                new EcComponent(p.FanLevelReg1, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanRateReadReg1, R, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanRateWriteReg1, W, B, int.MaxValue, this.Ec),
                new EcComponent(p.FanSpeedReg1, R, PlatformData.DataSize.Word, int.MaxValue, this.Ec),
                this.BiosCtl);

            this.Fans = preset != null
                ? Platform.CreateFanArray(preset, this.Ec, fanArrayBios != null ? fanArrayBios(this.BiosCtl) : this.BiosCtl)
                : new FanArray(new IFan[] { cpu, gpu },
                new EcComponent(p.CountdownReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.ManualReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.ModeReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.SwitchReg, RW, B, int.MaxValue, this.Ec),
                p.ManualValueOn, p.ManualValueOff, fanArrayBios != null ? fanArrayBios(this.BiosCtl) : this.BiosCtl);

            EcComponent cput = new EcTemperatureComponent(FakeEcRegisters.CpuTempRegister, Config.MaxBelievableTemperature, sensors);
            cput.SetName("CPUT");
            EcComponent gptm = new EcTemperatureComponent(FakeEcRegisters.GpuTempRegister, Config.MaxBelievableTemperature, sensors);
            gptm.SetName("GPTM");
            WmiBiosTemperatureComponent bios = new WmiBiosTemperatureComponent(Config.MaxBelievableTemperature, this.BiosCtl);

            this.Platform = new Platform(this.Settings, this.Fans,
                new IPlatformReadComponent[] { cput, gptm, bios },
                new bool[] { true, true, false },
                preset);

        }

        // Puts back what the EC driver mode replaced
        public void Dispose() {
            if(this.Driver != null) {
                Hw.Ec = this.savedEc!;
                Config.EcWaitSpinCount = this.savedEcWaitSpinCount;
            }
        }

        // Moves the simulated time
        public void Advance(int seconds) {
            this.Bios.Advance(TimeSpan.FromSeconds(seconds));
        }

        // Adds a fan program to the configuration (replacing one of the same name)
        public static void AddProgram(string name, params (byte Temperature, byte Cpu, byte Gpu)[] rows) {
            var levels = new SortedDictionary<byte, byte[]>();
            foreach(var row in rows)
                levels[row.Temperature] = new byte[] { row.Cpu, row.Gpu };
            Config.FanProgram[name] = new FanProgramData(name, BiosData.FanMode.Default, BiosData.GpuPowerLevel.Max, levels);
        }

        // Sets both the EC CPU sensor and the BIOS 0x23 sensor
        public void SetCpuTemperature(byte celsius) {
            this.Ec.CpuTemperature = celsius;
            this.Bios.CpuTemperature = celsius;
        }

    }

}
