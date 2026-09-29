using System;
using System.Collections.Generic;
using WpadManager.Core.Model;
using WpadManager.Core.Validate;
using WpadManager.Core.Generator;

namespace WpadManager.Core.Analyze
{
    public enum OverlapKind
    {
        SameAs,      // same match set: the new rule would never fire (or duplicates)
        CoveredBy,   // the existing rule already matches all of it: the new rule never fires
        Covers       // the new rule is broader; the existing rule still fires first for its part
    }

    // How a candidate rule relates to one existing rule (see Shadowing.CheckCandidate).
    public class Overlap
    {
        public OverlapKind Kind;
        public int Order;          // the existing rule
        public string Condition;   // its condition, as PAC text
    }

    // Order-aware overlap / shadowing detection.
    //
    // Rules are evaluated top-to-bottom and the FIRST matching rule wins. So if an
    // earlier rule i matches every request a later rule j would match (i's match-set
    // is a superset of j's), then j can never fire — it is unreachable.
    //
    // We model each condition as a boolean function over leaf predicates and compute a
    // *sound* (never a false "unreachable") subset relation Subset(a,b) = "every request
    // matching a also matches b". It is conservative: when we cannot prove a relation we
    // report nothing, preferring missed findings over false alarms.
    //
    // Reported relationships (i before j, both enabled):
    //   - equal sets, same action      -> WARNING  DUP_REDUNDANT   (j duplicates i)
    //   - equal sets, different action -> CRITICAL DUP_CONFLICT    (j never applies; i wins)
    //   - i superset of j (i broad)    -> CRITICAL SHADOW          (broad-before-narrow)
    //   - i subset of j (i narrow)     -> INFO     EXCEPTION       (valid specific carve-out)
    public static class Shadowing
    {
        public static Report Analyze(RuleSet rs)
        {
            Report report = new Report();
            if (rs == null) return report;

            List<Rule> rules = rs.Rules;
            for (int j = 1; j < rules.Count; j++)
            {
                Rule rj = rules[j];
                if (!rj.Enabled || rj.Condition == null) continue;

                bool jKilled = false;
                for (int i = 0; i < j; i++)
                {
                    Rule ri = rules[i];
                    if (!ri.Enabled || ri.Condition == null) continue;

                    bool iSupersetJ = Subset(rj.Condition, ri.Condition); // j ⊆ i
                    bool jSupersetI = Subset(ri.Condition, rj.Condition); // i ⊆ j

                    if (iSupersetJ && jSupersetI)
                    {
                        // Equal match-sets => duplicate condition.
                        if (SameAction(ri, rj))
                            report.Add(Finding.Make(Severity.Warning, "DUP_REDUNDANT",
                                "Rule (order " + rj.Order + ") duplicates rule (order " + ri.Order +
                                "): same condition and action.", rj.Id, rj.Order));
                        else
                            report.Add(Finding.Make(Severity.Critical, "DUP_CONFLICT",
                                "Rule (order " + rj.Order + ") never applies: same condition as rule (order " +
                                ri.Order + ") but a different action; the earlier rule wins.", rj.Id, rj.Order));
                        jKilled = true;
                        break;
                    }

                    if (iSupersetJ)
                    {
                        report.Add(Finding.Make(Severity.Critical, "SHADOW",
                            "Rule (order " + rj.Order + ") is unreachable: rule (order " + ri.Order +
                            ") already matches all of its traffic (broad rule before a narrower one).",
                            rj.Id, rj.Order));
                        jKilled = true;
                        break;
                    }

                    if (jSupersetI)
                    {
                        // i is a more specific exception placed ahead of the broader j: valid.
                        report.Add(Finding.Make(Severity.Info, "EXCEPTION",
                            "Rule (order " + ri.Order + ") is a more specific exception ahead of the broader rule (order " +
                            rj.Order + ").", ri.Id, ri.Order));
                        // keep scanning: a different earlier rule might still shadow j
                    }
                }
                if (jKilled) continue;
            }
            return report;
        }

        // Pre-add check: how a candidate condition (to be appended as the last rule) relates
        // to each existing enabled rule. Used to warn an engineer that a new rule (e.g.
        // "test.example.com") is already covered by an existing one (e.g. ".example.com").
        // The UI turns these into text in its own language.
        public static List<Overlap> CheckCandidate(RuleSet rs, Condition candidate)
        {
            List<Overlap> outp = new List<Overlap>();
            if (rs == null || candidate == null) return outp;
            for (int i = 0; i < rs.Rules.Count; i++)
            {
                Rule r = rs.Rules[i];
                if (!r.Enabled || r.Condition == null) continue;
                bool candInExisting = Subset(candidate, r.Condition); // candidate ⊆ existing
                bool existingInCand = Subset(r.Condition, candidate);  // existing ⊆ candidate
                OverlapKind kind;
                if (candInExisting && existingInCand) kind = OverlapKind.SameAs;
                else if (candInExisting) kind = OverlapKind.CoveredBy;
                else if (existingInCand) kind = OverlapKind.Covers;
                else continue;
                Overlap o = new Overlap();
                o.Kind = kind;
                o.Order = r.Order;
                o.Condition = PacGenerator.GenCondition(r.Condition);
                outp.Add(o);
            }
            return outp;
        }

        private static bool SameAction(Rule a, Rule b)
        {
            return ActionText.Format(a.Action) == ActionText.Format(b.Action);
        }

        // -------- set-subsumption over the condition tree --------
        // Returns true only when we can PROVE that every request matching `a`
        // also matches `b` (a ⊆ b). Sound but intentionally incomplete.

