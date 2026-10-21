  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System.Drawing;
using System.Globalization;
using System.IO;
using System.Resources;

namespace OmenMon {

    // Embedded resources (OmenMon.resx, manifest name OmenMon.Resources.resources)
    // Replaces the formerly generated strongly-typed class with the same members.
    // All resources are stored as plain byte arrays, so building them needs no
    // BinaryFormatter (unavailable to the .NET SDK's MSBuild) and reading them
    // needs no System.Resources.Extensions; Icon and Bitmap objects are created
    // from the original file bytes, as a fresh instance on each access as before
    internal static class Resources {

        private static ResourceManager resourceMan;

        // Resource manager instance
        internal static ResourceManager ResourceManager {
            get {
                if(resourceMan == null)
                    resourceMan = new ResourceManager("OmenMon.Resources", typeof(Resources).Assembly);
                return resourceMan;
            }
        }

        // Culture override for resource lookups
        internal static CultureInfo Culture { get; set; }

        // Retrieves a resource as a byte array
        private static byte[] GetBytes(string name) {
            return (byte[]) ResourceManager.GetObject(name, Culture);
        }

        // Retrieves a resource as an icon
        private static Icon GetIcon(string name) {
            return new Icon(new MemoryStream(GetBytes(name)));
        }

        // Retrieves a resource as a bitmap (the stream must remain open for the bitmap's lifetime)
        private static Bitmap GetBitmap(string name) {
            return new Bitmap(new MemoryStream(GetBytes(name)));
        }

        internal static byte[] FigureFont { get { return GetBytes("FigureFont"); } }
        internal static Icon Icon { get { return GetIcon("Icon"); } }
        internal static Icon IconTray { get { return GetIcon("IconTray"); } }
        internal static Icon IconTrayOutline { get { return GetIcon("IconTrayOutline"); } }
        internal static Bitmap Keyboard { get { return GetBitmap("Keyboard"); } }
        internal static Bitmap KeyboardOff { get { return GetBitmap("KeyboardOff"); } }
        internal static Bitmap Logo { get { return GetBitmap("Logo"); } }

    }

}
