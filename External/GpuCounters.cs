  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OmenMon.Library;

namespace OmenMon.External {

    // Reads the Windows GPU performance counters through PDH (pdh.dll), the source of
    // Task Manager's GPU columns. They come from the OS scheduler's accounting, so
    // reading them does not touch the GPU or its driver: a GPU that sleeps stays asleep.
    // Used by the GPU log (-GpuLog); read-only
    public sealed class GpuCounters : IDisposable {

        private const string Dll = "pdh.dll";
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const uint PDH_MORE_DATA = 0x800007D2;

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport(Dll)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport(Dll)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhCloseQuery(IntPtr query);

        private IntPtr query, engine, memory;

        // Opens the query and adds both counters; throws if the counter sets do not exist
        public GpuCounters() {
            Check(PdhOpenQueryW(null, IntPtr.Zero, out this.query), "PdhOpenQuery");
            Check(PdhAddEnglishCounterW(this.query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out this.engine),
                "GPU Engine counter set");
            Check(PdhAddEnglishCounterW(this.query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out this.memory),
                "GPU Process Memory counter set");
        }

        private static void Check(uint status, string what) {
            if(status != 0)
                throw new InvalidOperationException(what + ": PDH error 0x" + status.ToString("X8"));
        }

        // Takes a sample. The utilization is a rate over the time since the previous call,
        // so the first call only primes it
        public void Collect() {
            Check(PdhCollectQueryData(this.query), "PdhCollectQueryData");
        }

        // The utilization [%] of every engine instance since the last Collect
        public List<GpuCounterValue> ReadEngines() {
            return Read(this.engine);
        }

        // The dedicated memory [bytes] of every process instance
        public List<GpuCounterValue> ReadMemory() {
            return Read(this.memory);
        }

        // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR name; then PDH_FMT_COUNTERVALUE
        // { DWORD CStatus; (padding) double value }
        private static List<GpuCounterValue> Read(IntPtr counter) {
            List<GpuCounterValue> list = new List<GpuCounterValue>();
            uint size = 0;
            uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
            if(status != PDH_MORE_DATA || size == 0)
                return list; // No instances (or an error that the next sample may not repeat)

            IntPtr buffer = Marshal.AllocHGlobal((int) size);
            try {
                status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
                if(status != 0)
                    return list;
                int stride = IntPtr.Size + 16;
                for(int i = 0; i < count; i++) {
                    IntPtr item = IntPtr.Add(buffer, i * stride);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    int itemStatus = Marshal.ReadInt32(item, IntPtr.Size);
                    // CStatus 0 = valid, 0x00000001 = new data
                    if(name == null || (itemStatus != 0 && itemStatus != 1))
                        continue;
                    long bits = Marshal.ReadInt64(item, IntPtr.Size + 8);
                    list.Add(new GpuCounterValue(name, BitConverter.Int64BitsToDouble(bits)));
                }
            } finally {
                Marshal.FreeHGlobal(buffer);
            }
            return list;
        }

        public void Dispose() {
            if(this.query != IntPtr.Zero) {
                PdhCloseQuery(this.query);
                this.query = IntPtr.Zero;
            }
        }

    }

    // Finds the graphics adapters by their DXGI description (dxgi.dll), without opening a
    // device: the description is the OS's own record of the adapter, so it does not wake
    // a sleeping GPU. The discrete GPU is the adapter of vendor 0x10DE (NVIDIA) that is
    // not the software renderer
    public static class DxgiAdapters {

        public sealed class Adapter {
            public string Description;
            public uint VendorId;
            public bool Software;
            public string Luid;    // as in the performance counters' instance names
        }

        public const uint VendorNvidia = 0x10DE;

        [DllImport("dxgi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);

        private const int DXGI_ADAPTER_FLAG_SOFTWARE = 2;
        private const int DescSize = 320;   // DXGI_ADAPTER_DESC1 is 312 bytes

        // The method at a position of an object's virtual table
        private static T Method<T>(IntPtr obj, int index) where T : class {
            IntPtr table = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, index * IntPtr.Size), typeof(T)) as T;
        }

        // All adapters, in DXGI order; an empty list if DXGI is not available
        public static List<Adapter> List() {
            List<Adapter> list = new List<Adapter>();
            IntPtr factory = IntPtr.Zero;
            IntPtr desc = Marshal.AllocHGlobal(DescSize);
            try {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if(CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero)
                    return list;
                EnumAdapters1Fn enumerate = Method<EnumAdapters1Fn>(factory, 12); // IDXGIFactory1::EnumAdapters1
                for(uint i = 0; i < 64; i++) {
                    if(enumerate(factory, i, out IntPtr adapter) < 0 || adapter == IntPtr.Zero)
                        break; // DXGI_ERROR_NOT_FOUND: no more
                    try {
                        // IDXGIAdapter1::GetDesc1: Description[128] (WCHAR), VendorId at 256,
                        // ... LUID at 296 (low part, high part), Flags at 304
                        if(Method<GetDesc1Fn>(adapter, 10)(adapter, desc) < 0)
                            continue;
                        list.Add(new Adapter {
                            Description = Marshal.PtrToStringUni(desc, 128).Split('\0')[0],
                            VendorId = (uint) Marshal.ReadInt32(desc, 256),
                            Luid = GpuInstance.FormatLuid((uint) Marshal.ReadInt32(desc, 296), Marshal.ReadInt32(desc, 300)),
                            Software = (Marshal.ReadInt32(desc, 304) & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                        });
                    } finally {
                        Method<ReleaseFn>(adapter, 2)(adapter);
                    }
                }
            } catch {
                // DXGI missing or unusable: report what was found so far
            } finally {
                Marshal.FreeHGlobal(desc);
                if(factory != IntPtr.Zero)
                    try { Method<ReleaseFn>(factory, 2)(factory); } catch { }
            }
            return list;
        }

    }

    // Process name and executable path from a process ID, without needing elevation
    // for processes of the same session (a protected or elevated process may only give a name)
    public sealed class ProcessNames {

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseHandle(IntPtr handle);

        private readonly Dictionary<int, GpuProcess> cache = new Dictionary<int, GpuProcess>();

        // Remembers what it found; a process it could not name is looked up again next time
        public GpuProcess Get(int pid) {
            if(this.cache.TryGetValue(pid, out GpuProcess known))
                return known;

            GpuProcess p = new GpuProcess();
            if(pid == 0)
                p.Name = "Idle";
            else if(pid == 4)
                p.Name = "System";
            else {
                IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if(handle != IntPtr.Zero)
                    try {
                        StringBuilder sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if(QueryFullProcessImageNameW(handle, 0, sb, ref size)) {
                            p.Path = sb.ToString();
                            p.Name = Path.GetFileNameWithoutExtension(p.Path);
                        }
                    } finally {
                        CloseHandle(handle);
                    }
                if(p.Name == null)
                    try { p.Name = Process.GetProcessById(pid).ProcessName; } catch { }
            }
            if(p.Name != null)
                this.cache[pid] = p;
            return p;
        }

    }

}
