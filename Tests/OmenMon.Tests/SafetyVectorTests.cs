  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Text;
using System.Text.Json;
using OmenMon.Core;
using OmenMon.Hardware.Platform;
using OmenMon.Tests.Fakes;

namespace OmenMon.Tests {

    // The shared safety vectors (Vectors/safety-vectors.json): the fan-safety rules of the
    // restructuring plan, section 3, as a table, checked against the current classes.
    // The same table is what the extracted core must pass before old code is removed.
    //
    // Kinds:
    //   guard  FanSafetyGuard / FanSafetyPolicy / GpuReading with readings, time and a
    //          history of earlier writes, releases and panic as inputs
    //   panic  ThermalPanicState: previous state, reading, completeness, settings
    //   board  FanArray + Platform over the simulated 8BAC, driven by SafetyLoop: what
    //          the BIOS receives, what the fans then do
    [Collection(HardwareStateCollection.Name)]
    public class SafetyVectorTests {

        private static readonly DateTime Now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        private static readonly Lazy<JsonDocument> Document = new Lazy<JsonDocument>(() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "safety-vectors.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }));

        private static JsonElement Root { get { return Document.Value.RootElement; } }

        private static IEnumerable<JsonElement> Vectors() {
            return Root.GetProperty("vectors").EnumerateArray();
        }

        private static JsonElement Vector(string id) {
            return Vectors().Single(v => v.GetProperty("id").GetString() == id);
        }

        public static TheoryData<string> VectorIds {
            get {
                var data = new TheoryData<string>();
                foreach(JsonElement v in Vectors())
                    data.Add(v.GetProperty("id").GetString()!);
                return data;
            }
        }

        [Theory]
        [MemberData(nameof(VectorIds))]
        public void Vector_HoldsForTheCurrentCode(string id) {
            JsonElement v = Vector(id);
            switch(v.GetProperty("kind").GetString()) {
                case "guard": RunGuard(v); break;
                case "panic": RunPanic(v); break;
                case "board": RunBoard(v); break;
                default: Assert.Fail(id + ": unknown kind"); break;
            }
        }

        // Every vector is well-formed, and every rule of the table is covered by a vector
        // or listed as not expressible with the reason
        [Fact]
        public void Table_IsWellFormed_AndCoversEveryRule() {
            var tags = Root.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("tag").GetString()!).ToList();
            Assert.Equal(tags.Count, tags.Distinct().Count());
            var ids = new HashSet<string>();
            foreach(JsonElement v in Vectors()) {
                string id = v.GetProperty("id").GetString()!;
                Assert.True(ids.Add(id), "Duplicate id " + id);
                Assert.False(string.IsNullOrWhiteSpace(v.GetProperty("rule").GetString()), id + ": no rule text");
                Assert.Contains(v.GetProperty("covers").GetString()!, tags);
            }
            var covered = Vectors().Select(v => v.GetProperty("covers").GetString()!).ToHashSet();
            var excused = Root.GetProperty("notExpressible").EnumerateArray().Select(r => r.GetProperty("tag").GetString()!).ToHashSet();
            var uncovered = tags.Where(t => !covered.Contains(t) && !excused.Contains(t)).ToList();
            Assert.True(uncovered.Count == 0, "Rules without a vector: " + string.Join(", ", uncovered));
        }

#region Guard
        private static void RunGuard(JsonElement v) {

            FanSafetyPolicy policy = Policy(v);
            SensorReading cpu = Has(v, "cpu") ? Reading(v.GetProperty("cpu")) : SensorReading.None;
            GpuReading gpu = Has(v, "gpu") ? Gpu(v.GetProperty("gpu")) : GpuReading.None;
            DateTime clock = Now;
            var guard = new FanSafetyGuard(policy, () => cpu, () => gpu, () => clock);

            if(Has(v, "history"))
                foreach(JsonElement h in v.GetProperty("history").EnumerateArray()) {
                    clock = Now.AddSeconds(Has(h, "at") ? h.GetProperty("at").GetDouble() : 0);
                    if(Has(h, "write"))
                        guard.RecordWrite(Level(h.GetProperty("request")), Level(h.GetProperty("write")), Source(h));
                    else if(Has(h, "supervised"))
                        guard.RecordSupervisedWrite(Level(h.GetProperty("supervised")));
                    else if(Has(h, "release"))
                        guard.RecordRelease();
                    else if(Has(h, "panic"))
                        guard.SetPanic(Level(h.GetProperty("panic")));
                    else
                        Assert.Fail("Unknown history entry: " + h);
                }
            clock = Now;

            JsonElement expect = v.GetProperty("expect");
            string op = v.GetProperty("op").GetString()!;
            switch(op) {
                case "filter":
                    AssertLevel(expect, guard.Filter(Level(v.GetProperty("level")), Source(v)));
                    break;
                case "supervise":
                    AssertLevel(expect, guard.Supervise());
                    break;
                case "handBackLevel":
                    AssertLevel(expect, guard.HandBackLevel());
                    break;
                case "isCool":
                    Assert.Equal(expect.GetBoolean(), guard.IsCool());
                    break;
                case "hottest":
                    Assert.Equal(expect.ValueKind == JsonValueKind.Null ? (int?) null : expect.GetInt32(), guard.Hottest());
                    break;
                case "heartbeatAllowed":
                    Assert.Equal(expect.GetBoolean(), guard.HeartbeatAllowed);
                    break;
                case "holdsLevel":
                    Assert.Equal(expect.GetBoolean(), guard.HoldsLevel);
                    break;
                case "needsSupervision":
                    Assert.Equal(expect.GetBoolean(), guard.NeedsSupervision);
                    break;
                case "gpuState":
                    Assert.Equal(expect.GetString(), gpu.StateAt(Now, policy.ReadingMaxAge).ToString());
                    break;
                default:
                    Assert.Fail("Unknown op " + op);
                    break;
            }

        }

