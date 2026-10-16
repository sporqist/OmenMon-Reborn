  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System.Runtime.CompilerServices;
using OmenMon.Library;

namespace OmenMon.Tests.Fakes {

    // Runs when the test assembly loads, before any test: from then on the application
    // code in this process cannot open a BIOS WMI session or the EC driver (PawnIO), so a
    // code path that falls back to the live hardware fails closed instead.
    internal static class HardwareIsolation {

        [ModuleInitializer]
        internal static void Isolate() {
            Hw.Isolated = true;
        }

    }

}
