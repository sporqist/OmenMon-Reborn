// ===========================================================================================
//  SPIKE S1 - READ ONLY. NEVER CHANGES FAN, EC OR BIOS STATE.
//
//  This program sends exactly two kinds of BIOS WMI call, both reads:
//    command 0x20008, type 0x2D      - the measured fan levels [cpu, gpu] (rpm / 100)
//    command 0x20008, type 0x23 [1]  - sensor 1, the CPU temperature (EC 0xB2) in C
//  It contains no fan-level write and no other write type. Every call goes through
//  ReadBios(), which refuses any type outside ReadOnlyTypes before anything is sent.
//  Do not add a type to that list without the owner's go and the hardware rules in
//  CLAUDE.md (0/0 turns both fans off on 8BAC; the firmware checks no bounds).
// ===========================================================================================
//
// What it proves: a NativeAOT exe can call root\wmi hpqBIntM (signature "SECU") through
// plain COM (IWbemLocator / IWbemServices / IWbemClassObject vtables, function pointers,
// no System.Management, no runtime marshalling), as PLAN section 5 needs for the service.
//
// Usage (elevated; see README.md):  OmenMon.Spike.WmiRead.exe [samples] [intervalMs]

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace OmenMon.Spikes.WmiRead {

    internal static class Program {

        private static int Main(string[] args) {

            int samples = args.Length > 0 && int.TryParse(args[0], out int n) && n > 0 ? Math.Min(n, 3600) : 1;
            int interval = args.Length > 1 && int.TryParse(args[1], out int ms) && ms >= 100 ? ms : 1000;

            var clock = Stopwatch.StartNew();
            try {
                using(var bios = HpBiosReader.Connect()) {
                    Console.WriteLine("connect: " + clock.Elapsed.TotalMilliseconds.ToString("F1") + " ms, instance " + bios.InstancePath);
                    for(int i = 0; i < samples; i++) {
                        if(i > 0)
                            System.Threading.Thread.Sleep(interval);

                        long t0 = Stopwatch.GetTimestamp();
                        int rcFan = bios.ReadBios(HpBiosReader.TypeFanLevels, new byte[] { 0, 0, 0, 0 }, 128, out byte[] fan);
                        long t1 = Stopwatch.GetTimestamp();
                        int rcTemp = bios.ReadBios(HpBiosReader.TypeTemperature, new byte[] { 1, 0, 0, 0 }, 4, out byte[] temp);
                        long t2 = Stopwatch.GetTimestamp();

                        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss")
                            + "  fans " + At(fan, 0) + "/" + At(fan, 1) + " (rpm/100, rc " + rcFan + ", "
                            + Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds.ToString("F1") + " ms)"
                            + "  cpu " + At(temp, 0) + " C (rc " + rcTemp + ", "
                            + Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds.ToString("F1") + " ms)");
                    }
                }
            } catch(Exception e) {
                Console.Error.WriteLine("failed: " + e.Message);
                return 1;
            } finally {
                using(Process self = Process.GetCurrentProcess())
                    Console.WriteLine("process: private " + (self.PrivateMemorySize64 / 1024) + " KB, working set "
                        + (self.WorkingSet64 / 1024) + " KB, threads " + self.Threads.Count);
            }
            return 0;

        }

        private static string At(byte[] data, int index) {
            return data != null && data.Length > index ? data[index].ToString() : "?";
        }

    }

    // The BIOS WMI interface of HP OMEN/Victus laptops, read side only
    internal sealed unsafe class HpBiosReader : IDisposable {

        public const uint TypeFanLevels = 0x2D;
        public const uint TypeTemperature = 0x23;

        // The only types this spike may send. Both are reads.
        private static readonly uint[] ReadOnlyTypes = { TypeFanLevels, TypeTemperature };

        private const uint CommandDefault = 0x20008;
        private static readonly byte[] Sign = "SECU"u8.ToArray();

        private const string Namespace = @"ROOT\WMI";
        private const string MethodClass = "hpqBIntM";
        private const string DataClass = "hpqBDataIn";
        private const string MethodPrefix = "hpqBIOSInt"; // + output size: 0, 4, 128, 1024, 4096

        private void* services;      // IWbemServices
        private void* methodClass;   // IWbemClassObject, class hpqBIntM
        private void* dataClass;     // IWbemClassObject, class hpqBDataIn

        public string InstancePath { get; private set; } = "";

        private HpBiosReader() { }

        public static HpBiosReader Connect() {
            var reader = new HpBiosReader();
            try {
                reader.Open();
                return reader;
            } catch {
                reader.Dispose();
                throw;
            }
        }

        private void Open() {

            Com.Check(Com.CoInitializeEx(null, Com.COINIT_MULTITHREADED), "CoInitializeEx");
            int hr = Com.CoInitializeSecurity(null, -1, null, null, Com.RPC_C_AUTHN_LEVEL_DEFAULT,
                Com.RPC_C_IMP_LEVEL_IMPERSONATE, null, Com.EOAC_NONE, null);
            if(hr < 0 && hr != Com.RPC_E_TOO_LATE)
                Com.Check(hr, "CoInitializeSecurity");

            void* locator = null;
            Guid clsid = Com.CLSID_WbemLocator, iid = Com.IID_IWbemLocator;
            Com.Check(Com.CoCreateInstance(&clsid, null, Com.CLSCTX_INPROC_SERVER, &iid, &locator), "CoCreateInstance(WbemLocator)");
            try {
                using(var ns = new Bstr(Namespace)) {
                    void* svc = null;
                    // IWbemLocator::ConnectServer
                    Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, char*, char*, char*, int, char*, void*, void**, int>)
                        Com.Slot(locator, 3))(locator, ns.Ptr, null, null, null, 0, null, null, &svc), "ConnectServer(" + Namespace + ")");
                    this.services = svc;
                }
            } finally {
                Com.Release(locator);
            }

            Com.Check(Com.CoSetProxyBlanket(this.services, Com.RPC_C_AUTHN_WINNT, Com.RPC_C_AUTHZ_NONE, null,
                Com.RPC_C_AUTHN_LEVEL_CALL, Com.RPC_C_IMP_LEVEL_IMPERSONATE, null, Com.EOAC_NONE), "CoSetProxyBlanket");

            this.methodClass = GetObject(MethodClass);
            this.dataClass = GetObject(DataClass);
            this.InstancePath = FindInstancePath();

        }

        // Sends one read. Returns the BIOS return code (0 = OK), or throws on a COM failure.
        public int ReadBios(uint commandType, byte[] input, int outSize, out byte[] output) {

            // The read-only allowlist: nothing else is ever sent
            if(Array.IndexOf(ReadOnlyTypes, commandType) < 0)
                throw new InvalidOperationException("Refused: type 0x" + commandType.ToString("X2") + " is not a read this spike may send");
            if(outSize != 4 && outSize != 128)
                throw new ArgumentOutOfRangeException(nameof(outSize));

            output = Array.Empty<byte>();
            void* data = null, inSignature = null, outSignature = null, inParams = null, outParams = null, outData = null;
            try {

                // hpqBDataIn { Sign, Command, CommandType, Size, hpqBData }
                data = Spawn(this.dataClass, DataClass);
                PutBytes(data, "Sign", Sign);
                PutInt(data, "Command", (int) CommandDefault);
                PutInt(data, "CommandType", (int) commandType);
                PutInt(data, "Size", input.Length);
                PutBytes(data, "hpqBData", input);

                // The method's in-parameters: InData = the instance above
                string method = MethodPrefix + outSize;
                fixed(char* m = method)
                    Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, int, void**, void**, int>)
                        Com.Slot(this.methodClass, 19))(this.methodClass, m, 0, &inSignature, &outSignature), "GetMethod(" + method + ")");
                inParams = Spawn(inSignature, method + " in-parameters");
                PutObject(inParams, "InData", data);

                // IWbemServices::ExecMethod
                using(var path = new Bstr(this.InstancePath))
                using(var name = new Bstr(method))
                    Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, char*, int, void*, void*, void**, void*, int>)
                        Com.Slot(this.services, 24))(this.services, path.Ptr, name.Ptr, 0, null, inParams, &outParams, null), "ExecMethod(" + method + ")");

                // OutData { rwReturnCode, Data }
                outData = GetObjectProperty(outParams, "OutData");
                int rc = GetInt(outData, "rwReturnCode");
                output = GetBytes(outData, "Data");
                return rc;

            } finally {
                Com.Release(outData);
                Com.Release(outParams);
                Com.Release(inParams);
                Com.Release(outSignature);
                Com.Release(inSignature);
                Com.Release(data);
            }

        }

        // IWbemClassObject::SpawnInstance (slot 15)
        private static void* Spawn(void* cls, string what) {
            void* instance = null;
            Com.Check(((delegate* unmanaged[Stdcall]<void*, int, void**, int>)
                Com.Slot(cls, 15))(cls, 0, &instance), "SpawnInstance(" + what + ")");
            return instance;
        }

        private void* GetObject(string path) {
            void* obj = null;
            using(var p = new Bstr(path))
                // IWbemServices::GetObject
                Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, int, void*, void**, void*, int>)
                    Com.Slot(this.services, 6))(this.services, p.Ptr, 0, null, &obj, null), "GetObject(" + path + ")");
            return obj;
        }

        // The relative path of the (single) hpqBIntM instance, e.g. hpqBIntM.InstanceName="ACPI\\PNP0C14\\0_0"
        private string FindInstancePath() {
            void* enumerator = null, instance = null;
            try {
                using(var wql = new Bstr("WQL"))
                using(var query = new Bstr("SELECT * FROM " + MethodClass))
                    // IWbemServices::ExecQuery
                    Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, char*, int, void*, void**, int>)
                        Com.Slot(this.services, 20))(this.services, wql.Ptr, query.Ptr,
                        Com.WBEM_FLAG_FORWARD_ONLY | Com.WBEM_FLAG_RETURN_IMMEDIATELY, null, &enumerator), "ExecQuery(" + MethodClass + ")");
                uint returned = 0;
                // IEnumWbemClassObject::Next
                Com.Check(((delegate* unmanaged[Stdcall]<void*, int, uint, void**, uint*, int>)
                    Com.Slot(enumerator, 4))(enumerator, Com.WBEM_INFINITE, 1, &instance, &returned), "Next(" + MethodClass + ")");
                if(returned == 0)
                    throw new InvalidOperationException("No " + MethodClass + " instance (not an HP BIOS WMI interface?)");
                return GetString(instance, "__RELPATH");
            } finally {
                Com.Release(instance);
                Com.Release(enumerator);
            }
        }