        private static FanSafetyPolicy Policy(JsonElement v) {
            var policy = new FanSafetyPolicy();
            if(!Has(v, "policy"))
                return policy;
            foreach(JsonProperty p in v.GetProperty("policy").EnumerateObject())
                switch(p.Name) {
                    case "floorCpu": policy.FloorCpu = p.Value.GetByte(); break;
                    case "floorGpu": policy.FloorGpu = p.Value.GetByte(); break;
                    case "coolTemperature": policy.CoolTemperature = p.Value.GetInt32(); break;
                    case "readingMaxAgeS": policy.ReadingMaxAge = TimeSpan.FromSeconds(p.Value.GetDouble()); break;
                    case "belowFloorMaxMinutes": policy.BelowFloorMaxDuration = TimeSpan.FromMinutes(p.Value.GetDouble()); break;
                    case "handBackQuietS": policy.HandBackQuietPeriod = TimeSpan.FromSeconds(p.Value.GetDouble()); break;
                    case "warmCurve": policy.WarmCurve = WarmCurve(p.Value.GetString()!); break;
                    default: Assert.Fail("Unknown policy setting " + p.Name); break;
                }
            return policy;
        }

        private static SafetyStep[] WarmCurve(string text) {
            return text == "default" ? FanSafetyPolicy.DefaultWarmCurve() : SafetyStep.ParseList(text)!;
        }

        private static SensorReading Reading(JsonElement r) {
            return new SensorReading(r.GetProperty("c").GetInt32(), Now.AddSeconds(-r.GetProperty("age").GetDouble()));
        }

        private static GpuReading Gpu(JsonElement g) {
            SensorReading last = Has(g, "c") ? Reading(g) : SensorReading.None;
            DateTime zeroAt = Has(g, "zeroAge") ? Now.AddSeconds(-g.GetProperty("zeroAge").GetDouble()) : default(DateTime);
            return new GpuReading(last, zeroAt);
        }
#endregion

#region Panic
        private static void RunPanic(JsonElement v) {
            byte threshold = Has(v, "threshold") ? v.GetProperty("threshold").GetByte() : (byte) 90;
            byte hysteresis = Has(v, "hysteresis") ? v.GetProperty("hysteresis").GetByte() : (byte) 5;
            bool enabled = !Has(v, "enabled") || v.GetProperty("enabled").GetBoolean();
            bool complete = !Has(v, "complete") || v.GetProperty("complete").GetBoolean();
            JsonElement h = v.GetProperty("hottest");
            SensorReading hottest = h.ValueKind == JsonValueKind.Null ? SensorReading.None : new SensorReading(h.GetInt32(), Now);

            var state = new ThermalPanicState();
            if(v.GetProperty("active").GetBoolean())
                Assert.Equal(ThermalPanicTransition.Entered,
                    state.Update(true, new SensorReading(threshold, Now), true, threshold, hysteresis));

            ThermalPanicTransition transition = state.Update(enabled, hottest, complete, threshold, hysteresis);
            JsonElement expect = v.GetProperty("expect");
            Assert.Equal(expect.GetProperty("transition").GetString(), transition.ToString());
            Assert.Equal(expect.GetProperty("active").GetBoolean(), state.IsActive);
        }
#endregion

#region Board
        // BIOS calls that change something (the arming/keep-alive query counts: it arms
        // 0x2E and keeps a level alive); reads are left out
        private static readonly HashSet<uint> WriteTypes = new HashSet<uint> { 0x10, 0x1A, 0x27, 0x2E, 0x32 };

