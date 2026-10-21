  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using OmenMon.Library;

namespace OmenMon.Tests {

    // The GPU log's instance names, aggregation, CSV and summary (-GpuLog); the PDH and
    // DXGI readers are thin and only run on a machine with a GPU
    public class GpuLogTests {

        private const string Dgpu = "0x00000000_0x0000C8E5";
        private const string Igpu = "0x00000000_0x0000D1A0";

        private static string Eng(int pid, string luid, string type, int eng = 0) {
            return "pid_" + pid + "_luid_" + luid + "_phys_0_eng_" + eng + "_engtype_" + type;
        }

        private static string Mem(int pid, string luid) {
            return "pid_" + pid + "_luid_" + luid + "_phys_0";
        }

        private static GpuProcess? Resolve(int pid) {
            switch(pid) {
                case 100: return new GpuProcess { Name = "TouchDesigner", Path = @"C:\td\TouchDesigner.exe" };
                case 200: return new GpuProcess { Name = "chrome", Path = @"C:\c\chrome.exe" };
                case 201: return new GpuProcess { Name = "chrome", Path = @"C:\c\chrome.exe" };
                default: return null;
            }
        }

        [Fact]
        public void ParsesEngineInstance() {
            Assert.True(GpuInstance.TryParse(Eng(1234, "0x00000000_0x0000c8e5", "3D"), out GpuInstance i));
            Assert.Equal(1234, i.Pid);
            Assert.Equal(Dgpu, i.Luid);
            Assert.Equal("3D", i.EngineType);
        }

        [Theory]
        [InlineData("3D", "3D")]
        [InlineData("Compute_0", "Compute")]
        [InlineData("Cuda", "Compute")]
        [InlineData("Copy", "Copy")]
        [InlineData("VideoDecode", "VideoDecode")]
        [InlineData("VideoEncode", "VideoEncode")]
        [InlineData("VideoProcessing", "Other")]
        [InlineData("Security", "Other")]
        public void NormalizesEngineTypes(string raw, string expected) {
            Assert.True(GpuInstance.TryParse(Eng(1, Dgpu, raw), out GpuInstance i));
            Assert.Equal(expected, i.EngineType);
        }

        [Fact]
        public void ParsesMemoryInstanceWithoutEngineType() {
            Assert.True(GpuInstance.TryParse(Mem(7, Igpu), out GpuInstance i));
            Assert.Null(i.EngineType);
            Assert.Equal(Igpu, i.Luid);
        }

        [Theory]
        [InlineData("")]
        [InlineData("_Total")]
        [InlineData("pid_x_luid_0x0_0x1_phys_0")]
        [InlineData("pid_4_luid_zz_0x1_phys_0")]
        [InlineData("pid_4_foo_0x0_0x1_phys_0")]
        public void RejectsOtherNames(string name) {
            Assert.False(GpuInstance.TryParse(name, out _));
        }

        [Fact]
        public void FormatsAdapterLuidLikeTheCounters() {
            Assert.Equal(Dgpu, GpuInstance.FormatLuid(0xC8E5, 0));
        }

        [Fact]
        public void SumsEnginesOfATypeAndKeepsOnlyTheDiscreteGpu() {
            var engines = new[] {
                new GpuCounterValue(Eng(100, Dgpu, "3D", 0), 30),
                new GpuCounterValue(Eng(100, Dgpu, "3D", 1), 5),
                new GpuCounterValue(Eng(100, Dgpu, "Copy"), 2),
                new GpuCounterValue(Eng(100, Igpu, "3D"), 90),   // other GPU
                new GpuCounterValue(Eng(200, Dgpu, "3D"), 0),    // idle, no memory: no row
                new GpuCounterValue("_Total", 5),
            };
            var memory = new[] {
                new GpuCounterValue(Mem(100, Dgpu), 2048.0 * 1024 * 1024),
                new GpuCounterValue(Mem(100, Igpu), 99.0 * 1024 * 1024),
            };
            var rows = GpuLogMath.Aggregate(new DateTime(2026, 1, 1), engines, memory, new[] { Dgpu }, Resolve, "D0 (on)");

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("TouchDesigner", r.Process));
            Assert.All(rows, r => Assert.Equal("dGPU", r.Gpu));
            Assert.Equal(35, rows.Single(r => r.EngineType == "3D").Utilization);
            Assert.Equal(2, rows.Single(r => r.EngineType == "Copy").Utilization);
            Assert.Equal(2048, rows[0].DedicatedMB);
        }

        [Fact]
        public void MemoryOnlyProcessGetsOneRow() {
            var rows = GpuLogMath.Aggregate(DateTime.Now, null,
                new[] { new GpuCounterValue(Mem(200, Dgpu), 100.0 * 1024 * 1024) }, new[] { Dgpu }, Resolve, "D0 (on)");
            Assert.Single(rows);
            Assert.Equal("-", rows[0].EngineType);
            Assert.Equal(100, rows[0].DedicatedMB);
        }

