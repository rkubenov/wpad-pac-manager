using System;
using System.Collections.Generic;
using WpadManager.Core.Model;

namespace WpadManager.Core.Validate
{
    // Per-function arity / role metadata used to sanity-check conditions.
    // LitMin/LitMax count the *literal* arguments only (the host/url subject is
    // captured separately in Condition.Subject and is NOT part of Args).
    internal class FnSpec
    {
        public bool TakesSubject;   // true if the PAC signature begins with host/url
        public int LitMin;
        public int LitMax;
        public bool IsPredicate;    // returns bool => may legitimately drive a condition

        public FnSpec(bool subject, int min, int max, bool predicate)
        {
            TakesSubject = subject; LitMin = min; LitMax = max; IsPredicate = predicate;
        }
    }

    // Structural / grammar validation of a RuleSet:
    //  - unknown PAC functions (with "did you mean" suggestions)
    //  - non-predicate functions used as a condition
    //  - wrong literal argument counts
    //  - isInNet / isInNetEx network+mask / CIDR validity
    //  - action-string grammar: unknown keyword, empty host, port range
    //  - surfaces unparsed blocks (esp. "Unreachable after return") as findings
    public static class Validator
    {
        private static readonly Dictionary<string, FnSpec> Specs = BuildSpecs();

        private static Dictionary<string, FnSpec> BuildSpecs()
        {
            Dictionary<string, FnSpec> d = new Dictionary<string, FnSpec>(StringComparer.Ordinal);
            //                              subject  min max  predicate
            d["isPlainHostName"]     = new FnSpec(true,  0, 0, true);
            d["dnsDomainIs"]         = new FnSpec(true,  1, 1, true);
            d["localHostOrDomainIs"] = new FnSpec(true,  1, 1, true);
            d["isResolvable"]        = new FnSpec(true,  0, 0, true);
            d["isInNet"]             = new FnSpec(true,  2, 2, true);
            d["dnsResolve"]          = new FnSpec(true,  0, 0, false);
            d["myIpAddress"]         = new FnSpec(false, 0, 0, false);
            d["dnsDomainLevels"]     = new FnSpec(true,  0, 0, false);
            d["shExpMatch"]          = new FnSpec(true,  1, 1, true);
            d["weekdayRange"]        = new FnSpec(false, 1, 3, true);
            d["dateRange"]           = new FnSpec(false, 1, 7, true);
            d["timeRange"]           = new FnSpec(false, 1, 6, true);
            d["isInNetEx"]           = new FnSpec(true,  1, 1, true);
            d["dnsResolveEx"]        = new FnSpec(true,  0, 0, false);
            d["isResolvableEx"]      = new FnSpec(true,  0, 0, true);
            d["myIpAddressEx"]       = new FnSpec(false, 0, 0, false);
            d["sortIpAddressList"]   = new FnSpec(false, 1, 1, false);
            d["getClientVersion"]    = new FnSpec(false, 0, 0, false);
            d["alert"]               = new FnSpec(false, 1, 1, false);
            return d;
        }

        public static Report Validate(RuleSet rs)
        {
            Report report = new Report();
            if (rs == null) return report;

            for (int i = 0; i < rs.Rules.Count; i++)
            {
                Rule r = rs.Rules[i];
                if (r.Condition != null) ValidateCondition(r.Condition, r, report, true);
                ValidateAction(r.Action, r.Id, r.Order, report);
            }

            // The trailing default action gets the same grammar checks (rule-less).
            ValidateAction(rs.DefaultAction, null, -1, report);

            SurfaceUnparsed(rs, report);
            return report;
        }

