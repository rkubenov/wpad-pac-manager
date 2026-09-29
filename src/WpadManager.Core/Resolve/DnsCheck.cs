using System;
using System.Collections.Generic;
using System.Net;
using WpadManager.Core.Model;

namespace WpadManager.Core.Resolve
{
    public class DnsResult
    {
        public string Name;
        public bool Ok;
        public List<string> Addresses = new List<string>();
        public bool TimedOut;    // no answer within the timeout
        public bool NoRecords;   // answered, but without addresses
        public string Error;     // resolver error text, when neither of the above
    }

    // Resolves the domain names referenced by rules through the system DNS resolver
    // (pointed at corporate DNS), with a per-name timeout and an in-memory cache so a
    // re-check is instant. Used to flag rules whose domain no longer resolves (stale).
    // Thread-safe: the UI resolves several names in parallel off the UI thread.
    public static class DnsCheck
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, DnsResult> _cache =
            new Dictionary<string, DnsResult>(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache() { lock (_lock) _cache.Clear(); }

        public static DnsResult Resolve(string name, int timeoutMs)
        {
            DnsResult cached;
            lock (_lock)
                if (name != null && _cache.TryGetValue(name, out cached)) return cached;

            DnsResult res = new DnsResult();
            res.Name = name;
            if (string.IsNullOrEmpty(name)) { res.Ok = false; res.NoRecords = true; return res; }

            try
            {
                IAsyncResult ar = Dns.BeginGetHostAddresses(name, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    res.Ok = false;
                    res.TimedOut = true;
                }
                else
                {
                    IPAddress[] ips = Dns.EndGetHostAddresses(ar);
                    for (int i = 0; i < ips.Length; i++) res.Addresses.Add(ips[i].ToString());
                    res.Ok = res.Addresses.Count > 0;
                    res.NoRecords = !res.Ok;
                }
            }
            catch (Exception ex)
            {
                res.Ok = false;
                res.Error = ex.Message;
            }
            lock (_lock) _cache[name] = res;
            return res;
        }

        // The distinct resolvable domain names mentioned by a rule set:
        // the arguments of dnsDomainIs / localHostOrDomainIs, with any leading dot stripped
        // (".example.com" -> "example.com"). Wildcard shExpMatch patterns and IP nets are skipped.
        public static List<string> NamesFrom(RuleSet rs)
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rs == null) return names;
            for (int i = 0; i < rs.Rules.Count; i++)
                Collect(rs.Rules[i].Condition, names, seen);
            return names;
        }

        private static void Collect(Condition c, List<string> names, HashSet<string> seen)
        {
            if (c == null) return;
            if (c.Kind == ConditionKind.Composite)
            {
                if (c.Children != null)
                    for (int i = 0; i < c.Children.Count; i++) Collect(c.Children[i], names, seen);
                return;
            }
            if (c.Fn == "dnsDomainIs" || c.Fn == "localHostOrDomainIs")
            {
                if (c.Args != null && c.Args.Count > 0 && c.Args[0] != null)
                {
                    string d = c.Args[0].TrimStart('.').Trim();
                    if (d.Length > 0 && seen.Add(d)) names.Add(d);
                }
            }
        }
    }
}