        [Fact]
        public void WithoutALuidEveryGpuIsKeptUnderItsOwn() {
            var engines = new[] {
                new GpuCounterValue(Eng(100, Dgpu, "3D"), 10),
                new GpuCounterValue(Eng(100, Igpu, "3D"), 20),
            };
            var rows = GpuLogMath.Aggregate(DateTime.Now, engines, null, null, Resolve, "unknown");
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Gpu == Dgpu && r.Utilization == 10);
            Assert.Contains(rows, r => r.Gpu == Igpu && r.Utilization == 20);
        }

        [Fact]
        public void UnknownProcessIsNamedByItsPid() {
            var rows = GpuLogMath.Aggregate(DateTime.Now, new[] { new GpuCounterValue(Eng(999, Dgpu, "3D"), 1) },
                null, new[] { Dgpu }, Resolve, "D0 (on)");
            Assert.Equal("pid 999", rows.Single().Process);
        }

        [Fact]
        public void CsvQuotesFieldsAndKeepsTheColumnCount() {
            var row = new GpuLogRow {
                Time = new DateTime(2026, 10, 21, 12, 0, 2), Pid = 5, Process = "a,\"b\"", Path = @"C:\x y\a.exe",
                EngineType = "3D", Utilization = 12.34, DedicatedMB = 5.5, PowerState = "D0 (on)", Gpu = "dGPU"
            };
            string line = GpuLogMath.CsvLine(row);
            Assert.Equal("2026-10-21 12:00:02,5,\"a,\"\"b\"\"\",3D,12.3,5.5,D0 (on),dGPU,C:\\x y\\a.exe", line);
            Assert.Equal(9, GpuLogMath.CsvHeader.Split(',').Length);
        }

        [Fact]
        public void SummaryAveragesOverTheWholeRunAndFlagsOtherApps() {
            GpuLogSummary summary = new GpuLogSummary(2);
            DateTime t = new DateTime(2026, 1, 1);

            // Sample 1: TouchDesigner 40 % 3D; two chrome processes 6 and 4 % 3D, 100 MB each
            summary.Add(new[] {
                new GpuLogRow { Time = t, Pid = 100, Process = "TouchDesigner", EngineType = "3D", Utilization = 40, DedicatedMB = 3000 },
                new GpuLogRow { Time = t, Pid = 200, Process = "chrome", EngineType = "3D", Utilization = 6, DedicatedMB = 100 },
                new GpuLogRow { Time = t, Pid = 201, Process = "chrome", EngineType = "3D", Utilization = 4, DedicatedMB = 100 },
            });
            // Sample 2: only TouchDesigner
            summary.Add(new[] {
                new GpuLogRow { Time = t, Pid = 100, Process = "TouchDesigner", EngineType = "3D", Utilization = 60, DedicatedMB = 3100 },
            });

            Assert.Equal(2, summary.Samples);
            var chrome = summary.Apps["chrome"];
            Assert.Equal(5, chrome.Average("3D", summary.Samples));   // 10 % in one of two samples
            Assert.Equal(10, chrome.PeakOf("3D"));
            Assert.Equal(200, chrome.PeakMB);                         // memory once per process, summed over processes
            Assert.Equal(1, chrome.ActiveSamples);
            Assert.True(GpuLogSummary.Flagged(chrome));
            Assert.False(GpuLogSummary.Flagged(summary.Apps["TouchDesigner"]));

            var td = summary.Apps["TouchDesigner"];
            Assert.Equal(50, td.Average("3D", summary.Samples));
            Assert.Equal(3100, td.PeakMB);

            string text = summary.Format(null);
            Assert.Contains("Apps other than TouchDesigner that used the discrete GPU: chrome", text);
            Assert.Contains("* chrome", text);
            Assert.Contains("busy 2 s", text);
        }

        [Fact]
        public void SummaryOfAnIdleRunSaysSo() {
            GpuLogSummary summary = new GpuLogSummary(2);
            summary.Add(new GpuLogRow[0]);
            string text = summary.Format("dGPU power state: D3 (off) 2 s");
            Assert.Contains("no process used the GPU", text);
            Assert.Contains("No app other than TouchDesigner used the discrete GPU.", text);
        }

        [Fact]
        public void ParsesArguments() {
            Assert.True(GpuLogMath.TryParseArgs(new string[0], out int s, out string f));
            Assert.Equal(600, s);
            Assert.Null(f);

            Assert.True(GpuLogMath.TryParseArgs(new[] { "30", "out.csv" }, out s, out f));
            Assert.Equal(30, s);
            Assert.Equal("out.csv", f);

            Assert.True(GpuLogMath.TryParseArgs(new[] { "out.csv", "30" }, out s, out f));
            Assert.Equal(30, s);

            Assert.False(GpuLogMath.TryParseArgs(new[] { "0" }, out _, out _));
            Assert.False(GpuLogMath.TryParseArgs(new[] { "10", "20" }, out _, out _));
            Assert.False(GpuLogMath.TryParseArgs(new[] { "a.csv", "b.csv" }, out _, out _));
        }

        [Fact]
        public void DefaultFileNameHasTheTimestamp() {
            Assert.Equal("OmenMon-GpuLog-20261021-130501.csv", GpuLogMath.DefaultFileName(new DateTime(2026, 10, 21, 13, 5, 1)));
        }

    }

}