        // root=true means this node is allowed to be a non-predicate-driving point only
        // if it is itself a predicate; composites recurse with root=false on children.
        private static void ValidateCondition(Condition c, Rule r, Report report, bool root)
        {
            if (c == null) return;

            if (c.Kind == ConditionKind.Composite)
            {
                if (c.Children == null || c.Children.Count == 0)
                {
                    report.Add(Finding.Make(Severity.Warning, "COND_EMPTY",
                        "Composite condition has no children.", r.Id, r.Order));
                    return;
                }
                for (int i = 0; i < c.Children.Count; i++)
                    ValidateCondition(c.Children[i], r, report, false);
                return;
            }

            // Single
            string fn = c.Fn;
            if (!PacFunctions.IsKnown(fn))
            {
                string suggest = Levenshtein.NearestKnown(fn, PacFunctions.All);
                string msg = "Unknown PAC function '" + fn + "'.";
                if (suggest != null) msg += " Did you mean '" + suggest + "'?";
                report.Add(Finding.Make(Severity.Error, "FN_UNKNOWN", msg, r.Id, r.Order));
                return; // can't arity-check an unknown function
            }

            FnSpec spec = Specs[fn];
            if (!spec.IsPredicate)
            {
                report.Add(Finding.Make(Severity.Warning, "FN_NOT_PREDICATE",
                    "'" + fn + "' does not return a boolean and should not drive a rule condition.",
                    r.Id, r.Order));
            }

            int n = c.Args != null ? c.Args.Count : 0;
            if (n < spec.LitMin || n > spec.LitMax)
            {
                string want = spec.LitMin == spec.LitMax
                    ? spec.LitMin.ToString()
                    : (spec.LitMin + "-" + spec.LitMax);
                report.Add(Finding.Make(Severity.Warning, "FN_ARGS",
                    "'" + fn + "' expects " + want + " argument(s) but got " + n + ".",
                    r.Id, r.Order));
            }

            if (fn == "isInNet") ValidateIsInNet(c, r, report);
            else if (fn == "isInNetEx") ValidateIsInNetEx(c, r, report);
        }

        private static void ValidateIsInNet(Condition c, Rule r, Report report)
        {
            if (c.Args == null || c.Args.Count < 2) return; // arity finding already emitted
            string net = c.Args[0];
            string mask = c.Args[1];
            byte[] netb, maskb;
            if (!TryParseIPv4(net, out netb))
                report.Add(Finding.Make(Severity.Error, "NET_BAD_IP",
                    "isInNet network '" + net + "' is not a valid IPv4 address.", r.Id, r.Order));
            if (!TryParseIPv4(mask, out maskb))
            {
                report.Add(Finding.Make(Severity.Error, "NET_BAD_MASK",
                    "isInNet mask '" + mask + "' is not a valid IPv4 mask.", r.Id, r.Order));
            }
            else if (!IsContiguousMask(maskb))
            {
                report.Add(Finding.Make(Severity.Warning, "NET_MASK_NONCONTIG",
                    "isInNet mask '" + mask + "' is not a contiguous subnet mask.", r.Id, r.Order));
            }
        }

        private static void ValidateIsInNetEx(Condition c, Rule r, Report report)
        {
            if (c.Args == null || c.Args.Count < 1) return;
            string cidr = c.Args[0];
            int slash = cidr.IndexOf('/');
            if (slash < 0)
            {
                report.Add(Finding.Make(Severity.Warning, "NET_NO_PREFIX",
                    "isInNetEx expects CIDR form 'addr/prefix'; got '" + cidr + "'.", r.Id, r.Order));
                return;
            }
            string addr = cidr.Substring(0, slash);
            string pfxStr = cidr.Substring(slash + 1);
            int pfx;
            bool isV6 = addr.IndexOf(':') >= 0;
            byte[] dummy;
            if (!isV6 && !TryParseIPv4(addr, out dummy))
                report.Add(Finding.Make(Severity.Error, "NET_BAD_IP",
                    "isInNetEx address '" + addr + "' is not a valid IP address.", r.Id, r.Order));
            int max = isV6 ? 128 : 32;
            if (!int.TryParse(pfxStr, out pfx) || pfx < 0 || pfx > max)
                report.Add(Finding.Make(Severity.Error, "NET_BAD_PREFIX",
                    "isInNetEx prefix '" + pfxStr + "' must be 0-" + max + ".", r.Id, r.Order));
        }

