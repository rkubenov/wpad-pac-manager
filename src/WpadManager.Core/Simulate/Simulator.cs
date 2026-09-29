using System;
using System.Collections.Generic;
using System.Text;
using WpadManager.Core.Model;

namespace WpadManager.Core.Simulate
{
    // Three-valued result of evaluating a condition against a concrete request.
    //   True/False  = we can decide it from the input
    //   Unknown     = needs runtime info we don't have (live DNS, or time predicates
    //                 with no clock supplied) — reported, never guessed
    public enum Tri { False, True, Unknown }

    // What we know about the request being simulated.
    public class SimInput
    {
        public string Url;
        public string Host;
        public string HostIp;     // optional pre-resolved IPv4 for isInNet; null = derive/unknown
        public string MyIp;       // optional client IP for isInNet(myIpAddress(), ...); null = unknown
        public DateTime? Now;      // optional clock for weekdayRange; null = time predicates Unknown
        public bool AssumeResolvable;  // treat isResolvable(name) as true; default: needs live DNS => Unknown

        public SimInput() { }
        public SimInput(string url, string host) { Url = url; Host = host; }
    }

    public class SimStep
    {
        public int Order;
        public string RuleId;
        public Tri Result;
        public string Reason;
    }

    public class SimResult
    {
        public Rule Matched;                 // the first rule that fired (null => default used)
        public List<ProxyEntry> Action;      // the action that would be returned
        public bool UsedDefault;
        public bool HasIndeterminateBeforeMatch; // an earlier rule could match at runtime
        public List<SimStep> Trace = new List<SimStep>();

        public string ActionString()
        {
            return Model.ActionText.Format(Action);
        }
    }

    // Deterministic, dependency-free PAC evaluation. Walks rules in order, evaluates
    // each condition with the host/url predicates we can decide locally, and returns
    // the first definite match plus a full per-rule trace explaining why others were
    // skipped. No JavaScript is executed.
    public static class Simulator
    {
        public static SimResult Run(RuleSet rs, SimInput input)
        {
            SimResult res = new SimResult();
            if (rs == null) { res.Action = new List<ProxyEntry>(); res.UsedDefault = true; return res; }

            HashSet<string> ruleIds = new HashSet<string>();
            for (int i = 0; i < rs.Rules.Count; i++) if (rs.Rules[i].Id != null) ruleIds.Add(rs.Rules[i].Id);

            for (int i = 0; i < rs.Rules.Count; i++)
            {
                Rule r = rs.Rules[i];
                // Preserved code that sits in front of this rule runs first; we cannot evaluate it.
                for (int u = 0; u < rs.Unparsed.Count; u++)
                    if (r.Id != null && rs.Unparsed[u].BeforeRuleId == r.Id) TraceUnparsed(rs.Unparsed[u], res);

                SimStep step = new SimStep();
                step.Order = r.Order;
                step.RuleId = r.Id;

                if (!r.Enabled)
                {
                    step.Result = Tri.False;
                    step.Reason = "disabled; skipped";
                    res.Trace.Add(step);
                    continue;
                }

                Tri t = Eval(r.Condition, input);
                step.Result = t;

                if (t == Tri.True)
                {
                    step.Reason = "matched";
                    res.Trace.Add(step);
                    res.Matched = r;
                    res.Action = r.Action;
                    return res;
                }
                if (t == Tri.Unknown)
                {
                    step.Reason = "indeterminate (depends on live DNS or time); could match at runtime";
                    res.HasIndeterminateBeforeMatch = true;
                }
                else
                {
                    step.Reason = "condition false";
                }
                res.Trace.Add(step);
            }

            for (int u = 0; u < rs.Unparsed.Count; u++)
            {
                UnparsedBlock ub = rs.Unparsed[u];
                if (ub.BeforeRuleId == null || !ruleIds.Contains(ub.BeforeRuleId)) TraceUnparsed(ub, res);
            }

            res.UsedDefault = true;
            res.Action = rs.DefaultAction;
            return res;
        }

        // An in-body statement the recognizer could not model: if it can return, the real
        // result may come from it, so everything after it is only a best guess.
        private static void TraceUnparsed(UnparsedBlock ub, SimResult res)
        {
            if (ub.AfterDefault || ub.RawText == null) return;
            if (ub.Reason != null && (ub.Reason.StartsWith("Top-level", StringComparison.Ordinal) ||
                                      ub.Reason.StartsWith("Unreachable", StringComparison.Ordinal))) return;
            if (ub.RawText.IndexOf("return", StringComparison.Ordinal) < 0) return;
            SimStep step = new SimStep();
            step.Order = -1;
            step.Result = Tri.Unknown;
            step.Reason = "preserved code (line " + ub.SourceLineStart + ") may return first; not evaluated";
            res.Trace.Add(step);
            res.HasIndeterminateBeforeMatch = true;
        }

        // ---- tri-state condition evaluation ----