#region IWbemClassObject Get / Put
        // IWbemClassObject::Put (slot 5); type 0: the class defines it
        private static void Put(void* obj, string name, Variant* value) {
            fixed(char* n = name)
                Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, int, Variant*, int, int>)
                    Com.Slot(obj, 5))(obj, n, 0, value, 0), "Put(" + name + ")");
        }

        // IWbemClassObject::Get (slot 4)
        private static void Get(void* obj, string name, Variant* value) {
            fixed(char* n = name)
                Com.Check(((delegate* unmanaged[Stdcall]<void*, char*, int, Variant*, int*, int*, int>)
                    Com.Slot(obj, 4))(obj, n, 0, value, null, null), "Get(" + name + ")");
        }

        private static void PutInt(void* obj, string name, int value) {
            Variant v = default;
            v.Type = Com.VT_I4;
            v.Int = value;
            Put(obj, name, &v);
        }

        private static void PutBytes(void* obj, string name, byte[] value) {
            Variant v = default;
            v.Type = Com.VT_ARRAY | Com.VT_UI1;
            v.Pointer = Com.SafeArrayCreateVector(Com.VT_UI1, 0, (uint) value.Length);
            if(v.Pointer == null)
                throw new OutOfMemoryException();
            try {
                void* target;
                Com.Check(Com.SafeArrayAccessData(v.Pointer, &target), "SafeArrayAccessData");
                value.CopyTo(new Span<byte>(target, value.Length));
                Com.SafeArrayUnaccessData(v.Pointer);
                Put(obj, name, &v);
            } finally {
                Com.VariantClear(&v);
            }
        }

        // The embedded instance is only borrowed: WMI takes its own reference
        private static void PutObject(void* obj, string name, void* value) {
            Variant v = default;
            v.Type = Com.VT_UNKNOWN;
            v.Pointer = value;
            Put(obj, name, &v);
        }

        private static int GetInt(void* obj, string name) {
            Variant v = default;
            Get(obj, name, &v);
            try {
                return v.Type == Com.VT_I4 ? v.Int : throw new InvalidOperationException(name + ": unexpected VARIANT type " + v.Type);
            } finally {
                Com.VariantClear(&v);
            }
        }

        private static string GetString(void* obj, string name) {
            Variant v = default;
            Get(obj, name, &v);
            try {
                return v.Type == Com.VT_BSTR && v.Pointer != null ? new string((char*) v.Pointer)
                    : throw new InvalidOperationException(name + ": unexpected VARIANT type " + v.Type);
            } finally {
                Com.VariantClear(&v);
            }
        }

        private static byte[] GetBytes(void* obj, string name) {
            Variant v = default;
            Get(obj, name, &v);
            try {
                if(v.Type == Com.VT_NULL || v.Type == Com.VT_EMPTY)
                    return Array.Empty<byte>();
                if(v.Type != (Com.VT_ARRAY | Com.VT_UI1))
                    throw new InvalidOperationException(name + ": unexpected VARIANT type " + v.Type);
                int upper;
                Com.Check(Com.SafeArrayGetUBound(v.Pointer, 1, &upper), "SafeArrayGetUBound");
                byte[] result = new byte[upper + 1];
                void* source;
                Com.Check(Com.SafeArrayAccessData(v.Pointer, &source), "SafeArrayAccessData");
                new ReadOnlySpan<byte>(source, result.Length).CopyTo(result);
                Com.SafeArrayUnaccessData(v.Pointer);
                return result;
            } finally {
                Com.VariantClear(&v);
            }
        }

        // Returns an AddRef'd IWbemClassObject held in an object property
        private static void* GetObjectProperty(void* obj, string name) {
            Variant v = default;
            Get(obj, name, &v);
            try {
                if(v.Type != Com.VT_UNKNOWN || v.Pointer == null)
                    throw new InvalidOperationException(name + ": unexpected VARIANT type " + v.Type);
                void* result = null;
                Guid iid = Com.IID_IWbemClassObject;
                Com.Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)
                    Com.Slot(v.Pointer, 0))(v.Pointer, &iid, &result), "QueryInterface(IWbemClassObject)");
                return result;
            } finally {
                Com.VariantClear(&v);
            }
        }
