  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Text.RegularExpressions;

namespace OmenMon.Tests {

    // Source tripwires for the restructuring spikes (spikes\, not in the solution). Spike
    // S1 talks to the real BIOS when the owner runs it, so it must stay read-only: only
    // the fan-level read (0x2D) and the temperature read (0x23), never the fan-level set,
    // the keep-alive or any other write type.
    public class SpikeTripwireTests {

        private static string Root() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            return root;
        }

        private static string[] S1Sources() {
            string dir = Path.Combine(Root(), "spikes", "S1-WmiRead");
            return Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToArray();
        }

        [Fact]
        public void SpikeS1_SendsOnlyTheTwoReads() {
            string[] files = S1Sources();
            Assert.NotEmpty(files);
            string code = string.Join("\n", files.Select(File.ReadAllText));

            // The allowlist holds the two read types and nothing else
            Assert.Matches(@"const uint TypeFanLevels = 0x2D;", code);
            Assert.Matches(@"const uint TypeTemperature = 0x23;", code);
            Assert.Matches(@"ReadOnlyTypes = \{ TypeFanLevels, TypeTemperature \};", code);

            // Every call names one of them
            var calls = Regex.Matches(code, @"\.ReadBios\(\s*([\w.]+)").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, c => Assert.Matches(@"^(HpBiosReader\.)?(TypeFanLevels|TypeTemperature)$", c));

            // No write type appears at all: fan levels, Max Fan, fan mode, fan table,
            // power limits, MUX, NVRAM
            foreach(string type in new[] { "0x2E", "0x27", "0x1A", "0x32", "0x29", "0x52", "0x53" })
                Assert.DoesNotMatch("(?i)" + type + @"\b", code);
        }

    }

}
