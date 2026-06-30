using System;
using System.IO;
using System.Collections.Generic;
using WpadManager.Core.Model;
using WpadManager.Core.Parser;
using WpadManager.Core.Generator;
using WpadManager.Core.Validate;
using WpadManager.Core.Analyze;
using WpadManager.Core.Simulate;
using WpadManager.Core.Storage;

namespace WpadManager.App
{
    // Headless command-line interface for automation/CI. Returns a process exit code:
    // 0 = OK, 1 = problem found (errors / bad usage / missing file).
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            string cmd = args[0].ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "--validate": case "-v": return Validate(args);
                    case "--simulate": case "-s": return Simulate(args);
                    case "--export": case "-e": return Export(args);
                    case "--help": case "-h": case "/?": Usage(); return 0;
                    default:
                        Console.WriteLine("Unknown command: " + cmd);
                        Usage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Validate(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("usage: --validate <file.pac>"); return 1; }
            string path = args[1];
            if (!File.Exists(path)) { Console.WriteLine("File not found: " + path); return 1; }

            string src = File.ReadAllText(path);
            PacImportResult res = PacImporter.Import(src);
            if (!res.Ok)
            {
                Console.WriteLine("SYNTAX ERROR (line " + res.SyntaxErrorLine + "): " + res.SyntaxError);
                return 1;
            }
            Console.WriteLine("Parsed " + res.RuleSet.Rules.Count + " rule(s).");

            Report combined = new Report();
            Merge(combined, Safety.Analyze(src));
            Merge(combined, Validator.Validate(res.RuleSet));
            Merge(combined, Shadowing.Analyze(res.RuleSet));

            PrintFindings(combined);
            return combined.HasErrors ? 1 : 0;
        }

        private static int Simulate(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("usage: --simulate <file.pac> <url> <host>");
                return 1;
            }
            string path = args[1], url = args[2], host = args[3];
            if (!File.Exists(path)) { Console.WriteLine("File not found: " + path); return 1; }

            PacImportResult res = PacImporter.Import(File.ReadAllText(path));
            if (!res.Ok) { Console.WriteLine("SYNTAX ERROR: " + res.SyntaxError); return 1; }

            SimInput input = new SimInput(url, host);
            input.Now = DateTime.Now;
            SimResult r = Simulator.Run(res.RuleSet, input);

            Console.WriteLine("URL : " + url);
            Console.WriteLine("HOST: " + host);
            Console.WriteLine("---- trace ----");
            for (int i = 0; i < r.Trace.Count; i++)
            {
                SimStep s = r.Trace[i];
                Console.WriteLine("  rule " + s.Order + " [" + s.Result + "] " + s.Reason);
            }
            Console.WriteLine("---- result ----");
            if (r.Matched != null) Console.WriteLine("MATCHED rule " + r.Matched.Order);
            else Console.WriteLine("MATCHED: <default>");
            Console.WriteLine("PROXY  : " + r.ActionString());
            if (r.HasIndeterminateBeforeMatch)
                Console.WriteLine("NOTE   : an earlier rule was indeterminate (live DNS/time) and could change this at runtime.");
            return 0;
        }

        private static int Export(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: --export <store.json> <out.pac>");
                return 1;
            }
            string storePath = args[1], outPath = args[2];
            if (!File.Exists(storePath)) { Console.WriteLine("Store not found: " + storePath); return 1; }

            Store store = RuleStore.Load(storePath);
            string pac = PacGenerator.Generate(store.Current);
            File.WriteAllText(outPath, pac);
            Console.WriteLine("Wrote " + outPath + " (" + store.Current.Rules.Count + " rules).");
            return 0;
        }

        // ---- helpers ----

        private static void Merge(Report into, Report from)
        {
            for (int i = 0; i < from.Findings.Count; i++) into.Add(from.Findings[i]);
        }

        private static void PrintFindings(Report rep)
        {
            if (rep.Findings.Count == 0) { Console.WriteLine("No findings. OK."); return; }
            Console.WriteLine("---- findings ----");
            for (int i = 0; i < rep.Findings.Count; i++)
            {
                Finding f = rep.Findings[i];
                string loc = f.Order >= 0 ? ("rule " + f.Order) : (f.Line > 0 ? ("line " + f.Line) : "");
                Console.WriteLine("  [" + f.Severity.ToString().ToUpperInvariant() + "] " +
                    f.Code + (loc.Length > 0 ? " (" + loc + ")" : "") + ": " + f.Message);
            }
            Console.WriteLine("Summary: " +
                rep.Count(Severity.Critical) + " critical, " +
                rep.Count(Severity.Error) + " error, " +
                rep.Count(Severity.Warning) + " warning, " +
                rep.Count(Severity.Info) + " info.");
        }

        private static void Usage()
        {
            Console.WriteLine("WPAD/PAC File Manager");
            Console.WriteLine("  (no args)                              open the GUI");
            Console.WriteLine("  --validate <file.pac>                  syntax + safety + rules + shadowing");
            Console.WriteLine("  --simulate <file.pac> <url> <host>     which rule fires and final proxy");
            Console.WriteLine("  --export   <store.json> <out.pac>      generate a .pac from a saved store");
        }
    }
}
