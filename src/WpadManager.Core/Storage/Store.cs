using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WpadManager.Core.Model;
using WpadManager.Core.Generator;
using JsonLib = WpadManager.Core.Json.Json;  // class, not the sibling namespace

namespace WpadManager.Core.Storage
{
    // One saved snapshot of the rule set, for history / rollback.
    public class VersionEntry
    {
        public string Id;
        public string Timestamp;   // ISO-8601 UTC
        public string Author;
        public string Note;
        public RuleSet Snapshot;
    }

    // One audit-log line (who did what, when).
    public class AuditEntry
    {
        public string Timestamp;
        public string Author;
        public string Action;      // commit / rollback / ...
        public string Detail;
    }

    // The whole persisted document: current state + history + audit trail.
    public class Store
    {
        public RuleSet Current;
        public string SourcePath;   // the imported .pac/.dat we write changes back to
        public List<VersionEntry> Versions = new List<VersionEntry>();
        public List<AuditEntry> Audit = new List<AuditEntry>();
    }

    // Which files the operator has open and which one is active. Persisted next to the exe
    // (wpad-workspace.json) so the set of files reopens automatically — no re-import.
    public class WorkspaceState
    {
        public List<string> OpenFiles = new List<string>();
        public string ActiveFile;
        public string Language;   // UI language code: "ru" (default) or "en"
        public string Theme;      // UI theme: "system" (default), "light" or "dark"
        public string HistoryDir; // where per-file histories live; null = "history" next to the exe
    }

    public class DiffResult
    {
        public List<string> Added = new List<string>();
        public List<string> Removed = new List<string>();
        public List<string> Changed = new List<string>();

        public bool IsEmpty
        {
            get { return Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0; }
        }
    }

    // Portable JSON-file persistence that lives next to the .exe. The whole store is one
    // human-readable, git-friendly JSON document. Commits snapshot the state for rollback
    // and every mutation appends an audit entry. No database, no external dependency.
    public static class RuleStore
    {
        // Folder of the running executable (falls back to the current directory).
        private static string ExeDir()
        {
            string dir;
            try
            {
                dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                dir = Directory.GetCurrentDirectory();
            }
            if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
            return dir;
        }

        // Legacy single-file store path: alongside the running executable.
        public static string DefaultPath()
        {
            return Path.Combine(ExeDir(), "wpad-store.json");
        }

        // The workspace list (which files are open + which is active) lives next to the exe.
        // It holds only paths — the actual history travels with each file (see SidecarPath).
        public static string WorkspacePath()
        {
            return Path.Combine(ExeDir(), "wpad-workspace.json");
        }

        // Where older versions kept a file's history: "<sourceFile>.history.json" right next
        // to it. Only read now, to move such files into the history folder (see below).
        public static string SidecarPath(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return DefaultPath();
            return sourcePath + ".history.json";
        }

        // Default history folder: "history" next to the exe (shared by all admins when the
        // exe itself sits on a shared folder).
        public static string DefaultHistoryDir()
        {
            return Path.Combine(ExeDir(), "history");
        }