        internal static Tri Eval(Condition c, SimInput input)
        {
            if (c == null) return Tri.False;

            Tri val;
            if (c.Kind == ConditionKind.Composite)
            {
                if (c.Op == CompositeOp.And) val = EvalAnd(c.Children, input);
                else val = EvalOr(c.Children, input);
            }
            else
            {
                val = EvalLeaf(c, input);
            }

            if (c.Negate) val = Not(val);
            return val;
        }

        private static Tri EvalAnd(List<Condition> kids, SimInput input)
        {
            if (kids == null || kids.Count == 0) return Tri.False;
            bool anyUnknown = false;
            for (int i = 0; i < kids.Count; i++)
            {
                Tri t = Eval(kids[i], input);
                if (t == Tri.False) return Tri.False;   // short-circuit
                if (t == Tri.Unknown) anyUnknown = true;
            }
            return anyUnknown ? Tri.Unknown : Tri.True;
        }

        private static Tri EvalOr(List<Condition> kids, SimInput input)
        {
            if (kids == null || kids.Count == 0) return Tri.False;
            bool anyUnknown = false;
            for (int i = 0; i < kids.Count; i++)
            {
                Tri t = Eval(kids[i], input);
                if (t == Tri.True) return Tri.True;      // short-circuit
                if (t == Tri.Unknown) anyUnknown = true;
            }
            return anyUnknown ? Tri.Unknown : Tri.False;
        }

        private static Tri Not(Tri t)
        {
            if (t == Tri.True) return Tri.False;
            if (t == Tri.False) return Tri.True;
            return Tri.Unknown;
        }

        // Semantics follow the reference PAC helpers (Chrome / Firefox): the browser passes
        // the host in lower case and the URL with scheme and host lower-cased (path as is);
        // dnsDomainIs, localHostOrDomainIs and shExpMatch compare case-sensitively.
        private static Tri EvalLeaf(Condition c, SimInput input)
        {
            string host = input.Host != null ? input.Host.ToLowerInvariant() : "";
            string url = CanonicalUrl(input.Url);
            string s;   // the value the predicate looks at (host, url, client IP, resolved IP)

            switch (c.Fn)
            {
                case "isPlainHostName":
                    if (!SubjectValue(c, input, host, url, out s)) return Tri.Unknown;
                    return B(s.IndexOf('.') < 0);

                case "dnsDomainIs":
                {
                    string d = Arg(c, 0);
                    if (d == null || !SubjectValue(c, input, host, url, out s)) return Tri.Unknown;
                    return B(s.EndsWith(d, StringComparison.Ordinal));
                }

                case "localHostOrDomainIs":
                {
                    // host == hostdom, or host is the leading part of hostdom up to a dot
                    string hd = Arg(c, 0);
                    if (hd == null || !SubjectValue(c, input, host, url, out s)) return Tri.Unknown;
                    return B(s == hd || hd.StartsWith(s + ".", StringComparison.Ordinal));
                }

                case "shExpMatch":
                {
                    string pat = Arg(c, 0);
                    if (pat == null || !SubjectValue(c, input, host, url, out s)) return Tri.Unknown;
                    return B(Wildcard(s, pat));
                }

                case "isInNet":
                    return EvalIsInNet(c, input);

                case "isInNetEx":
                    return EvalIsInNetEx(c, input);

                case "isResolvable":
                case "isResolvableEx":
                {
                    // An IP literal (or a supplied host IP) resolves; a name needs live DNS.
                    byte[] ip;
                    if (TryIp(host, out ip) || input.HostIp != null) return Tri.True;
                    return input.AssumeResolvable ? Tri.True : Tri.Unknown;
                }

                case "weekdayRange":
                    return EvalWeekday(c, input);

                case "dateRange":
                case "timeRange":
                    // Fully simulating date/time ranges is out of scope; mark indeterminate.
                    return Tri.Unknown;

                default:
                    return Tri.Unknown; // unknown predicate: can't decide
            }
        }

        private static Tri EvalIsInNet(Condition c, SimInput input)
        {
            byte[] ip = ResolveIpFor(c, input);
            if (ip == null) return Tri.Unknown;
            byte[] net, mask;
            if (!TryIp(Arg(c, 0), out net)) return Tri.Unknown;
            if (!TryIp(Arg(c, 1), out mask)) return Tri.Unknown;
            uint m = U(mask);
            return B((U(ip) & m) == (U(net) & m));
        }

        private static Tri EvalIsInNetEx(Condition c, SimInput input)
        {
            byte[] ip = ResolveIpFor(c, input);
            if (ip == null) return Tri.Unknown;
            string cidr = Arg(c, 0);
            if (cidr == null) return Tri.Unknown;
            int slash = cidr.IndexOf('/');
            if (slash < 0) return Tri.Unknown;
            byte[] net;
            if (!TryIp(cidr.Substring(0, slash), out net)) return Tri.Unknown; // IPv6 unsupported here
            int prefix;
            if (!int.TryParse(cidr.Substring(slash + 1), out prefix) || prefix < 0 || prefix > 32)
                return Tri.Unknown;
            uint m = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
            return B((U(ip) & m) == (U(net) & m));
        }

