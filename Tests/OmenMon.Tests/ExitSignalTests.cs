using System;
using System.Security.Principal;
using System.Threading;
using OmenMon.Library;
using Xunit;

namespace OmenMon.Tests {

    // OmenMon -Exit: the GUI's event, who may set it, and the wait for the GUI to go
    public class ExitSignalTests {

        private static string NewName() {
            return "Local\\OmenMonTest-Exit-" + Guid.NewGuid().ToString("N");
        }

        private static bool Elevated() {
            using(WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [Fact]
        public void Signal_WithoutTheEvent_IsFalse() {
            Assert.False(ExitSignal.Signal(NewName()));
        }

        // Only administrators and SYSTEM may set it: a process without elevation must
        // not be able to stop the fan controller
        [Fact]
        public void Signal_ReachesTheEvent_OnlyWhenElevated() {
            string name = NewName();
            using(EventWaitHandle e = ExitSignal.Create(name)) {
                Assert.NotNull(e);
                bool set = ExitSignal.Signal(name);
                Assert.Equal(Elevated(), set);
                Assert.Equal(set, e.WaitOne(0));
            }
        }

        // An event someone else made first is not trusted (its ACL is theirs)
        [Fact]
        public void Create_RefusesAnExistingEvent() {
            string name = NewName();
            using(EventWaitHandle other = new EventWaitHandle(false, EventResetMode.AutoReset, name))
                Assert.Null(ExitSignal.Create(name));
        }

        [Fact]
        public void WaitGone_TrueOnceGone_FalseAfterTheTimeout() {
            DateTime now = new DateTime(2026, 10, 21);
            int polls = 0;
            Assert.True(ExitSignal.WaitGone(() => ++polls < 4, TimeSpan.FromSeconds(30),
                t => now += t, () => now));
            Assert.Equal(4, polls);

            now = new DateTime(2026, 10, 21);
            Assert.False(ExitSignal.WaitGone(() => true, TimeSpan.FromSeconds(5), t => now += t, () => now));
            Assert.True(now >= new DateTime(2026, 10, 21).AddSeconds(5));
        }

    }

}
