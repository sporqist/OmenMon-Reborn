  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// Fork additions © 2026 Marius Alwan Meyer (sporqist)

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace OmenMon.Library {

    // What a feature changed in Windows and when: one timestamped line per change in a
    // log file next to its state file (OmenMon-EcoPlus.log, OmenMon-Graphics.log). Cut
    // to its newer half when it passes MaxBytes.
    public static class ChangeLog {

        public const int MaxBytes = 64 * 1024;

        public static void Append(string path, DateTime time, string text) {
            if(path == null || string.IsNullOrEmpty(text))
                return;
            try {
                if(File.Exists(path) && new FileInfo(path).Length > MaxBytes) {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2), new UTF8Encoding(false));
                }
                File.AppendAllText(path, time.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + text + "\r\n", new UTF8Encoding(false));
            } catch {
                // The log is a record, not a safeguard: a line that cannot be written
                // never stops or undoes a change (the state file is what keeps originals)
            }
        }

    }

    // A small text file of the original values a feature will restore (Eco+, Graphics
    // apps). The feature saves it before it changes Windows, and changes nothing if the
    // save fails. A file that exists but cannot be read is never overwritten: it is moved
    // aside to a .bad copy (originals in it kept for a human), or, if even that fails,
    // Save refuses until a later Reload can read it. A save is atomic (temporary file
    // first, then swapped in), and the temporary file of a save cut short is read when
    // the file itself is missing.
    public abstract class StateFile {

        private readonly string path;

        protected StateFile(string path) {
            this.path = path;
        }

        // The feature's name in messages, and its log file next to the state file
        protected abstract string Owner { get; }
        protected abstract string LogFileName { get; }

        // Reads the lines into the values, clearing what was there first; throws if the
        // file cannot be used
        protected abstract void Parse(string[] lines);

        // The file's content for the values held now
        protected abstract string Format();

        public abstract bool IsEmpty { get; }

        // Why the file could not be loaded (null if it was, or there was none)
        public string LoadError { get; private set; }

        // True while an unreadable file is still in place: Save does not overwrite it
        public bool Blocked { get; private set; }

        // Why the last Save failed (null if it worked)
        public string LastError { get; private set; }

        // The log next to the state file
        public string LogPath {
            get { return this.path == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(this.path)), LogFileName); }
        }

        private string TempPath { get { return this.path + ".tmp"; } }

        // Reads the file, if there is one (called once, by the derived class's Load)
        protected void LoadFile() {
            string source = Source();
            if(source == null)
                return;
            try {
                Parse(File.ReadAllLines(source, Encoding.UTF8));
            } catch(Exception e) {
                SetAside(source, e);
            }
        }

        // Reads the file again while Blocked; true once it is no longer blocked
        public bool Reload() {
            if(!this.Blocked)
                return true;
            try {
                string source = Source();
                if(source != null)
                    Parse(File.ReadAllLines(source, Encoding.UTF8));
                this.Blocked = false;
                this.LoadError = null;
                return true;
            } catch {
                return false;
            }
        }

        // The file to read: the state file, or the temporary file of a save that was cut
        // short between writing it and putting it in place
        private string Source() {
            if(this.path == null)
                return null;
            if(File.Exists(this.path))
                return this.path;
            return File.Exists(TempPath) ? TempPath : null;
        }

        private void SetAside(string source, Exception e) {
            this.LoadError = "could not read " + Path.GetFileName(source) + " (" + e.Message + ")";
            string bad = this.path + ".bad";
            if(File.Exists(bad))
                bad = this.path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try {
                File.Move(source, bad);
                this.LoadError += ", kept as " + Path.GetFileName(bad) + " with the originals it holds; starting empty";
            } catch {
                this.Blocked = true;
                this.LoadError += " and could not move it aside: " + Owner + " changes nothing until it can be read";
            }
        }

        // Atomic: written to a temporary file first, which is never deleted while it may
        // be the only copy; the file is removed when empty. False if it was not saved.
        public bool Save() {
            if(this.path == null)
                return true;
            if(this.Blocked) {
                this.LastError = "the state file could not be read, so it is not overwritten";
                return false;
            }
            try {
                if(IsEmpty) {
                    if(File.Exists(this.path))
                        File.Delete(this.path);
                    if(File.Exists(TempPath))
                        File.Delete(TempPath);
                    this.LastError = null;
                    return true;
                }
                File.WriteAllText(TempPath, Format(), new UTF8Encoding(false));
                if(File.Exists(this.path)) {
                    try {
                        File.Replace(TempPath, this.path, null);
                    } catch when(!File.Exists(this.path) && File.Exists(TempPath)) {
                        // ReplaceFile can fail after moving the old file away: the new
                        // content is in the temporary file, so put that in place
                        File.Move(TempPath, this.path);
                    }
                } else
                    File.Move(TempPath, this.path);
                this.LastError = null;
                return true;
            } catch(Exception e) {
                this.LastError = e.Message;
                return false;
            }
        }

    }

}
