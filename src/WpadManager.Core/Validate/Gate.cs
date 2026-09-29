using System;
using System.Collections.Generic;
using WpadManager.Core.Analyze;
using WpadManager.Core.Generator;
using WpadManager.Core.Model;

namespace WpadManager.Core.Validate
{
    // The single "may this be written?" check shared by every path that produces a PAC
    // file: save, export, history restore and the CLI export. It generates the PAC once,
    // runs the security pass on exactly that text (plus structure and shadowing checks on
    // the model) and hands the same text back — callers write what was vetted, never a
    // second, unchecked rendering.
    public static class Gate
    {
        public static Report Check(RuleSet rs, out string pac)
        {
            Report rep = new Report();

            // The safety pass reports by line; translate those lines back to the rule the
            // operator sees, so findings read "rule N".
            Dictionary<int, int> lineToOrder;
            pac = PacGenerator.Generate(rs, out lineToOrder);
            Report safety = Safety.Analyze(pac);
            for (int i = 0; i < safety.Findings.Count; i++)
            {
                Finding f = safety.Findings[i];
                int order;
                if (f.Order < 0 && f.Line > 0 && lineToOrder.TryGetValue(f.Line, out order))
                {
                    f.Order = order;
                    f.Line = 0;
                }
                rep.Add(f);
            }

            Append(rep, Validator.Validate(rs));
            Append(rep, Shadowing.Analyze(rs));
            return rep;
        }

        // Writing is allowed only when nothing above Info remains (Info covers e.g. valid
        // narrow-before-broad exceptions and preserved helper code).
        public static bool IsBlocking(Report rep)
        {
            for (int i = 0; i < rep.Findings.Count; i++)
                if (rep.Findings[i].Severity != Severity.Info) return true;
            return false;
        }

        private static void Append(Report into, Report from)
        {
            for (int i = 0; i < from.Findings.Count; i++) into.Add(from.Findings[i]);
        }
    }
}