        private static void RunBoard(JsonElement v) {

            var rig = new TestRig();
            rig.Platform.SafetyPolicy.WarmCurve = FanSafetyPolicy.DefaultWarmCurve();
            var loop = new SafetyLoop(rig);
            int mark = 0;
            int stepNo = 0;

            foreach(JsonElement step in v.GetProperty("steps").EnumerateArray()) {
                stepNo++;
                JsonProperty action = step.EnumerateObject().First();
                switch(action.Name) {
                    case "cpu": rig.SetCpuTemperature((byte) action.Value.GetInt32()); break;
                    case "gpu": rig.Ec.GpuTemperature = (byte) action.Value.GetInt32(); break;
                    case "ecFails": rig.Ec.LockFails = action.Value.GetBoolean(); break;
                    case "biosFail": rig.Bios.FailTypes.Add(Hex(action.Value.GetString()!)); break;
                    case "biosOk": rig.Bios.FailTypes.Remove(Hex(action.Value.GetString()!)); break;
                    case "heartbeatPaused": loop.HeartbeatPaused = action.Value.GetBoolean(); break;
                    case "panicEnabled": loop.PanicEnabled = action.Value.GetBoolean(); break;
                    case "advance": rig.Advance(action.Value.GetInt32()); break;
                    case "sample": rig.Platform.UpdateTemperature(); break;
                    case "tick": loop.Tick(); break;
                    case "ticks":
                        for(int i = 0; i < action.Value.GetInt32(); i++) {
                            rig.Advance(step.GetProperty("interval").GetInt32());
                            loop.Tick();
                        }
                        break;
                    case "set": rig.Fans.SetLevels(Level(action.Value), Source(step)); break;
                    case "off": rig.Fans.SetOff(true); break;
                    case "release": rig.Fans.Release(); break;
                    case "releaseIfHeld": rig.Fans.ReleaseIfHeld(); break;
                    case "expect":
                        CheckBoard(v.GetProperty("id").GetString() + " step " + stepNo, action.Value, rig, loop, mark);
                        mark = rig.Bios.Calls.Count;
                        break;
                    default: Assert.Fail("Unknown step " + action.Name); break;
                }
            }

        }

        private static void CheckBoard(string where, JsonElement expect, TestRig rig, SafetyLoop loop, int mark) {
            var calls = rig.Bios.Calls.Skip(mark).Where(c => WriteTypes.Contains(c.Type)).ToList();
            foreach(JsonProperty p in expect.EnumerateObject())
                switch(p.Name) {
                    case "calls":
                        Assert.True(p.Value.EnumerateArray().Select(e => e.GetString()!).SequenceEqual(calls.Select(Format)),
                            where + ": calls were [" + string.Join(", ", calls.Select(Format)) + "]");
                        break;
                    case "writes":
                        var writes = calls.Where(c => c.Type == 0x2E).Select(c => c.Data[0] + "/" + c.Data[1]).ToList();
                        var expected = p.Value.EnumerateArray().Select(l => l[0].GetInt32() + "/" + l[1].GetInt32()).ToList();
                        Assert.True(expected.SequenceEqual(writes), where + ": writes were [" + string.Join(", ", writes) + "]");
                        break;
                    case "measured":
                        Assert.True(Level(p.Value).SequenceEqual(rig.Bios.MeasuredLevels),
                            where + ": measured " + rig.Bios.MeasuredLevels[0] + "/" + rig.Bios.MeasuredLevels[1]);
                        break;
                    case "panic":
                        Assert.True(p.Value.GetBoolean() == loop.Panic.IsActive, where + ": panic state");
                        Assert.True(p.Value.GetBoolean() == rig.Fans.Guard.IsPanic, where + ": guard panic state");
                        break;
                    case "heartbeatAllowed":
                        Assert.True(p.Value.GetBoolean() == rig.Fans.HeartbeatAllowed, where + ": heartbeat allowed");
                        break;
                    case "holds":
                        Assert.True(p.Value.GetBoolean() == rig.Fans.Guard.HoldsLevel, where + ": holds a level");
                        break;
                    default:
                        Assert.Fail(where + ": unknown expectation " + p.Name);
                        break;
                }
        }

        private static string Format((uint Type, byte[] Data) call) {
            var s = new StringBuilder("0x" + call.Type.ToString("X2"));
            if(call.Type == 0x2E)
                s.Append(' ').Append(call.Data[0]).Append('/').Append(call.Data[1]);
            return s.ToString();
        }

        private static uint Hex(string text) {
            return Convert.ToUInt32(text, 16);
        }
#endregion

        private static bool Has(JsonElement e, string name) {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);
        }

        private static byte[] Level(JsonElement l) {
            return new byte[] { l[0].GetByte(), l[1].GetByte() };
        }

        private static FanLevelSource Source(JsonElement e) {
            return Has(e, "source") && e.GetProperty("source").GetString() == "program"
                ? FanLevelSource.Program : FanLevelSource.Manual;
        }

        private static void AssertLevel(JsonElement expect, byte[] actual) {
            if(expect.ValueKind == JsonValueKind.Null)
                Assert.Null(actual);
            else {
                Assert.NotNull(actual);
                Assert.Equal(Level(expect), actual);
            }
        }

    }

}
