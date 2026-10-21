  //\   OmenMon: Hardware Monitoring & Control Utility
 //  \  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using OmenMon.Library;

namespace OmenMon.Tests {

    // The logon task name is shared by every copy of the application: a copy with
    // AutoStartup off only removes the task if it starts that very executable.
    // Found on hardware (2026-10-16): a test copy unregistered the installed one's task.
    public class TaskOwnershipTests {

        [Theory]
        [InlineData(@"C:\Program Files\OmenMon\OmenMon.exe", @"C:\Program Files\OmenMon\OmenMon.exe")]
        [InlineData(@"""C:\Program Files\OmenMon\OmenMon.exe""", @"C:\Program Files\OmenMon\OmenMon.exe")]
        [InlineData(@"c:\program files\omenmon\OMENMON.EXE", @"C:\Program Files\OmenMon\OmenMon.exe")]
        [InlineData(@"C:\Program Files\OmenMon\..\OmenMon\OmenMon.exe", @"C:\Program Files\OmenMon\OmenMon.exe")]
        public void SameExecutable(string taskCommand, string appFile) {
            Assert.True(Hw.IsSameFile(taskCommand, appFile));
        }

        [Theory]
        [InlineData(@"C:\Program Files\OmenMon\OmenMon.exe", @"C:\Temp\hwtest\OmenMon.exe")]
        [InlineData(@"C:\Program Files\OmenMon\OmenMon.exe", @"C:\Program Files\OmenMon\OmenMon2.exe")]
        [InlineData("", @"C:\Program Files\OmenMon\OmenMon.exe")]
        [InlineData("\0bad", @"C:\Program Files\OmenMon\OmenMon.exe")]
        public void OtherExecutable(string taskCommand, string appFile) {
            Assert.False(Hw.IsSameFile(taskCommand, appFile));
        }

    }

}