        private static Tri EvalWeekday(Condition c, SimInput input)
        {
            if (input.Now == null) return Tri.Unknown;
            string[] days = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };
            DateTime now = input.Now.Value;
            // optional trailing "GMT" switches to UTC
            bool gmt = false;
            int argc = c.Args != null ? c.Args.Count : 0;
            if (argc > 0 && string.Equals(c.Args[argc - 1], "GMT", StringComparison.OrdinalIgnoreCase))
            {
                gmt = true; argc--;
            }
            if (gmt) now = now.ToUniversalTime();
            int today = (int)now.DayOfWeek; // Sunday=0..Saturday=6

            if (argc == 1)
            {
                int d1 = IndexOf(days, c.Args[0]);
                if (d1 < 0) return Tri.Unknown;
                return B(today == d1);
            }
            if (argc >= 2)
            {
                int d1 = IndexOf(days, c.Args[0]);
                int d2 = IndexOf(days, c.Args[1]);
                if (d1 < 0 || d2 < 0) return Tri.Unknown;
                // inclusive range, wrapping around the week
                if (d1 <= d2) return B(today >= d1 && today <= d2);
                return B(today >= d1 || today <= d2);
            }
            return Tri.Unknown;
        }

        // ---- helpers ----

        // Pick the IP a network test should run against: the client's own IP when the
        // predicate's subject is myIpAddress()/myIpAddressEx(), otherwise the host's IP.
        private static byte[] ResolveIpFor(Condition c, SimInput input)
        {
            if (c.Subject != null &&
                c.Subject.IndexOf("myIpAddress", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] mip;
                if (input.MyIp != null && TryIp(input.MyIp, out mip)) return mip;
                return null; // client IP not supplied => indeterminate
            }
            return ResolveIp(input);
        }

        private static byte[] ResolveIp(SimInput input)
        {
            byte[] ip;
            if (input.HostIp != null && TryIp(input.HostIp, out ip)) return ip;
            if (input.Host != null && TryIp(input.Host, out ip)) return ip; // host is an IP literal
            return null; // would require live DNS
        }

        private static bool TryIp(string s, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('.');
            if (parts.Length != 4) return false;
            byte[] b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], out v) || v < 0 || v > 255) return false;
                b[i] = (byte)v;
            }
            bytes = b;
            return true;
        }

        private static uint U(byte[] b)
        {
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static int IndexOf(string[] arr, string v)
        {
            if (v == null) return -1;
            for (int i = 0; i < arr.Length; i++)
                if (string.Equals(arr[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string Arg(Condition c, int i)
        {
            if (c.Args == null || i >= c.Args.Count) return null;
            return c.Args[i];
        }

        private static Tri B(bool b) { return b ? Tri.True : Tri.False; }

        // What a predicate is applied to: host (or no subject), url, the client IP for
        // myIpAddress(), the resolved IP for dnsResolve(host). False = unknown here (client IP
        // not given, or a DNS lookup would be needed) — never guessed.
        private static bool SubjectValue(Condition c, SimInput input, string host, string url, out string value)
        {
            value = null;
            string subj = c.Subject != null ? c.Subject.Replace(" ", "") : null;
            if (subj == null || subj == "host") { value = host; return true; }
            if (subj == "url") { value = url; return true; }
            if (subj == "myIpAddress()" || subj == "myIpAddressEx()")
            {
                if (input.MyIp == null) return false;
                value = input.MyIp.Trim();
                return true;
            }
            if (subj == "dnsResolve(host)" || subj == "dnsResolveEx(host)")
            {
                byte[] ip;
                if (TryIp(host, out ip)) { value = host; return true; }
                if (input.HostIp != null) { value = input.HostIp.Trim(); return true; }
                return false;
            }
            return false;
        }

        // The URL as a browser hands it to FindProxyForURL: scheme and host lower-cased,
        // the rest untouched.
        internal static string CanonicalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string u = url.Trim();
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return u;
            int authEnd = u.IndexOfAny(new char[] { '/', '?', '#' }, scheme + 3);
            if (authEnd < 0) authEnd = u.Length;
            return u.Substring(0, authEnd).ToLowerInvariant() + u.Substring(authEnd);
        }

        // shExpMatch-style glob: '*' = any run, '?' = any single char. Case-sensitive, like
        // the browsers' implementation (a RegExp without the i flag).
        internal static bool Wildcard(string text, string pattern)
        {
            if (text == null) text = "";
            if (pattern == null) return false;
            string s = text;
            string p = pattern;
            int si = 0, pi = 0, star = -1, ss = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || p[pi] == s[si])) { si++; pi++; }
                else if (pi < p.Length && p[pi] == '*') { star = pi; ss = si; pi++; }
                else if (star != -1) { pi = star + 1; ss++; si = ss; }
                else return false;
            }
            while (pi < p.Length && p[pi] == '*') pi++;
            return pi == p.Length;
        }
    }
}
