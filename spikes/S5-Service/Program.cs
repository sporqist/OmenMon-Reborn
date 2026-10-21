// SPIKE S5 (service half): an empty Windows service as a NativeAOT exe, to measure what the
// elevated part of the restructured app costs before it does anything (PLAN.md section 2:
// target <= 8 MB private, accept <= 20 MB, <= 4 threads, <= 0.01 % CPU).
//
// It touches no hardware and makes no BIOS, WMI or EC call. The control loop is one wait
// with a 1 s timeout (the one control timer of PLAN section 2) that does nothing yet.
// Raw advapi32 calls instead of Microsoft.Extensions.Hosting: no package, smallest binary.
//
// Usage: see README.md. --console runs the same loop in a console (Ctrl+C ends it).

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OmenMon.Spikes.Service {

    internal static unsafe class Program {

        // Unique to the spike: never the name of anything an OmenMon copy installs
        private const string ServiceName = "OmenCtrlSpikeS5";

        private static readonly ManualResetEvent Stop = new ManualResetEvent(false);
        private static nint statusHandle;
        private static long passes;

        private static int Main(string[] args) {

            if(args.Length > 0 && args[0] == "--console") {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; Stop.Set(); };
                Console.WriteLine(ServiceName + ": console mode, Ctrl+C to stop");
                Loop();
                Console.WriteLine("passes: " + passes);
                return 0;
            }

            fixed(char* name = ServiceName) {
                ServiceTableEntry* table = stackalloc ServiceTableEntry[2];
                table[0] = new ServiceTableEntry { Name = name, Proc = &ServiceMain };
                table[1] = default;
                if(Native.StartServiceCtrlDispatcherW(table) != 0)
                    return 0;
            }

            int error = Marshal.GetLastSystemError();
            Console.Error.WriteLine(error == Native.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
                ? ServiceName + " runs as a service (see README.md), or with --console"
                : "StartServiceCtrlDispatcher failed: " + error);
            return 1;

        }

        [UnmanagedCallersOnly]
        private static void ServiceMain(uint argc, char** argv) {
            fixed(char* name = ServiceName)
                statusHandle = Native.RegisterServiceCtrlHandlerExW(name, &Handler, null);
            if(statusHandle == 0)
                return;
            Report(Native.SERVICE_RUNNING, 0);
            Loop();
            Report(Native.SERVICE_STOPPED, 0);
        }

        // The control loop: one wait, one pass a second, nothing in it yet
        private static void Loop() {
            while(!Stop.WaitOne(1000))
                passes++;
        }

        [UnmanagedCallersOnly]
        private static uint Handler(uint control, uint eventType, void* eventData, void* context) {
            switch(control) {
                case Native.SERVICE_CONTROL_STOP:
                case Native.SERVICE_CONTROL_SHUTDOWN:
                    Report(Native.SERVICE_STOP_PENDING, 2000);
                    Stop.Set();
                    return 0;
                case Native.SERVICE_CONTROL_POWEREVENT:   // the real service hands back on PBT_APMSUSPEND
                case Native.SERVICE_CONTROL_INTERROGATE:
                    return 0;
                default:
                    return Native.ERROR_CALL_NOT_IMPLEMENTED;
            }
        }

        private static void Report(uint state, uint waitHint) {
            ServiceStatus status = new ServiceStatus {
                ServiceType = Native.SERVICE_WIN32_OWN_PROCESS,
                CurrentState = state,
                ControlsAccepted = state == Native.SERVICE_RUNNING
                    ? Native.SERVICE_ACCEPT_STOP | Native.SERVICE_ACCEPT_SHUTDOWN | Native.SERVICE_ACCEPT_POWEREVENT : 0,
                WaitHint = waitHint };
            Native.SetServiceStatus(statusHandle, &status);
        }

    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ServiceTableEntry {
        public char* Name;
        public delegate* unmanaged<uint, char**, void> Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    internal static unsafe class Native {

        public const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
        public const uint SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
        public const uint SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4, SERVICE_ACCEPT_POWEREVENT = 0x40;
        public const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_POWEREVENT = 0x0D;
        public const uint ERROR_CALL_NOT_IMPLEMENTED = 120;
        public const int ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern int StartServiceCtrlDispatcherW(ServiceTableEntry* table);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint RegisterServiceCtrlHandlerExW(char* name,
            delegate* unmanaged<uint, uint, void*, void*, uint> handler, void* context);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern int SetServiceStatus(nint handle, ServiceStatus* status);

    }

}
