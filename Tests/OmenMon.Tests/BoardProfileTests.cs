  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The 8BAC board profile in the core: the measured facts as data, checked against
    // themselves, against the simulated board the other tests run on, and against the
    // guard's own constants, so the three cannot drift apart.
    [Collection(HardwareStateCollection.Name)]
    public class BoardProfileTests {

        private static readonly BoardProfile P = BoardProfile.Omen8Bac;

        [Fact]
        public void Omen8Bac_Identity() {
            Assert.Equal("8BAC", P.ProductId);
            Assert.Equal("F.30", P.ValidatedBios);
            Assert.Equal((uint) BiosData.Cmd.Default, P.Command);
            Assert.Equal(0x10u, P.FanCountType);
            Assert.Equal(0x2Eu, P.FanLevelType);
        }

        // Writes: 0x2E and 0x10 only. Everything else is refused, including the types the
        // application still writes today without a gate of its own (Max Fan 0x27, fan
        // mode 0x1A) and the SMI-backed ones.
        [Theory]
        [InlineData(0x10u, true)]
        [InlineData(0x2Eu, true)]
        [InlineData(0x1Au, false)]  // fan mode (also writes power limits)
        [InlineData(0x27u, false)]  // Max Fan
        [InlineData(0x29u, false)]  // CPU power limits, SMI, no bounds
        [InlineData(0x2Fu, false)]
        [InlineData(0x32u, false)]  // fan table write
        [InlineData(0x52u, false)]  // graphics mode (MUX), SMI
        [InlineData(0x53u, false)]  // NVRAM, SMI
        [InlineData(0u, false)]
        [InlineData(0xFFFFFFFFu, false)]
        public void Omen8Bac_WriteAllowlist(uint type, bool allowed) {
            Assert.Equal(allowed, P.MayWrite(type));
        }

        [Fact]
        public void WriteTypes_IsACopy() {
            Assert.Equal(new uint[] { 0x10, 0x2E }, P.WriteTypes);
            P.WriteTypes[0] = 0x52;
            Assert.False(P.MayWrite(0x52));
            Assert.Equal(new uint[] { 0x10, 0x2E }, P.WriteTypes);
        }

        [Theory]
        [InlineData(0xFF, FanLevelEffect.Keep)]
        [InlineData(0, FanLevelEffect.Off)]
        [InlineData(1, FanLevelEffect.Set)]
        [InlineData(20, FanLevelEffect.Set)]
        [InlineData(63, FanLevelEffect.Set)]
        [InlineData(100, FanLevelEffect.Set)]
        public void Omen8Bac_LevelEffect(byte level, FanLevelEffect effect) {
            Assert.Equal(effect, P.Effect(level));
        }

        [Theory]
        [InlineData(0xFF, 0xFF, true, false)]   // no change at all
        [InlineData(0xFF, 30, false, false)]    // the CPU fan stays, the GPU fan is set
        [InlineData(30, 0xFF, false, false)]
        [InlineData(0, 0, false, true)]         // both fans off
        [InlineData(0, 30, false, false)]
        [InlineData(0xFF, 0, false, false)]
        [InlineData(20, 22, false, false)]
        public void Omen8Bac_NoChangeAndOff(byte cpu, byte gpu, bool noChange, bool off) {
            Assert.Equal(noChange, P.IsNoChange(cpu, gpu));
            Assert.Equal(off, P.IsOff(cpu, gpu));
        }

        [Fact]
        public void Omen8Bac_ArmingAndExpiry() {
            DateTime t = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            Assert.False(P.IsArmed(null, t));
            Assert.True(P.IsArmed(t, t));
            Assert.True(P.IsArmed(t, t.AddSeconds(30)));
            Assert.False(P.IsArmed(t, t.AddSeconds(31)));
            Assert.Equal(t.AddSeconds(124), P.LevelExpiresAt(t));
        }

        [Fact]
        public void Find() {
            Assert.Same(P, BoardProfile.Find("8BAC"));
            Assert.Same(P, BoardProfile.Find(" 8bac "));
            Assert.Null(BoardProfile.Find("8C77"));
            Assert.Null(BoardProfile.Find(""));
            Assert.Null(BoardProfile.Find(null!));
        }

        // The profile and the simulated 8BAC (Fake8BacBios) describe the same board
        [Fact]
        public void SimulatedBoard_MatchesTheProfile_Timing() {
            var fake = new Fake8BacBios();
            Assert.Equal(P.ArmWindow, fake.ArmWindow);
            Assert.Equal(P.LevelExpiry, fake.ExpiryAfter);

            var bios = new BiosCtl(fake);
            bios.SetFanLevelWithoutGuard(new byte[] { 40, 41 });
            DateTime set = fake.Now;
            fake.Now = P.LevelExpiresAt(set) - TimeSpan.FromSeconds(1);
            Assert.Equal(new byte[] { 40, 41 }, fake.MeasuredLevels);
            fake.Now = P.LevelExpiresAt(set);
            Assert.Equal(fake.CurveLevels, fake.MeasuredLevels);

            // A 0x2E later than the arm window after the last 0x10 does nothing
            fake.Send(BiosData.Cmd.Default, P.FanCountType, new byte[4], 4, out _);
            fake.Advance(P.ArmWindow + TimeSpan.FromSeconds(1));
            fake.Send(BiosData.Cmd.Default, P.FanLevelType, new byte[] { 50, 50, 0, 0 }, 0, out _);
            Assert.Null(fake.ActiveLevels);
        }

        [Fact]
        public void SimulatedBoard_MatchesTheProfile_SpecialValues() {
            var fake = new Fake8BacBios();
            var bios = new BiosCtl(fake);

            bios.SetFanLevelWithoutGuard(new byte[] { 30, 33 });
            bios.SetFanLevelWithoutGuard(new byte[] { P.KeepLevel, 40 });
            Assert.Equal(new byte[] { 30, 40 }, fake.MeasuredLevels);   // Keep: the CPU fan stayed
            bios.SetFanLevelWithoutGuard(new byte[] { 35, P.KeepLevel });
            Assert.Equal(new byte[] { 35, 40 }, fake.MeasuredLevels);
            bios.SetFanLevelWithoutGuard(new byte[] { P.KeepLevel, P.KeepLevel });
            Assert.Equal(new byte[] { 35, 40 }, fake.MeasuredLevels);   // no change

            Assert.True(P.IsOff(0, 0));
            bios.SetFanLevelWithoutGuard(new byte[] { 0, 0 });
            Assert.True(fake.FansOff);                                  // 0/0 is off, not automatic
        }

        // The guard's constants agree with the profile: its "no change" value is the
        // board's keep value, and the hand-back stays quiet for longer than a level holds
        [Fact]
        public void Guard_AgreesWithTheProfile() {
            Assert.Equal(FanSafetyGuard.NoChange, P.KeepLevel);
            Assert.True(new FanSafetyPolicy().HandBackQuietPeriod > P.LevelExpiry);
        }

    }

}
