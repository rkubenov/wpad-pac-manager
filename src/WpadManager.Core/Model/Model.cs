using System;
using System.Collections.Generic;

namespace WpadManager.Core.Model
{
    // The set of legitimate PAC helper functions a condition may use.
    // Kept as the canonical spelling; the validator uses this list to catch typos.
    public static class PacFunctions
    {
        public static readonly string[] All = new string[]
        {
            "isPlainHostName", "dnsDomainIs", "localHostOrDomainIs", "isResolvable",
            "isInNet", "dnsResolve", "myIpAddress", "dnsDomainLevels", "shExpMatch",
            "weekdayRange", "dateRange", "timeRange", "isInNetEx", "dnsResolveEx",
            "isResolvableEx", "myIpAddressEx", "sortIpAddressList", "getClientVersion",
            "alert"
        };

        // Predicate-style functions that can legitimately drive a condition (return bool).
        public static readonly string[] Predicates = new string[]
        {
            "isPlainHostName", "dnsDomainIs", "localHostOrDomainIs", "isResolvable",
            "isInNet", "shExpMatch", "weekdayRange", "dateRange", "timeRange",
            "isInNetEx", "isResolvableEx"
        };

        public static bool IsKnown(string name)
        {
            if (name == null) return false;
            for (int i = 0; i < All.Length; i++) if (All[i] == name) return true;
            return false;
        }
    }

    public enum ConditionKind { Single, Composite }
    public enum CompositeOp { And, Or }

    // A condition is a tree: leaves are single PAC predicate calls,
    // internal nodes are AND/OR of children. Negate flips a node (the `!` operator).
    public class Condition
    {
        public ConditionKind Kind;

        // --- Single ---
        public string Fn;        // canonical PAC function name, e.g. "dnsDomainIs"
        public string Subject;   // runtime subject identifier ("host"/"url"); null for time predicates
        // Args as they appeared, normalized to strings. host/url placeholders are dropped;
        // e.g. dnsDomainIs(host, ".x.com") -> Args = [".x.com"]; isInNet(host,"10.0.0.0","255.0.0.0") -> ["10.0.0.0","255.0.0.0"].
        public List<string> Args;

        // --- Composite ---
        public CompositeOp Op;
        public List<Condition> Children;

        public bool Negate;

        public Condition() { }

        public static Condition Single(string fn, List<string> args, bool negate)
        {
            Condition c = new Condition();
            c.Kind = ConditionKind.Single;
            c.Fn = fn;
            c.Args = args != null ? args : new List<string>();
            c.Negate = negate;
            return c;
        }

        public static Condition Composite(CompositeOp op, List<Condition> children, bool negate)
        {
            Condition c = new Condition();
            c.Kind = ConditionKind.Composite;
            c.Op = op;
            c.Children = children != null ? children : new List<Condition>();
            c.Negate = negate;
            return c;
        }

        // Convenience: the "primary" value used for filtering/search/denormalization.
        public string PrimaryValue()
        {
            if (Kind == ConditionKind.Single)
                return (Args != null && Args.Count > 0) ? Args[0] : "";
            if (Children != null && Children.Count > 0)
                return Children[0].PrimaryValue();
            return "";
        }

        public string PrimaryFn()
        {
            if (Kind == ConditionKind.Single) return Fn;
            if (Children != null && Children.Count > 0) return Children[0].PrimaryFn();
            return "";
        }
    }

    public enum ProxyKind { Direct, Proxy, Socks, Socks5, Https, Http }

    // One hop of an action fallback chain, e.g. "PROXY p1:8080" or "DIRECT".
    public class ProxyEntry
    {
        public ProxyKind Kind;
        public string Host;   // null for DIRECT
        public int Port;      // 0 for DIRECT

        public ProxyEntry() { }

        public static ProxyEntry Direct()
        {
            ProxyEntry e = new ProxyEntry();
            e.Kind = ProxyKind.Direct;
            return e;
        }
    }

    // A single PAC rule: condition -> action chain, plus metadata.
    public class Rule
    {
        public string Id;
        public int Order;                 // position in the chain; lower runs first
        public bool Enabled;
        public Condition Condition;
        public List<ProxyEntry> Action;   // fallback chain joined by ';'
        public string Comment;            // may span lines ('\n'); each round-trips as a // line above the rule
        public string Author;
        public string CreatedAt;          // ISO-8601 UTC
        public string UpdatedAt;

        public Rule()
        {
            Enabled = true;
            Action = new List<ProxyEntry>();
        }
    }

    // A block of original PAC text we could not map to a structured rule.
    // We keep it verbatim so import never loses data.
    public class UnparsedBlock
    {
        public string Id;
        public string RawText;
        public int SourceLineStart;
        public int SourceLineEnd;
        public string Reason;
        public string Comment;        // comments that preceded it (in-body blocks), '\n'-separated
        public string BeforeRuleId;   // in-body: the rule it preceded, so it keeps its place; null = after all rules
        public bool AfterDefault;     // in-body: it followed the default return (unreachable) and stays after it
    }

    // The container: an ordered list of rules + a default action (the trailing return),
    // plus anything we could not parse on import.
    public class RuleSet
    {
        public string Id;
        public string Name;
        public string Description;
        public List<Rule> Rules;
        public List<ProxyEntry> DefaultAction;   // the final `return "..."`; default DIRECT
        public List<UnparsedBlock> Unparsed;
        public string HeaderComment;             // file-level comments outside FindProxyForURL
        public string DefaultComment;            // comments above the final return
        public string CreatedBy;
        public string CreatedAt;
        public string UpdatedAt;

        public RuleSet()
        {
            Rules = new List<Rule>();
            DefaultAction = new List<ProxyEntry>();
            DefaultAction.Add(ProxyEntry.Direct());
            Unparsed = new List<UnparsedBlock>();
        }

        // Remove a rule, renumber the rest and keep preserved code in place: a block that sat
        // right before the removed rule now sits before the rule that followed it.
        public void RemoveRuleAt(int index)
        {
            Rule removed = Rules[index];
            string next = index + 1 < Rules.Count ? Rules[index + 1].Id : null;
            for (int i = 0; i < Unparsed.Count; i++)
                if (Unparsed[i].BeforeRuleId != null && Unparsed[i].BeforeRuleId == removed.Id)
                    Unparsed[i].BeforeRuleId = next;
            Rules.RemoveAt(index);
            for (int i = 0; i < Rules.Count; i++) Rules[i].Order = i;
        }

        // Swap a rule with its neighbour (delta -1 = up, +1 = down) and renumber. Preserved
        // code anchored to a rule moves with it. Returns the rule's new index, or -1 if it
        // cannot move that way.
        public int MoveRule(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || index >= Rules.Count || to < 0 || to >= Rules.Count) return -1;
            Rule r = Rules[index];
            Rules[index] = Rules[to];
            Rules[to] = r;
            for (int i = 0; i < Rules.Count; i++) Rules[i].Order = i;
            return to;
        }
    }
}

