using System;
using System.Collections.Generic;

namespace WpadManager.Core.Validate
{
    public enum Severity { Info, Warning, Error, Critical }

    // A single diagnostic produced by validation or analysis.
    public class Finding
    {
        public Severity Severity;
        public string Code;      // stable machine code, e.g. "FN_UNKNOWN"
        public string Message;   // human-readable
        public string RuleId;    // affected rule (may be null)
        public int Order;        // affected rule order (-1 if n/a)
        public int Line;         // source line (0 if n/a)

        public Finding() { Order = -1; }

        public static Finding Make(Severity sev, string code, string message, string ruleId, int order)
        {
            Finding f = new Finding();
            f.Severity = sev; f.Code = code; f.Message = message; f.RuleId = ruleId; f.Order = order;
            return f;
        }
    }

    public class Report
    {
        public List<Finding> Findings = new List<Finding>();

        public void Add(Finding f) { Findings.Add(f); }

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Findings.Count; i++)
                    if (Findings[i].Severity == Severity.Error || Findings[i].Severity == Severity.Critical)
                        return true;
                return false;
            }
        }

        public int Count(Severity s)
        {
            int n = 0;
            for (int i = 0; i < Findings.Count; i++) if (Findings[i].Severity == s) n++;
            return n;
        }
    }

    // String distance for "did you mean" suggestions on misspelled PAC function names.
    public static class Levenshtein
    {
        public static int Distance(string a, string b)
        {
            if (a == null) a = "";
            if (b == null) b = "";
            int n = a.Length, m = b.Length;
            int[,] d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int del = d[i - 1, j] + 1;
                    int ins = d[i, j - 1] + 1;
                    int sub = d[i - 1, j - 1] + cost;
                    int min = del < ins ? del : ins;
                    if (sub < min) min = sub;
                    d[i, j] = min;
                }
            }
            return d[n, m];
        }

        public static string NearestKnown(string name, string[] candidates)
        {
            string best = null;
            int bestD = int.MaxValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                int dd = Distance(name, candidates[i]);
                if (dd < bestD) { bestD = dd; best = candidates[i]; }
            }
            if (best != null && bestD <= 3) return best;  // only suggest if reasonably close
            return null;
        }
    }
}