#endregion

        public void Dispose() {
            Com.Release(this.dataClass);
            Com.Release(this.methodClass);
            Com.Release(this.services);
            this.dataClass = this.methodClass = this.services = null;
        }

    }

    // A BSTR for the duration of a call
    internal readonly unsafe struct Bstr : IDisposable {
        public readonly char* Ptr;
        public Bstr(string value) {
            fixed(char* s = value)
                this.Ptr = Com.SysAllocString(s);
            if(this.Ptr == null)
                throw new OutOfMemoryException();
        }
        public void Dispose() {
            Com.SysFreeString(this.Ptr);
        }
    }

    // VARIANT (x64: 24 bytes; the value at offset 8)
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal unsafe struct Variant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public int Int;
        [FieldOffset(8)] public void* Pointer;
    }

    // COM plumbing: blittable P/Invokes and vtable slots, nothing marshalled at run time
    internal static unsafe class Com {

        public static readonly Guid CLSID_WbemLocator = new Guid("4590F811-1D3A-11D0-891F-00AA004B2E24");
        public static readonly Guid IID_IWbemLocator = new Guid("DC12A687-737F-11CF-884D-00AA004B2E24");
        public static readonly Guid IID_IWbemClassObject = new Guid("DC12A681-737F-11CF-884D-00AA004B2E24");

        public const uint COINIT_MULTITHREADED = 0;
        public const uint CLSCTX_INPROC_SERVER = 1;
        public const uint RPC_C_AUTHN_LEVEL_DEFAULT = 0, RPC_C_AUTHN_LEVEL_CALL = 3;
        public const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
        public const uint RPC_C_AUTHN_WINNT = 10, RPC_C_AUTHZ_NONE = 0;
        public const uint EOAC_NONE = 0;
        public const int RPC_E_TOO_LATE = unchecked((int) 0x80010119);
        public const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x10, WBEM_FLAG_FORWARD_ONLY = 0x20;
        public const int WBEM_INFINITE = -1;

        public const ushort VT_EMPTY = 0, VT_NULL = 1, VT_I4 = 3, VT_BSTR = 8, VT_UNKNOWN = 13, VT_UI1 = 17, VT_ARRAY = 0x2000;

        public static void* Slot(void* obj, int index) {
            return (*(void***) obj)[index];
        }


        public static void Release(void* obj) {
            if(obj != null)
                ((delegate* unmanaged[Stdcall]<void*, uint>) Slot(obj, 2))(obj);
        }

        public static void Check(int hr, string what) {
            if(hr < 0)
                throw new InvalidOperationException(what + " failed: 0x" + hr.ToString("X8"));
        }

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoInitializeEx(void* reserved, uint coInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoInitializeSecurity(void* securityDescriptor, int authServices, void* authServiceList,
            void* reserved1, uint authnLevel, uint impLevel, void* authList, uint capabilities, void* reserved3);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** instance);

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoSetProxyBlanket(void* proxy, uint authnSvc, uint authzSvc, char* serverPrincipalName,
            uint authnLevel, uint impLevel, void* authInfo, uint capabilities);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern char* SysAllocString(char* value);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern void SysFreeString(char* value);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern void* SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayAccessData(void* array, void** data);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayUnaccessData(void* array);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int SafeArrayGetUBound(void* array, uint dimension, int* upperBound);

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        public static extern int VariantClear(Variant* value);

    }

}
