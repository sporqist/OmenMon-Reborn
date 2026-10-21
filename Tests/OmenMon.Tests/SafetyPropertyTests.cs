  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // Property tests for the fan-safety rules: random sequences of requests (manual and
    // program levels, Fan Off, hand-backs), temperatures, sensor failures, time steps and
    // monitor passes against the simulated 8BAC. After every step, what the BIOS received
    // is checked against an oracle of its own (SensorModel), not against the guard:
    //
    //   P1  no fan level below the floor unless the readings say known cool; a hand-back
    //       level never below the floor; after a pass, the fans do not run below it
    //       either unless known cool
    //   P2  a pass whose reading is at or above the panic threshold ends in panic, with
    //       the board at the fan-table maximum or above; it stays there while panic lasts
    //   P3  0/0 only while known cool: never while warm or unknown, never in panic, never
    //       as a hand-back level
    //   P4  after a hand-back, no 0x2E and no 0x10 for 150 s unless a new request or
    //       panic comes first
    //
    // Hand-rolled generator, seeded and reproducible: a failure names its seed; set
    // OMENMON_PROPERTY_SEED to that seed to run it alone.
    [Collection(HardwareStateCollection.Name)]
    public class SafetyPropertyTests {

        public const string SeedVariable = "OMENMON_PROPERTY_SEED";
        private const int BaseSeed = 20261021;
        private const int Runs = 1000;
        private const int Steps = 300;

        [Fact]
        public void RandomSequences_KeepTheSafetyRules() {
            string? only = Environment.GetEnvironmentVariable(SeedVariable);
            IEnumerable<int> seeds = only != null
                ? new[] { int.Parse(only) }
                : Enumerable.Range(0, Runs).Select(i => BaseSeed + i);
            var stats = new Stats();
            foreach(int seed in seeds) {
                string? failure = new Run(seed, null, stats).Execute();
                if(failure != null)
                    Assert.Fail("Seed " + seed + " (rerun alone with " + SeedVariable + "=" + seed + "): " + failure);
            }

            // The generator reaches the interesting cases often enough to mean something
            TestContext.Current.TestOutputHelper?.WriteLine(stats.ToString());
            if(only == null)
                Assert.True(stats.PanicEntered >= 500 && stats.PanicCleared >= 250 && stats.HandBacks >= 1000
                    && stats.ZeroSent >= 250 && stats.ZeroRefused >= 1000 && stats.Stale >= 1000, stats.ToString());
        }

        // What the runs went through
        private sealed class Stats {
            public int Steps, PanicEntered, PanicCleared, HandBacks, ZeroSent, ZeroRefused, Stale;
            public override string ToString() {
                return Steps + " steps: panic entered " + PanicEntered + ", cleared " + PanicCleared
                    + "; hand-backs " + HandBacks + "; 0/0 sent while cool " + ZeroSent
                    + ", refused while not cool " + ZeroRefused + "; passes without a fresh CPU reading " + Stale;
            }
        }

        // The checker itself: a guard that thinks everything below 200 C is cool must be
        // caught sending levels below the floor while warm
        [Fact]
        public void Checker_CatchesAGuardWithoutTheFloor() {
            Assert.Contains("P1", FirstFailure(loop => loop.Rig.Platform.SafetyPolicy.CoolTemperature = 200));
        }

        // ...and a monitor that never starts panic must be caught too
        [Fact]
        public void Checker_CatchesAMissingPanic() {
            Assert.Contains("P2", FirstFailure(loop => loop.PanicEnabled = false));
        }

        // ...and a heartbeat that does not keep quiet after a hand-back
        [Fact]
        public void Checker_CatchesAHeartbeatAfterHandBack() {
            Assert.Contains("P4", FirstFailure(loop => loop.Rig.Platform.SafetyPolicy.HandBackQuietPeriod = TimeSpan.Zero));
        }

        private static string FirstFailure(Action<SafetyLoop> sabotage) {
            for(int i = 0; i < Runs; i++) {
                string? failure = new Run(BaseSeed + i, sabotage).Execute();
                if(failure != null)
                    return failure;
            }
            return "no failure found";
        }

        // What the sensors last reported, kept apart from the production code: the
        // oracle for "known cool" and for the panic reading
        private sealed class SensorModel {

            private const int MaxBelievable = 120;
            private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);

            private (int Value, DateTime At)? cput, bios, gptm;
            private DateTime? gptmZeroAt;

            // One sensor sweep with the ground truth as it is now
            public void Sample(DateTime now, int ecCpu, int ecGpu, bool ecFails, int biosCpu, bool biosFails) {
                if(!ecFails) {
                    if(Valid(ecCpu)) this.cput = (ecCpu, now);
                    if(Valid(ecGpu)) this.gptm = (ecGpu, now);
                    else if(ecGpu == 0) this.gptmZeroAt = now;
                }
                if(!biosFails && Valid(biosCpu))
                    this.bios = (biosCpu, now);
            }

            private static bool Valid(int value) {
                return value > 0 && value <= MaxBelievable;
            }

            private static bool Fresh((int Value, DateTime At)? r, DateTime now) {
                return r.HasValue && now - r.Value.At <= MaxAge;
            }

            // The CPU temperature: the hotter of the fresh EC and BIOS readings
            public int? Cpu(DateTime now) {
                int? best = null;
                if(Fresh(this.cput, now)) best = this.cput!.Value.Value;
                if(Fresh(this.bios, now) && (!best.HasValue || this.bios!.Value.Value > best)) best = this.bios!.Value.Value;
                return best;
            }

            // fresh: a value; parked: never read, or a recent successful 0; else unknown
            public bool GpuFresh(DateTime now) { return Fresh(this.gptm, now); }
            public bool GpuParked(DateTime now) {
                return !GpuFresh(now) && (!this.gptm.HasValue
                    || (this.gptmZeroAt.HasValue && now - this.gptmZeroAt.Value <= MaxAge));
            }

            public bool Cool(DateTime now, int coolTemperature) {
                int? cpu = Cpu(now);
                if(!cpu.HasValue || cpu.Value >= coolTemperature)
                    return false;
                if(GpuFresh(now))
                    return this.gptm!.Value.Value < coolTemperature;
                return GpuParked(now);
            }

            // What thermal panic is judged on: null without a CPU reading, as in
            // Platform.GetHottestReading (a hot GPU alone does not start panic while the
            // CPU does not read: vector b-panic-09, disputed)
            public int? Hottest(DateTime now) {
                int? cpu = Cpu(now);
                if(!cpu.HasValue)
                    return null;
                return GpuFresh(now) && this.gptm!.Value.Value > cpu.Value ? this.gptm!.Value.Value : cpu.Value;
            }

        }

        private sealed class Run {

            private const byte FloorCpu = 20, FloorGpu = 22;
            private const int CoolTemperature = 50;
            private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(150);

            private readonly Random random;
            private readonly Action<SafetyLoop>? sabotage;
            private readonly Stats stats;
            private readonly List<string> trace = new List<string>();

            private TestRig rig = null!;
            private SafetyLoop loop = null!;
            private readonly SensorModel model = new SensorModel();
            private byte[] tableMax = null!;

            // Ground truth
            private int cpu = 45, gpu;
            private bool ecFails, biosFails;

            // Start of the quiet period after the last hand-back (null: none)
            private DateTime? quietSince;

            public Run(int seed, Action<SafetyLoop>? sabotage = null, Stats? stats = null) {
                this.random = new Random(seed);
                this.sabotage = sabotage;
                this.stats = stats ?? new Stats();
            }

            private DateTime Now { get { return this.rig.Bios.Now; } }

            public string? Execute() {

                using(this.rig = new TestRig()) {

                    this.rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();
                    this.loop = new SafetyLoop(this.rig);
                    byte[][] settings = { new byte[] { 90, 5 }, new byte[] { 65, 5 }, new byte[] { 95, 10 } };
                    byte[] panic = settings[this.random.Next(settings.Length)];
                    this.loop.PanicThreshold = panic[0];
                    this.loop.PanicHysteresis = panic[1];
                    this.sabotage?.Invoke(this.loop);
                    this.tableMax = TableMaximum(this.rig.Bios.FanTable);
                    this.rig.SetCpuTemperature((byte) this.cpu);
                    this.trace.Add("panic " + panic[0] + "/" + panic[1]);

                    for(int step = 0; step < Steps; step++) {
                        string? failure = Step();
                        if(failure != null)
                            return failure + Environment.NewLine + "Last steps:" + Environment.NewLine
                                + string.Join(Environment.NewLine, this.trace.Skip(Math.Max(0, this.trace.Count - 25)));
                    }

                }
                return null;

            }

            private static byte[] TableMaximum(byte[] table) {
                byte[] max = new byte[2];
                for(int row = 0; row < table[1]; row++) {
                    max[0] = Math.Max(max[0], table[2 + 3 * row]);
                    max[1] = Math.Max(max[1], table[3 + 3 * row]);
                }
                return max;
            }

            private string? Step() {

                int callsBefore = this.rig.Bios.Calls.Count;
                bool panicBefore = this.rig.Fans.Guard.IsPanic;
                DateTime? releasedBefore = this.rig.Fans.Guard.ReleasedAt;
                bool request = false, handBack = false, tick = false, zeroRequest = false;
                string what;

                int r = this.random.Next(100);
                if(r < 25) {
                    int s = this.random.Next(10) == 0 ? this.random.Next(30, 90) : this.random.Next(1, 16);
                    this.rig.Advance(s);
                    what = "advance " + s;
                } else if(r < 55 || r >= 97) {
                    tick = true;
                    what = "tick";
                } else if(r < 65) {
                    this.cpu = this.random.Next(30, 101);
                    this.rig.SetCpuTemperature((byte) this.cpu);
                    what = "cpu " + this.cpu;
                } else if(r < 72) {
                    this.gpu = this.random.Next(3) == 0 ? 0 : this.random.Next(30, 101);
                    this.rig.Ec.GpuTemperature = (byte) this.gpu;
                    what = "gpu " + this.gpu;
                } else if(r < 74) {
                    this.ecFails = !this.ecFails;
                    this.rig.Ec.LockFails = this.ecFails;
                    what = "ec " + (this.ecFails ? "fails" : "reads");
                } else if(r < 76) {
                    this.biosFails = !this.biosFails;
                    if(this.biosFails) this.rig.Bios.FailTypes.Add(0x23); else this.rig.Bios.FailTypes.Remove(0x23);
                    what = "bios 0x23 " + (this.biosFails ? "fails" : "reads");
                } else if(r < 77) {
                    bool fail = !this.rig.Bios.FailTypes.Contains(0x2F);
                    if(fail) this.rig.Bios.FailTypes.Add(0x2F); else this.rig.Bios.FailTypes.Remove(0x2F);
                    what = "bios 0x2F " + (fail ? "fails" : "reads");
                } else if(r < 87) {
                    byte[] level = this.random.Next(10) < 3
                        ? new byte[] { 0, 0 }
                        : new byte[] { (byte) this.random.Next(0, 71), (byte) this.random.Next(0, 71) };
                    FanLevelSource source = this.random.Next(2) == 0 ? FanLevelSource.Manual : FanLevelSource.Program;
                    this.rig.Fans.SetLevels(level, source);
                    request = true;
                    zeroRequest = level[0] == 0 && level[1] == 0;
                    what = "set " + level[0] + "/" + level[1] + " " + source;
                } else if(r < 91) {
                    this.rig.Fans.SetOff(true);
                    request = zeroRequest = true;
                    what = "fan off";
                } else if(r < 94) {
                    if(this.random.Next(2) == 0) {
                        this.rig.Fans.Release();
                        what = "release";
                    } else {
                        this.rig.Fans.SetLevels(new byte[] { 0xFF, 0xFF });
                        what = "set 255/255";
                    }
                    request = handBack = true;
                } else if(r < 95) {
                    handBack = this.rig.Fans.Guard.HoldsLevel;
                    this.rig.Fans.ReleaseIfHeld();
                    request = true;
                    what = "release if held";
                } else {
                    this.loop.HeartbeatPaused = !this.loop.HeartbeatPaused;
                    what = "heartbeat " + (this.loop.HeartbeatPaused ? "paused" : "on");
                }

                if(tick) {
                    this.loop.Tick();
                    this.model.Sample(this.Now, this.ecFails ? 0 : this.cpu, this.ecFails ? 0 : this.gpu, this.ecFails,
                        this.cpu, this.biosFails);
                }

                var calls = this.rig.Bios.Calls.Skip(callsBefore).ToList();
                bool cool = this.model.Cool(this.Now, CoolTemperature);
                bool panicAfter = this.rig.Fans.Guard.IsPanic;
                this.trace.Add("t+" + (int) (this.Now - new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)).TotalSeconds + "s "
                    + what + (calls.Count > 0 ? " -> " + string.Join(" ", calls.Where(c => c.Type == 0x10 || c.Type == 0x2E)
                        .Select(c => c.Type == 0x2E ? c.Data[0] + "/" + c.Data[1] : "0x10")) : "")
                    + (cool ? " [cool]" : "") + (panicAfter ? " [panic]" : ""));

                // A hand-back writes a safe level, then nothing: the last level written in
                // a step that handed back is the one left in effect
                DateTime? releasedAfter = this.rig.Fans.Guard.ReleasedAt;
                bool handedBack = releasedAfter.HasValue && (releasedAfter != releasedBefore || handBack);
                byte[]? previous = null;
                foreach(var c in calls) {
                    if(c.Type != 0x2E)
                        continue;
                    if(c.Data[0] == 0xFF && c.Data[1] == 0xFF)
                        return "P4: 255/255 sent (" + what + ")";
                    previous = new byte[] { c.Data[0], c.Data[1] };
                    if((c.Data[0] < FloorCpu || c.Data[1] < FloorGpu) && !cool)
                        return "P1: sent " + c.Data[0] + "/" + c.Data[1] + " while not known cool";
                    if(c.Data[0] == 0 && c.Data[1] == 0 && panicBefore && panicAfter)
                        return "P3: sent 0/0 in panic";
                    if(c.Data[0] == 0 && c.Data[1] == 0 && !cool)
                        return "P3: sent 0/0 while not known cool";
                }
                if(handedBack && previous != null && (previous[0] < FloorCpu || previous[1] < FloorGpu))
                    return "P1: hand-back left " + previous[0] + "/" + previous[1] + ", below the floor";

                // P1 on what the fans do: after a pass, the board runs below the floor only
                // while known cool (a held level that is no longer allowed has been raised)
                if(tick) {
                    byte[] m = this.rig.Bios.MeasuredLevels;
                    if((m[0] < FloorCpu || m[1] < FloorGpu) && !cool)
                        return "P1: after a pass the fans run at " + m[0] + "/" + m[1] + " while not known cool";
                }

                // P2: the reading this pass judged on is at or above the threshold
                if(tick) {
                    int? hottest = this.model.Hottest(this.Now);
                    if(hottest.HasValue && hottest.Value >= this.loop.PanicThreshold) {
                        if(!this.loop.Panic.IsActive || !panicAfter)
                            return "P2: read " + hottest + " C (threshold " + this.loop.PanicThreshold + "), no panic";
                        if(!calls.Any(c => c.Type == 0x2E && c.Data[0] >= this.tableMax[0] && c.Data[1] >= this.tableMax[1]))
                            return "P2: read " + hottest + " C, panic, but no write at the fan-table maximum this pass";
                    }
                    if(panicAfter) {
                        byte[] m = this.rig.Bios.MeasuredLevels;
                        if(m[0] < this.tableMax[0] || m[1] < this.tableMax[1])
                            return "P2: in panic, but the fans run at " + m[0] + "/" + m[1];
                    }
                }

                // The oracle and the code agree on what the readings say (otherwise P1 and
                // P2 would check against the wrong model). Not with a sabotaged guard.
                if(this.sabotage == null) {
                    if(this.rig.Fans.Guard.IsCool() != cool)
                        return "M: the oracle says " + (cool ? "cool" : "not cool") + ", the guard does not agree";
                    SensorReading hot = this.rig.Platform.GetHottestReading(false, out _);
                    int? oracleHot = this.model.Hottest(this.Now);
                    if((hot.HasValue ? hot.Value : (int?) null) != oracleHot)
                        return "M: hottest " + (hot.HasValue ? hot.Value.ToString() : "none") + ", the oracle says "
                            + (oracleHot.HasValue ? oracleHot.Value.ToString() : "none");
                }

                this.stats.Steps++;
                if(!panicBefore && panicAfter) this.stats.PanicEntered++;
                if(panicBefore && !panicAfter) this.stats.PanicCleared++;
                if(handedBack) this.stats.HandBacks++;
                this.stats.ZeroSent += calls.Count(c => c.Type == 0x2E && c.Data[0] == 0 && c.Data[1] == 0);
                if(zeroRequest && !cool && !calls.Any(c => c.Type == 0x2E && c.Data[0] == 0 && c.Data[1] == 0)) this.stats.ZeroRefused++;
                if(tick && !this.model.Cpu(this.Now).HasValue) this.stats.Stale++;

                // P4: silence after a hand-back
                if(panicBefore || panicAfter || (request && !handBack))
                    this.quietSince = null;
                if(handedBack)
                    this.quietSince = this.Now; // the step's own calls are the hand-back
                else foreach(var c in calls) {
                    if(!request && !panicBefore && !panicAfter && (c.Type == 0x2E || c.Type == 0x10)
                        && this.quietSince.HasValue && this.Now - this.quietSince.Value < Quiet)
                        return "P4: 0x" + c.Type.ToString("X2") + " " + (int) (this.Now - this.quietSince.Value).TotalSeconds
                            + " s after a hand-back";
                }

                return null;

            }

        }

    }

}
