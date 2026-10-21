  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using OmenMon.Core;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The BIOS takes fan levels only as a SafeLevel (IBiosCtl.SetFanLevel), which only the
    // core's guard can make. The one way around it, SetFanLevelWithoutGuard, is the legacy
    // path of a fan array without a guard. These tests check that nothing else takes it:
    // at runtime (every 0x2E a guarded fan array sends goes through SetFanLevel) and in
    // the sources (nobody else calls it).
    [Collection(HardwareStateCollection.Name)]
    public class SafeLevelBoundaryTests {

        // Records which IBiosCtl methods are called and passes every call on
        public class BiosRecorder : DispatchProxy {

            public IBiosCtl Target = null!;
            public List<string> Calls { get; } = new List<string>();

            public static IBiosCtl Wrap(IBiosCtl target, out BiosRecorder recorder) {
                IBiosCtl proxy = DispatchProxy.Create<IBiosCtl, BiosRecorder>();
                recorder = (BiosRecorder) (object) proxy;
                recorder.Target = target;
                return proxy;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args) {
                this.Calls.Add(method!.Name);
                try {
                    return method.Invoke(this.Target, args);
                } catch(TargetInvocationException e) {
                    ExceptionDispatchInfo.Capture(e.InnerException!).Throw();
                    throw;
                }
            }

        }

        // Random sequences of everything that writes fan levels (manual and program
        // levels, 0/0, 255 for one fan and both, Fan Off, hand-backs, panic passes with
        // warm, hot, failing and parked sensors): with a guard, no call takes the
        // unguarded path, and every 0x2E the board receives came through SetFanLevel
        [Fact]
        public void GuardedFanArray_SendsEveryLevelAsASafeLevel() {
            int safeWrites = 0;
            for(int seed = 0; seed < 40; seed++) {
                BiosRecorder recorder = null!;
                using var rig = new TestRig(false, bios => BiosRecorder.Wrap(bios, out recorder));
                rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();
                var loop = new SafetyLoop(rig) { PanicThreshold = 65 };
                var random = new Random(seed);
                rig.SetCpuTemperature(45);

                for(int step = 0; step < 300; step++) {
                    int r = random.Next(100);
                    if(r < 20) rig.Advance(random.Next(1, 40));
                    else if(r < 45) loop.Tick();
                    else if(r < 55) rig.SetCpuTemperature((byte) random.Next(30, 101));
                    else if(r < 60) rig.Ec.GpuTemperature = (byte) (random.Next(3) == 0 ? 0 : random.Next(30, 101));
                    else if(r < 62) rig.Ec.LockFails = !rig.Ec.LockFails;
                    else if(r < 80) {
                        byte[][] choices = {
                            new byte[] { 0, 0 },
                            new byte[] { 0xFF, (byte) random.Next(0, 71) },
                            new byte[] { (byte) random.Next(0, 71), 0xFF },
                            new byte[] { 0xFF, 0xFF },
                            new byte[] { (byte) random.Next(0, 71), (byte) random.Next(0, 71) } };
                        rig.Fans.SetLevels(choices[random.Next(choices.Length)],
                            random.Next(2) == 0 ? FanLevelSource.Manual : FanLevelSource.Program);
                    }
                    else if(r < 88) rig.Fans.SetOff(true);
                    else if(r < 93) rig.Fans.Release();
                    else if(r < 96) rig.Fans.ReleaseIfHeld();
                    else rig.Fans.SetPanic(random.Next(2) == 0);
                }

                Assert.DoesNotContain(nameof(IBiosCtl.SetFanLevelWithoutGuard), recorder.Calls);
                int viaSafeLevel = recorder.Calls.Count(c => c == nameof(IBiosCtl.SetFanLevel));
                Assert.Equal(rig.Bios.Count(0x2E), viaSafeLevel);
                safeWrites += viaSafeLevel;
            }
            Assert.True(safeWrites > 1000, safeWrites + " level writes");
        }

        // Without a guard (legacy) the fan array writes what it is asked, through the
        // unguarded path, and only then
        [Fact]
        public void FanArrayWithoutGuard_UsesTheUnguardedPath() {
            BiosRecorder recorder = null!;
            using var rig = new TestRig(false, bios => BiosRecorder.Wrap(bios, out recorder));
            rig.Fans.Guard = null!;
            rig.BiosCtl.GetFanCount();

            rig.Fans.SetLevels(new byte[] { 30, 33 });
            rig.Fans.Release();
            rig.Fans.SetOff(true);

            Assert.Equal(3, recorder.Calls.Count(c => c == nameof(IBiosCtl.SetFanLevelWithoutGuard)));
            Assert.DoesNotContain(nameof(IBiosCtl.SetFanLevel), recorder.Calls);
            Assert.Equal(new[] { new byte[] { 30, 33 }, new byte[] { 0xFF, 0xFF }, new byte[] { 0, 0 } }, rig.Bios.LevelWrites);
        }

        // A default SafeLevel (no rule produced it) never reaches the board
        [Fact]
        public void DefaultSafeLevel_SendsNothing() {
            var rig = new TestRig();
            Assert.Throws<InvalidOperationException>(() => rig.BiosCtl.SetFanLevel(default));
            Assert.Empty(rig.Bios.Calls);
        }

        // Source tripwire: the unguarded call exists in BiosCtl (its definition) and
        // FanArray only, and FanArray takes it only where it has no guard
        [Fact]
        public void UnguardedPath_OnlyInTheLegacyBranches() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            Func<string, string> code = f => Regex.Replace(File.ReadAllText(f), @"//.*", "");

            var users = new[] { "App", "Core", "Driver", "External", "Hardware", "Library" }
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => code(f).Contains("SetFanLevelWithoutGuard"))
                .Select(f => Path.GetRelativePath(root, f))
                .OrderBy(f => f)
                .ToList();
            Assert.Equal(new[] { Path.Combine("Hardware", "BiosCtl.cs"), Path.Combine("Hardware", "FanArray.cs") }, users);

            string fanArray = code(Path.Combine(root, "Hardware", "FanArray.cs"));

            // Two calls of the BIOS method: the unguarded writer and SetOff's legacy 0/0
            Assert.Equal(2, Regex.Matches(fanArray, @"this\.Bios\.SetFanLevelWithoutGuard\b").Count);
            Assert.Matches(@"private void WriteLevelsWithoutGuard\(byte\[\] levels\) \{\s*WriteLevels\(levels, \(\) => Hw\.BiosSet\(this\.Bios\.SetFanLevelWithoutGuard, levels\)\);", fanArray);
            Assert.Matches(@"if\(allowedOff\.IsValid\)\s*Hw\.BiosSet\(this\.Bios\.SetFanLevel, allowedOff\);\s*else\s*Hw\.BiosSet\(this\.Bios\.SetFanLevelWithoutGuard, new byte\[\] \{ 0, 0 \}\);", fanArray);

            // The unguarded writer is called twice, each right inside "no guard"
            Assert.Equal(3, Regex.Matches(fanArray, @"\bWriteLevelsWithoutGuard\(").Count);
            Assert.Equal(2, Regex.Matches(fanArray, @"if\(this\.Guard == null\) \{\s*WriteLevelsWithoutGuard\(").Count);
        }

    }

}