        private static void ValidateAction(List<ProxyEntry> action, string ruleId, int order, Report report)
        {
            if (action == null || action.Count == 0)
            {
                report.Add(Finding.Make(Severity.Warning, "ACT_EMPTY",
                    "Action chain is empty; the rule returns nothing.", ruleId, order));
                return;
            }
            for (int i = 0; i < action.Count; i++)
            {
                ProxyEntry e = action[i];
                if (e.Kind == ProxyKind.Direct) continue;

                // ActionText stores an unrecognized keyword by stuffing the whole token
                // into Host (which then contains whitespace) with Port 0.
                if (e.Host != null && HasWhitespace(e.Host))
                {
                    report.Add(Finding.Make(Severity.Error, "ACT_UNKNOWN_KEYWORD",
                        "Unrecognized proxy directive '" + e.Host + "'.", ruleId, order));
                    continue;
                }

                if (string.IsNullOrEmpty(e.Host))
                {
                    report.Add(Finding.Make(Severity.Error, "ACT_NO_HOST",
                        FormatKind(e.Kind) + " entry is missing a host.", ruleId, order));
                }
                if (e.Port <= 0)
                {
                    report.Add(Finding.Make(Severity.Warning, "ACT_NO_PORT",
                        FormatKind(e.Kind) + " entry '" + (e.Host != null ? e.Host : "")
                        + "' has no port.", ruleId, order));
                }
                else if (e.Port > 65535)
                {
                    report.Add(Finding.Make(Severity.Error, "ACT_BAD_PORT",
                        "Port " + e.Port + " is out of range (1-65535).", ruleId, order));
                }
            }

            // A non-DIRECT-terminated chain is legal but worth noting: if every hop is
            // unreachable the client falls back to DIRECT implicitly anyway.
        }

        private static void SurfaceUnparsed(RuleSet rs, Report report)
        {
            if (rs.Unparsed == null) return;
            for (int i = 0; i < rs.Unparsed.Count; i++)
            {
                UnparsedBlock u = rs.Unparsed[i];
                string reason = u.Reason != null ? u.Reason : "Unparsed block.";
                Severity sev = reason.StartsWith("Unreachable", StringComparison.OrdinalIgnoreCase)
                    ? Severity.Warning
                    : Severity.Info;
                Finding f = Finding.Make(sev, "UNPARSED", reason, null, -1);
                f.Line = u.SourceLineStart;
                report.Add(f);
            }
        }

        // ----- helpers -----

        private static string FormatKind(ProxyKind k)
        {
            switch (k)
            {
                case ProxyKind.Proxy: return "PROXY";
                case ProxyKind.Socks: return "SOCKS";
                case ProxyKind.Socks5: return "SOCKS5";
                case ProxyKind.Https: return "HTTPS";
                case ProxyKind.Http: return "HTTP";
                default: return "DIRECT";
            }
        }

        private static bool HasWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == ' ' || s[i] == '\t') return true;
            return false;
        }

        internal static bool TryParseIPv4(string s, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split('.');
            if (parts.Length != 4) return false;
            byte[] b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], out v)) return false;
                if (v < 0 || v > 255) return false;
                // reject leading-zero ambiguity like "01" but allow plain "0"
                if (parts[i].Length > 1 && parts[i][0] == '0') return false;
                b[i] = (byte)v;
            }
            bytes = b;
            return true;
        }

        internal static bool IsContiguousMask(byte[] m)
        {
            uint val = ((uint)m[0] << 24) | ((uint)m[1] << 16) | ((uint)m[2] << 8) | m[3];
            uint inv = ~val;                  // for a valid mask this is 0...01...1
            return (inv & (inv + 1)) == 0;    // true iff inv is of the form 2^k - 1
        }
    }
}
