  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OmenMon.Core;

namespace OmenMon.Tests {

    // Rule 0 of the restructuring plan: OmenMon.Core decides and does no I/O. It references
    // no hardware, WMI, registry, file or clock API; readings, time and settings come in as
    // values. Checked on the built assembly (what it references) and on its project file
    // (what it may reference), so a dependency cannot creep in unnoticed.
    public class CorePurityTests {

        // Namespaces the core may use types from. Extending this list is a design decision:
        // anything that reaches the system belongs in the application, not here.
        private static readonly HashSet<string> AllowedNamespaces = new HashSet<string> {
            "System",
            "System.Collections",                // IEnumerator, which foreach uses
            "System.Collections.Generic",
            "System.Diagnostics",                // DebuggableAttribute only, see below
            "System.Reflection",                 // assembly attributes
            "System.Runtime.CompilerServices",
            "System.Runtime.Versioning" };       // TargetFrameworkAttribute

        // Types of the allowed namespaces that still reach the system
        private static readonly HashSet<string> ForbiddenTypes = new HashSet<string> {
            "System.AppDomain",
            "System.Console",
            "System.Environment",
            "System.GC",
            "System.Diagnostics.Debugger",
            "System.Diagnostics.Process",
            "System.Diagnostics.Stopwatch" };

        // The clock: time comes in as a value (the guard takes a clock function)
        private static readonly HashSet<string> ForbiddenMembers = new HashSet<string> {
            "System.DateTime.get_Now",
            "System.DateTime.get_UtcNow",
            "System.DateTime.get_Today",
            "System.DateTimeOffset.get_Now",
            "System.DateTimeOffset.get_UtcNow" };

        private static readonly string[] ForbiddenAssemblies = {
            "Microsoft.Management.Infrastructure",
            "Microsoft.Win32.Primitives",
            "Microsoft.Win32.Registry",
            "System.Drawing",
            "System.Management",
            "System.Windows.Forms" };

        private static string Name(MetadataReader md, EntityHandle handle) {
            switch(handle.Kind) {
                case HandleKind.TypeReference: {
                    TypeReference t = md.GetTypeReference((TypeReferenceHandle) handle);
                    string ns = md.GetString(t.Namespace);
                    if(ns.Length == 0 && t.ResolutionScope.Kind == HandleKind.TypeReference)
                        return Name(md, (TypeReferenceHandle) t.ResolutionScope) + "+" + md.GetString(t.Name);
                    return ns + "." + md.GetString(t.Name);
                }
                case HandleKind.TypeDefinition: {
                    TypeDefinition t = md.GetTypeDefinition((TypeDefinitionHandle) handle);
                    return md.GetString(t.Namespace) + "." + md.GetString(t.Name);
                }
                default:
                    return "";
            }
        }

        [Fact]
        public void Core_ReferencesNoWindowsAssembly() {
            using PEReader pe = new PEReader(File.OpenRead(typeof(SensorReading).Assembly.Location));
            MetadataReader md = pe.GetMetadataReader();
            var names = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.DoesNotContain(n, ForbiddenAssemblies));
            Assert.All(names, n => Assert.DoesNotMatch(@"^(Microsoft\.Win32|System\.Management|System\.Windows|OmenMon)", n));
        }

        [Fact]
        public void Core_UsesOnlyPureTypes() {
            using PEReader pe = new PEReader(File.OpenRead(typeof(SensorReading).Assembly.Location));
            MetadataReader md = pe.GetMetadataReader();
            var types = md.TypeReferences
                .Select(h => (Handle: h, Name: Name(md, h), Namespace: md.GetString(md.GetTypeReference(h).Namespace)))
                .ToList();
            Assert.NotEmpty(types);

            // Nested types (an empty namespace) are judged by their outer type
            var offenders = types
                .Where(t => t.Namespace.Length > 0 && !AllowedNamespaces.Contains(t.Namespace))
                .Select(t => t.Name)
                .Concat(types.Select(t => t.Name).Where(ForbiddenTypes.Contains))
                .Concat(types.Select(t => t.Name).Where(n => n.StartsWith("System.Diagnostics.")
                    && !n.StartsWith("System.Diagnostics.DebuggableAttribute")))
                .Distinct()
                .ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void Core_ReadsNoClock() {
            using PEReader pe = new PEReader(File.OpenRead(typeof(SensorReading).Assembly.Location));
            MetadataReader md = pe.GetMetadataReader();
            var members = md.MemberReferences
                .Select(h => md.GetMemberReference(h))
                .Select(m => Name(md, m.Parent) + "." + md.GetString(m.Name))
                .ToList();
            Assert.DoesNotContain(members, ForbiddenMembers.Contains);
        }

        [Fact]
        public void Core_HasNoPlatformInvoke() {
            using PEReader pe = new PEReader(File.OpenRead(typeof(SensorReading).Assembly.Location));
            MetadataReader md = pe.GetMetadataReader();
            var pinvoke = md.MethodDefinitions
                .Select(h => md.GetMethodDefinition(h))
                .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                .Select(m => md.GetString(m.Name))
                .ToList();
            Assert.Empty(pinvoke);
            Assert.Equal(0, md.GetTableRowCount(TableIndex.ModuleRef));
        }

        // The project file: plain net48 and net10.0 (no -windows target, so no Windows
        // Forms or WPF), no project or assembly references, and no packages other than
        // the net48 reference assemblies
        [Fact]
        public void CoreProject_HasNoWindowsReferences() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            XDocument project = XDocument.Load(Path.Combine(root, "Core", "OmenMon.Core.csproj"));

            Assert.Equal("net48;net10.0", project.Descendants("TargetFrameworks").Single().Value);
            Assert.Empty(project.Descendants("TargetFramework"));
            Assert.Empty(project.Descendants("Reference"));
            Assert.Empty(project.Descendants("ProjectReference"));
            Assert.Empty(project.Descendants("FrameworkReference"));
            Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "UseWindowsForms"
                || e.Name.LocalName == "UseWPF");
            Assert.All(project.Descendants("PackageReference"),
                p => Assert.Equal("Microsoft.NETFramework.ReferenceAssemblies", (string?) p.Attribute("Include")));
        }

        // The sources: no P/Invoke or Windows namespaces, and no conditional compilation,
        // so the net48 build that ships is the same code as the net10.0 build checked above
        [Fact]
        public void CoreSources_AreTheSameForBothTargets() {
            string root = AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(root, "OmenMon.xml")))
                root = Path.GetDirectoryName(root)!;
            string dir = Path.Combine(root, "Core");
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .ToList();
            Assert.NotEmpty(files);
            var offenders = files
                .Where(f => Regex.IsMatch(File.ReadAllText(f),
                    @"^\s*#\s*if\b|\bDllImport\b|\bLibraryImport\b|\bextern\s|\bMicrosoft\.Win32\b|\bSystem\.Management\b|\bSystem\.Windows\b",
                    RegexOptions.Multiline))
                .Select(f => Path.GetRelativePath(root, f))
                .ToList();
            Assert.Empty(offenders);
        }

    }

}