        internal static bool Subset(Condition a, Condition b)
        {
            if (a == null || b == null) return false;

            // Universal splits first (always sound, improve completeness):
            // (a1 OR a2) ⊆ b  iff  a1 ⊆ b AND a2 ⊆ b
            if (IsOr(a))
            {
                for (int i = 0; i < a.Children.Count; i++)
                    if (!Subset(a.Children[i], b)) return false;
                return true;
            }
            // a ⊆ (b1 AND b2)  iff  a ⊆ b1 AND a ⊆ b2
            if (IsAnd(b))
            {
                for (int i = 0; i < b.Children.Count; i++)
                    if (!Subset(a, b.Children[i])) return false;
                return true;
            }
            // Existential splits:
            // a ⊆ (b1 OR b2)  if  a ⊆ b1 OR a ⊆ b2
            if (IsOr(b))
            {
                for (int i = 0; i < b.Children.Count; i++)
                    if (Subset(a, b.Children[i])) return true;
                return false;
            }
            // (a1 AND a2) ⊆ b  if  a1 ⊆ b OR a2 ⊆ b  (intersection ⊆ any conjunct ⊆ b)
            if (IsAnd(a))
            {
                for (int i = 0; i < a.Children.Count; i++)
                    if (Subset(a.Children[i], b)) return true;
                return false;
            }

            // Both are single leaves (or degenerate composites handled above).
            if (a.Kind == ConditionKind.Single && b.Kind == ConditionKind.Single)
                return LeafSubset(a, b);

            return false;
        }

        private static bool IsAnd(Condition c)
        {
            return c.Kind == ConditionKind.Composite && c.Op == CompositeOp.And
                   && !c.Negate && c.Children != null && c.Children.Count > 0;
        }

        private static bool IsOr(Condition c)
        {
            return c.Kind == ConditionKind.Composite && c.Op == CompositeOp.Or
                   && !c.Negate && c.Children != null && c.Children.Count > 0;
        }

        // a ⊆ b for two single-predicate leaves. Conservative on negation/subject.
        private static bool LeafSubset(Condition a, Condition b)
        {
            // Negation makes set reasoning tricky; only allow the exact-equal case.
            if (a.Negate || b.Negate)
                return a.Negate == b.Negate && LeafEquals(a, b);

            // Different subject variable (host vs url) => not comparable.
            if (!SubjectMatch(a.Subject, b.Subject)) return false;

            if (a.Fn != b.Fn) return false;

            string av = Arg(a, 0);
            string bv = Arg(b, 0);

            switch (a.Fn)
            {
                case "dnsDomainIs":
                    // Suffix match, compared exactly (browsers pass the host in lower case):
                    // every host ending with av also ends with bv iff av ends with bv.
                    if (av == null || bv == null) return false;
                    return av.EndsWith(bv, StringComparison.Ordinal);

                case "localHostOrDomainIs":
                    // Matches the full name hd, or a leading part of it ending at a dot
                    // ("www" for "www.example.com") — NOT a suffix. The set for av lies inside
                    // the set for bv iff av is bv or a leading part of it.
                    if (av == null || bv == null) return false;
                    return av == bv || bv.StartsWith(av + ".", StringComparison.Ordinal);

                case "shExpMatch":
                    if (av == null || bv == null) return false;
                    if (bv == "*" || bv == "*:*" || bv == "*://*/*") return true;
                    return string.Equals(av, bv, StringComparison.Ordinal);

                case "isInNet":
                    return IsInNetSubset(a, b);

                case "isInNetEx":
                    return string.Equals(av, bv, StringComparison.OrdinalIgnoreCase);

                case "isPlainHostName":
                case "isResolvable":
                case "isResolvableEx":
                    return true; // same no-arg predicate, same subject => equal sets

                default:
                    return LeafEquals(a, b);
            }
        }

        private static bool LeafEquals(Condition a, Condition b)
        {
            if (a.Fn != b.Fn) return false;
            if (!SubjectMatch(a.Subject, b.Subject)) return false;
            int an = a.Args != null ? a.Args.Count : 0;
            int bn = b.Args != null ? b.Args.Count : 0;
            if (an != bn) return false;
            for (int i = 0; i < an; i++)
                if (!string.Equals(a.Args[i], b.Args[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // isInNet(host, netA, maskA) ⊆ isInNet(host, netB, maskB) iff subnet A is inside subnet B:
        // maskB is no longer than maskA (fewer/equal one-bits) and A's network falls in B.
        private static bool IsInNetSubset(Condition a, Condition b)
        {
            byte[] netA, maskA, netB, maskB;
            if (!Validator.TryParseIPv4(Arg(a, 0), out netA)) return LeafEquals(a, b);
            if (!Validator.TryParseIPv4(Arg(a, 1), out maskA)) return LeafEquals(a, b);
            if (!Validator.TryParseIPv4(Arg(b, 0), out netB)) return LeafEquals(a, b);
            if (!Validator.TryParseIPv4(Arg(b, 1), out maskB)) return LeafEquals(a, b);

            uint mA = U(maskA), mB = U(maskB);
            // B must be the same width or broader: every bit set in mB is set in mA.
            if ((mB & ~mA) != 0) return false;
            // A's network address, masked to B, must equal B's network masked to B.
            return (U(netA) & mB) == (U(netB) & mB);
        }

        private static uint U(byte[] b)
        {
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static bool SubjectMatch(string sa, string sb)
        {
            if (sa == null || sb == null) return true; // unknown subject: don't block comparison
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        private static string Arg(Condition c, int i)
        {
            if (c.Args == null || i >= c.Args.Count) return null;
            return c.Args[i];
        }
    }
}