        // A file's history: "<historyDir>\<file name>-<hash of full path>.history.json".
        // Deliberately not next to the PAC file, whose folder is usually the web server's —
        // anyone on the network could download the history there (old rules, admin names,
        // notes). The hash keeps two files of the same name (two sites' wpad.dat) apart; paths
        // are compared case-insensitively, as Windows does.
        public static string HistoryPath(string sourcePath, string historyDir)
        {
            string full;
            try { full = Path.GetFullPath(sourcePath); }
            catch { full = sourcePath; }
            string hash = TextFile.Fingerprint(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
            return Path.Combine(historyDir, Path.GetFileName(full).ToLowerInvariant() + "-" +
                hash.Substring(0, 10).ToLowerInvariant() + ".history.json");
        }

        // Move a history file from the old place (next to the PAC) into historyDir, merging it
        // with any history already there, and remove it from the PAC's folder. Returns true if
        // something was moved. A damaged old file is set aside by LoadOrRecover, not moved.
        public static bool MigrateLegacySidecar(string sourcePath, string historyDir)
        {
            string damaged;
            return MigrateLegacySidecar(sourcePath, historyDir, out damaged);
        }

        // `damagedMovedTo`: set when the old file could not be read and was set aside instead.
        public static bool MigrateLegacySidecar(string sourcePath, string historyDir, out string damagedMovedTo)
        {
            damagedMovedTo = null;
            string legacy = SidecarPath(sourcePath);
            if (!File.Exists(legacy)) return false;
            Store old = LoadOrRecover(legacy, out damagedMovedTo);
            if (damagedMovedTo != null) return false;
            SaveMerged(HistoryPath(sourcePath, historyDir), old);
            File.Delete(legacy);
            return true;
        }

        // Load an existing store, or create an empty one if the file does not exist.
        public static Store Load(string path)
        {
            if (!File.Exists(path))
            {
                Store s = new Store();
                s.Current = new RuleSet();
                return s;
            }
            string text = TextFile.Read(path);
            Store loaded = JsonLib.Deserialize<Store>(text);
            if (loaded == null) loaded = new Store();
            if (loaded.Current == null) loaded.Current = new RuleSet();
            if (loaded.Versions == null) loaded.Versions = new List<VersionEntry>();
            if (loaded.Audit == null) loaded.Audit = new List<AuditEntry>();
            return loaded;
        }

        // Like Load, but a history file that exists yet cannot be parsed (hand-edited,
        // truncated) is renamed to "<name>.corrupt-<UTC time>" and an empty store returned,
        // so one broken sidecar can neither crash the app nor be silently overwritten.
        // `movedTo` is the new name, or null when nothing was moved. I/O errors propagate.
        public static Store LoadOrRecover(string path, out string movedTo)
        {
            movedTo = null;
            try
            {
                return Load(path);
            }
            catch (FormatException)
            {
                movedTo = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture);
                File.Move(path, movedTo);
                return Load(path);   // now missing => empty store
            }
        }

