  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Hardware.Bios;
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
    // EC 0xB4, plus the BIOS 0x23 sensor.
    public sealed class TestRig {

        public Fake8BacBios Bios { get; } = new Fake8BacBios();
        public FakeEcRegisters Ec { get; } = new FakeEcRegisters();
        public FakeSettings Settings { get; } = new FakeSettings();
        public BiosCtl BiosCtl { get; }
        public FanArray Fans { get; }
        public Platform Platform { get; }

        // Before anything else: this process must never open the BIOS or the EC driver
        static TestRig() {
            Hw.Isolated = true;
        }

        public TestRig() {

            Hw.Isolated = true;
            Config.LocaleInit();

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

            this.Fans = new FanArray(new IFan[] { cpu, gpu },
                new EcComponent(p.CountdownReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.ManualReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.ModeReg, RW, B, int.MaxValue, this.Ec),
                new EcComponent(p.SwitchReg, RW, B, int.MaxValue, this.Ec),
                p.ManualValueOn, p.ManualValueOff, this.BiosCtl);

            EcComponent cput = new EcTemperatureComponent(FakeEcRegisters.CpuTempRegister, Config.MaxBelievableTemperature, this.Ec);
            cput.SetName("CPUT");
            EcComponent gptm = new EcTemperatureComponent(FakeEcRegisters.GpuTempRegister, Config.MaxBelievableTemperature, this.Ec);
            gptm.SetName("GPTM");
            WmiBiosTemperatureComponent bios = new WmiBiosTemperatureComponent(Config.MaxBelievableTemperature, this.BiosCtl);

            this.Platform = new Platform(this.Settings, this.Fans,
                new IPlatformReadComponent[] { cput, gptm, bios },
                new bool[] { true, true, false });

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