        public static void Save(string path, Store store)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);   // e.g. a new history folder
            TextFile.Write(path, JsonLib.Stringify(store, true));
        }

        // Save, but first fold in versions / audit entries that someone else has written to
        // the same history file since we loaded it (two admins editing one wpad.dat), so
        // neither loses the other's history. `store` itself ends up with the merged lists.
        public static void SaveMerged(string path, Store store)
        {
            if (File.Exists(path))
            {
                string ignored;
                Store disk = LoadOrRecover(path, out ignored);
                store.Versions = MergeVersions(disk.Versions, store.Versions);
                store.Audit = MergeAudit(disk.Audit, store.Audit);
            }
            Save(path, store);
        }

        private static List<VersionEntry> MergeVersions(List<VersionEntry> disk, List<VersionEntry> mine)
        {
            List<VersionEntry> all = new List<VersionEntry>(disk);
            HashSet<string> ids = new HashSet<string>();
            for (int i = 0; i < disk.Count; i++) if (disk[i].Id != null) ids.Add(disk[i].Id);
            for (int i = 0; i < mine.Count; i++)
                if (mine[i].Id == null || !ids.Contains(mine[i].Id)) all.Add(mine[i]);
            // Stable sort by timestamp (ISO-8601 sorts as text): equal times keep disk first.
            return all.OrderBy(v => v.Timestamp ?? "", StringComparer.Ordinal).ToList();
        }

        private static List<AuditEntry> MergeAudit(List<AuditEntry> disk, List<AuditEntry> mine)
        {
            List<AuditEntry> all = new List<AuditEntry>(disk);
            HashSet<string> keys = new HashSet<string>();
            for (int i = 0; i < disk.Count; i++) keys.Add(AuditKey(disk[i]));
            for (int i = 0; i < mine.Count; i++)
                if (keys.Add(AuditKey(mine[i]))) all.Add(mine[i]);
            return all.OrderBy(a => a.Timestamp ?? "", StringComparer.Ordinal).ToList();
        }

        private static string AuditKey(AuditEntry a)
        {
            return a.Timestamp + "\n" + a.Author + "\n" + a.Action + "\n" + a.Detail;
        }

        // Load the workspace list, or an empty one if it does not exist / is unreadable.
        public static WorkspaceState LoadWorkspace(string path)
        {
            if (!File.Exists(path)) return new WorkspaceState();
            WorkspaceState ws;
            try { ws = JsonLib.Deserialize<WorkspaceState>(TextFile.Read(path)); }
            catch { ws = null; }
            if (ws == null) ws = new WorkspaceState();
            if (ws.OpenFiles == null) ws.OpenFiles = new List<string>();
            return ws;
        }

        public static void SaveWorkspace(string path, WorkspaceState ws)
        {
            TextFile.Write(path, JsonLib.Stringify(ws, true));
        }

        // Replace the current rule set, snapshot it as a new version, and audit the change.
        public static VersionEntry Commit(Store store, RuleSet next, string author, string note)
        {
            if (store.Versions == null) store.Versions = new List<VersionEntry>();
            if (store.Audit == null) store.Audit = new List<AuditEntry>();

            string now = Iso.Now();
            if (next != null) next.UpdatedAt = now;
            store.Current = next;

            VersionEntry v = new VersionEntry();
            v.Id = NewId();
            v.Timestamp = now;
            v.Author = author;
            v.Note = note;
            v.Snapshot = DeepCopy(next);
            store.Versions.Add(v);

            store.Audit.Add(MakeAudit(now, author, "commit",
                "version " + v.Id + (note != null ? " — " + note : "")));
            return v;
        }

        // Restore Current from a stored version snapshot (recorded as a new audit entry).
        public static bool Rollback(Store store, string versionId, string author)
        {
            VersionEntry found = null;
            for (int i = 0; i < store.Versions.Count; i++)
                if (store.Versions[i].Id == versionId) { found = store.Versions[i]; break; }
            if (found == null) return false;

            string now = Iso.Now();
            store.Current = DeepCopy(found.Snapshot);
            if (store.Current != null) store.Current.UpdatedAt = now;
            store.Audit.Add(MakeAudit(now, author, "rollback", "to version " + versionId));
            return true;
        }

        // Structural diff between two rule sets, keyed by rule Id.
        public static DiffResult Diff(RuleSet from, RuleSet to)
        {
            DiffResult d = new DiffResult();
            Dictionary<string, Rule> a = Index(from);
            Dictionary<string, Rule> b = Index(to);

            foreach (KeyValuePair<string, Rule> kv in b)
                if (!a.ContainsKey(kv.Key))
                    d.Added.Add(Signature(kv.Value));

            foreach (KeyValuePair<string, Rule> kv in a)
                if (!b.ContainsKey(kv.Key))
                    d.Removed.Add(Signature(kv.Value));

            foreach (KeyValuePair<string, Rule> kv in a)
            {
                Rule other;
                if (b.TryGetValue(kv.Key, out other))
                {
                    string sa = Signature(kv.Value);
                    string sb = Signature(other);
                    if (sa != sb) d.Changed.Add(sa + "  ->  " + sb);
                }
            }
            return d;
        }

        // A canonical, timestamp-free fingerprint of a rule's meaning.
        public static string Signature(Rule r)
        {
            if (r == null) return "<null>";
            string cond = r.Condition != null ? PacGenerator.GenCondition(r.Condition) : "<none>";
            string act = ActionText.Format(r.Action);
            string state = r.Enabled ? "on" : "off";
            string comment = r.Comment != null ? r.Comment : "";
            return state + " [" + cond + "] => " + act + (comment.Length > 0 ? " // " + comment : "");
        }

        // ---- helpers ----

        private static Dictionary<string, Rule> Index(RuleSet rs)
        {
            Dictionary<string, Rule> map = new Dictionary<string, Rule>();
            if (rs == null || rs.Rules == null) return map;
            for (int i = 0; i < rs.Rules.Count; i++)
            {
                Rule r = rs.Rules[i];
                string key = !string.IsNullOrEmpty(r.Id) ? r.Id : ("idx#" + i);
                if (!map.ContainsKey(key)) map.Add(key, r);
            }
            return map;
        }

        private static AuditEntry MakeAudit(string ts, string author, string action, string detail)
        {
            AuditEntry e = new AuditEntry();
            e.Timestamp = ts; e.Author = author; e.Action = action; e.Detail = detail;
            return e;
        }

        // Deep copy via JSON round-trip so snapshots never alias the live object graph.
        private static RuleSet DeepCopy(RuleSet rs)
        {
            if (rs == null) return null;
            return JsonLib.Deserialize<RuleSet>(JsonLib.Stringify(rs, false));
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }
    }
}
