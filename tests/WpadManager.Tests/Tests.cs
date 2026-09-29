using System;
using System.Collections.Generic;
using WpadManager.Core.Model;
using WpadManager.Core.Parser;
using WpadManager.Core.Generator;
using WpadManager.Core.Json;
using WpadManager.Core.Validate;
using WpadManager.Core.Analyze;
using WpadManager.Core.Simulate;
using WpadManager.Core.Storage;
using WpadManager.Core.Resolve;

namespace WpadManager.Tests
{
    public static class T
    {
        static int total = 0;
        static int fails = 0;

        static void Check(bool cond, string name)
        {
            total++;
            if (cond) { Console.WriteLine("  PASS " + name); }
            else { fails++; Console.WriteLine("  FAIL " + name); }
        }

        static void Eq(object expected, object actual, string name)
        {
            Check(object.Equals(expected, actual), name + " [exp=" + expected + " got=" + actual + "]");
        }

        public static int Main()
        {
            Console.WriteLine("== Parser / Recognizer ==");
            ParseAndRecognize();
            Console.WriteLine("== Round-trip ==");
            RoundTrip();
            Console.WriteLine("== Edge cases ==");
            EdgeCases();
            Console.WriteLine("== JSON ==");
            JsonRoundTrip();
            Console.WriteLine("== Validation ==");
            Validation();
            Console.WriteLine("== Safety ==");
            SafetyChecks();
            Console.WriteLine("== Security hardening ==");
            SecurityHardeningChecks();
            Console.WriteLine("== Write gate ==");
            GateChecks();
            Console.WriteLine("== Shadowing ==");
            ShadowingChecks();
            Console.WriteLine("== Simulator ==");
            SimulatorChecks();
            Console.WriteLine("== Storage ==");
            StorageChecks();
            Console.WriteLine("== Duplicate guard / DNS names ==");
            DuplicateAndDnsChecks();
            Console.WriteLine("== Line -> rule mapping ==");
            LineMapChecks();
            Console.WriteLine("== Workspace / multi-file ==");
            WorkspaceChecks();

            Console.WriteLine();
            Console.WriteLine("TOTAL " + total + ", FAILED " + fails);
            return fails == 0 ? 0 : 1;
        }

        static void ParseAndRecognize()
        {
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  // direct for internal\n" +
                "  if (isPlainHostName(host)) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".example.com\") || dnsDomainIs(host, \".test.com\")) return \"PROXY p1:8080; DIRECT\";\n" +
                "  if (shExpMatch(url, \"http://ads.*/*\")) return \"PROXY block:1\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            PacImportResult res = PacImporter.Import(pac);
            Check(res.Ok, "import ok");
            Eq(3, res.RuleSet.Rules.Count, "rule count");

            Rule r0 = res.RuleSet.Rules[0];
            Eq("isPlainHostName", r0.Condition.Fn, "r0 fn");
            Eq("host", r0.Condition.Subject, "r0 subject");
            Eq("direct for internal", r0.Comment, "r0 comment preserved");

            Rule r1 = res.RuleSet.Rules[1];
            Eq(ConditionKind.Composite, r1.Condition.Kind, "r1 composite");
            Eq(CompositeOp.Or, r1.Condition.Op, "r1 OR");
            Eq(2, r1.Condition.Children.Count, "r1 children");
            Eq(2, r1.Action.Count, "r1 action chain length");
            Eq(ProxyKind.Proxy, r1.Action[0].Kind, "r1 action0 proxy");
            Eq(8080, r1.Action[0].Port, "r1 action0 port");
            Eq(ProxyKind.Direct, r1.Action[1].Kind, "r1 action1 direct");

            Rule r2 = res.RuleSet.Rules[2];
            Eq("shExpMatch", r2.Condition.Fn, "r2 fn");
            Eq("url", r2.Condition.Subject, "r2 subject url");

            Eq(1, res.RuleSet.DefaultAction.Count, "default action count");
            Eq(ProxyKind.Direct, res.RuleSet.DefaultAction[0].Kind, "default DIRECT");

            // generated condition text for the OR rule
            string gen = PacGenerator.GenCondition(r1.Condition);
            Eq("dnsDomainIs(host, \".example.com\") || dnsDomainIs(host, \".test.com\")", gen, "r1 gen condition");
        }

        static void RoundTrip()
        {
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (!shExpMatch(url, \"*.local\")) return \"PROXY p:1\";\n" +
                "  if (isInNet(host, \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  return \"PROXY edge:8080; DIRECT\";\n" +
                "}\n";
            PacImportResult res = PacImporter.Import(pac);
            Check(res.Ok, "rt import ok");
            Eq(2, res.RuleSet.Rules.Count, "rt rule count");
            Check(res.RuleSet.Rules[0].Condition.Negate, "rt negation captured");

            string outPac = PacGenerator.Generate(res.RuleSet);
            PacImportResult res2 = PacImporter.Import(outPac);
            Check(res2.Ok, "rt re-import ok");
            Eq(res.RuleSet.Rules.Count, res2.RuleSet.Rules.Count, "rt stable rule count");
            Check(res2.RuleSet.Rules[0].Condition.Negate, "rt negation survives round-trip");
            Eq("10.0.0.0", res2.RuleSet.Rules[1].Condition.Args[0], "rt isInNet arg survives");

            // disabled rule round-trips as a commented (inert) line
            res.RuleSet.Rules[1].Enabled = false;
            string outPac2 = PacGenerator.Generate(res.RuleSet);
            Check(outPac2.Contains("[disabled]"), "disabled rule commented");
            PacImportResult res3 = PacImporter.Import(outPac2);
            Eq(1, res3.RuleSet.Rules.Count, "disabled rule not active after round-trip");
        }

        static void EdgeCases()
        {
            PacImportResult empty = PacImporter.Import("");
            Check(empty.Ok, "empty import does not crash");
            Eq(0, empty.RuleSet.Rules.Count, "empty has no rules");
            Check(empty.RuleSet.Unparsed.Count >= 1, "empty kept as unparsed");

            // unknown / non-predicate condition -> unparsed, not lost
            string weird =
                "function FindProxyForURL(url, host) {\n" +
                "  if (host == \"srv\") return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".ok.com\")) return \"PROXY p:1\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            PacImportResult w = PacImporter.Import(weird);
            Check(w.Ok, "weird import ok");
            Eq(1, w.RuleSet.Rules.Count, "only the recognizable rule mapped");
            Check(w.RuleSet.Unparsed.Count >= 1, "weird condition kept as unparsed");

            // hard syntax error reported, not silently swallowed
            PacImportResult bad = PacImporter.Import("function FindProxyForURL(url, host) { if (");
            Check(!bad.Ok, "syntax error flagged");
            Check(bad.SyntaxErrorLine >= 1, "syntax error has line");

            // typo in function name still maps (validator will flag it later)
            string typo =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainls(host, \".x.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            PacImportResult tp = PacImporter.Import(typo);
            Eq(1, tp.RuleSet.Rules.Count, "typo'd fn still recognized structurally");
            Eq("dnsDomainls", tp.RuleSet.Rules[0].Condition.Fn, "typo fn name preserved");

            // Nested-call subject: isInNet(myIpAddress(), ...) must be recognized (real WPAD idiom).
            string myip =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isInNet(myIpAddress(), \"10.0.0.0\", \"255.0.0.0\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            PacImportResult mi = PacImporter.Import(myip);
            Eq(1, mi.RuleSet.Rules.Count, "isInNet(myIpAddress()) recognized as a rule");
            Eq("myIpAddress()", mi.RuleSet.Rules[0].Condition.Subject, "myIpAddress() subject captured");
            Eq("10.0.0.0", mi.RuleSet.Rules[0].Condition.Args[0], "isInNet net arg captured");
            string regen = PacGenerator.Generate(mi.RuleSet);
            Check(regen.Contains("isInNet(myIpAddress(), \"10.0.0.0\", \"255.0.0.0\")"),
                "myIpAddress() condition round-trips");

            // Simulator: matches on client IP when supplied, indeterminate otherwise.
            SimInput inClient = new SimInput("http://x/", "x");
            inClient.MyIp = "10.5.5.5";
            SimResult srC = Simulator.Run(mi.RuleSet, inClient);
            Check(srC.Matched != null && srC.Matched.Order == 0, "myIpAddress matches client subnet");
            SimResult srU = Simulator.Run(mi.RuleSet, new SimInput("http://x/", "x"));
            Check(srU.UsedDefault, "no client IP => indeterminate => default");
        }

        static void JsonRoundTrip()
        {
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"PROXY p1:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            RuleSet rs = PacImporter.Import(pac).RuleSet;
            string json = Json.Stringify(rs);
            Check(json.Contains("dnsDomainIs"), "json contains fn");
            Check(json.Contains("\"Kind\""), "json has enum-bearing field");
            RuleSet rs2 = Json.Deserialize<RuleSet>(json);
            Eq(rs.Rules.Count, rs2.Rules.Count, "json rule count");
            Eq("dnsDomainIs", rs2.Rules[0].Condition.Fn, "json fn survives");
            Eq(ProxyKind.Proxy, rs2.Rules[0].Action[0].Kind, "json enum survives as Proxy");
            Eq(8080, rs2.Rules[0].Action[0].Port, "json port survives");
        }

        // ---- Validation helpers ----

        static bool Has(Report rep, string code)
        {
            for (int i = 0; i < rep.Findings.Count; i++)
                if (rep.Findings[i].Code == code) return true;
            return false;
        }

        static Finding First(Report rep, string code)
        {
            for (int i = 0; i < rep.Findings.Count; i++)
                if (rep.Findings[i].Code == code) return rep.Findings[i];
            return null;
        }

        static RuleSet Imported(string pac) { return PacImporter.Import(pac).RuleSet; }

        static void Validation()
        {
            // A clean, well-formed PAC produces no Error/Critical findings.
            string clean =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isPlainHostName(host)) return \"DIRECT\";\n" +
                "  if (isInNet(host, \"10.0.0.0\", \"255.255.0.0\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".corp.com\")) return \"PROXY edge:8080; DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report cr = Validator.Validate(Imported(clean));
            Check(!cr.HasErrors, "clean ruleset has no errors");
            Eq(0, cr.Count(Severity.Critical), "clean ruleset no critical");

            // Unknown function => Error + "did you mean" suggestion.
            string typo =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainls(host, \".x.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report tr = Validator.Validate(Imported(typo));
            Check(Has(tr, "FN_UNKNOWN"), "unknown function flagged");
            Finding fu = First(tr, "FN_UNKNOWN");
            Check(fu != null && fu.Message.Contains("dnsDomainIs"), "suggests nearest function");

            // Wrong argument count: dnsDomainIs needs one literal domain.
            string args =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host)) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Validator.Validate(Imported(args)), "FN_ARGS"), "wrong arg count flagged");

            // Non-predicate function used as a condition.
            string nonpred =
                "function FindProxyForURL(url, host) {\n" +
                "  if (myIpAddress()) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Validator.Validate(Imported(nonpred)), "FN_NOT_PREDICATE"), "non-predicate flagged");

            // Bad isInNet IP and non-contiguous mask.
            string badnet =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isInNet(host, \"10.0.0.999\", \"255.0.1.0\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report nr = Validator.Validate(Imported(badnet));
            Check(Has(nr, "NET_BAD_IP"), "invalid network IP flagged");
            Check(Has(nr, "NET_MASK_NONCONTIG"), "non-contiguous mask flagged");

            // Action port out of range.
            string badport =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"PROXY p:99999\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Validator.Validate(Imported(badport)), "ACT_BAD_PORT"), "out-of-range port flagged");

            // Unreachable-after-return surfaces from the recognizer's unparsed block.
            string unreach =
                "function FindProxyForURL(url, host) {\n" +
                "  return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"PROXY p:8080\";\n" +
                "}\n";
            Check(Has(Validator.Validate(Imported(unreach)), "UNPARSED"), "unreachable code surfaced");
        }

        static void SafetyChecks()
        {
            // Clean PAC: no critical security findings.
            string clean =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".corp.com\")) return \"DIRECT\";\n" +
                "  return \"PROXY edge:8080; DIRECT\";\n" +
                "}\n";
            Report sc = Safety.Analyze(clean);
            Eq(0, sc.Count(Severity.Critical), "clean PAC has no critical security findings");

            // eval() is forbidden.
            string ev =
                "function FindProxyForURL(url, host) {\n" +
                "  if (eval(\"isPlainHostName(host)\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report er = Safety.Analyze(ev);
            Check(Has(er, "SEC_DANGEROUS_CALL"), "eval flagged as dangerous");
            Check(er.HasErrors, "dangerous call is an error-level finding");

            // Host-object access (document.*) is forbidden.
            string doc =
                "function FindProxyForURL(url, host) {\n" +
                "  var c = document.cookie;\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Safety.Analyze(doc), "SEC_HOST_ACCESS"), "document access flagged");

            // A non-whitelisted, non-local call is a warning (typo'd helper).
            string typo =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainls(host, \".x.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Safety.Analyze(typo), "SEC_UNKNOWN_CALL"), "unknown helper call flagged");
        }

        // Regression tests for the 2026-09 security review: places where the parser saw a
        // different program than a browser would run (so code slipped past the Safety pass),
        // and stored text that could break out of a comment or string when regenerated.
        static void SecurityHardeningChecks()
        {
            string head = "function FindProxyForURL(url, host) {\n";
            string tail = "  return \"DIRECT\";\n}\n";

            // Code inside a computed member index is part of the AST and gets vetted.
            Report idx = Safety.Analyze("var x = host[eval(\"1\")];\n" + head + tail);
            Check(Has(idx, "SEC_DANGEROUS_CALL") && idx.HasErrors, "eval inside [ ] is flagged");

            // Overriding a PAC built-in, or aliasing a dangerous global, is flagged.
            Report ovr = Safety.Analyze("dnsResolve = eval;\n" + head + "  dnsResolve(\"1\");\n" + tail);
            Check(Has(ovr, "SEC_BUILTIN_OVERRIDE") && ovr.HasErrors, "assigning to a PAC built-in is flagged");
            Check(Has(ovr, "SEC_DANGEROUS_REF"), "bare reference to eval is flagged");
            Check(Has(Safety.Analyze("function dnsDomainIs(h, d) { return true; }\n" + head + tail),
                "SEC_BUILTIN_OVERRIDE"), "redefining a PAC built-in function is flagged");
            Check(Has(Safety.Analyze("var isInNet = 1;\n" + head + tail), "SEC_BUILTIN_OVERRIDE"),
                "var shadowing a PAC built-in is flagged");

            // Prototype-chain escapes and `this`.
            Check(Has(Safety.Analyze(head + "  var f = host[\"constructor\"];\n" + tail), "SEC_PROTO_ACCESS"),
                "['constructor'] access is flagged");
            Check(Has(Safety.Analyze(head + "  var f = url.constructor;\n" + tail), "SEC_PROTO_ACCESS"),
                ".constructor access is flagged");
            Check(Safety.Analyze(head + "  this.x = 1;\n" + tail).HasErrors, "access through `this` is an error");

            // JS keywords the subset parser does not model cannot be vetted.
            Report kw = Safety.Analyze(head + "  while (true) { }\n" + tail);
            Check(Has(kw, "SEC_UNSUPPORTED") && kw.HasErrors, "unsupported keyword (while) is an error");

            // CR, U+2028 and U+2029 end a // comment in JavaScript, so what follows is code.
            Check(Has(Safety.Analyze(head + "  // note\reval(\"1\");\n" + tail), "SEC_DANGEROUS_CALL"),
                "code after CR in a // comment is vetted");
            Check(Has(Safety.Analyze(head + "  // note\u2028eval(\"1\");\n" + tail), "SEC_DANGEROUS_CALL"),
                "code after U+2028 in a // comment is vetted");
            Check(Has(Safety.Analyze(head + "  // note\u2029eval(\"1\");\n" + tail), "SEC_DANGEROUS_CALL"),
                "code after U+2029 in a // comment is vetted");
            RuleSet crlf = Imported(head.Replace("\n", "\r\n") +
                "  // note\r\n  if (isPlainHostName(host)) return \"DIRECT\";\r\n" + tail.Replace("\n", "\r\n"));
            Check(crlf.Rules.Count == 1 && crlf.Rules[0].Comment == "note", "CRLF comment still attaches to its rule");

            // A DNS lookup of a computed name can leak every visited host to an outside resolver.
            Report exf = Safety.Analyze(head + "  if (isResolvable(host + \".x.example.net\")) return \"DIRECT\";\n" + tail);
            Check(Has(exf, "SEC_DNS_COMPUTED") && exf.HasErrors, "DNS lookup of a computed name is an error");
            Check(Has(exf, "SEC_NAME_BUILD"), "building a name from host is flagged");
            Report okDns = Safety.Analyze(head +
                "  if (isInNet(myIpAddress(), \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  if (isInNet(dnsResolve(host), \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  if (isResolvable(host)) return \"DIRECT\";\n" + tail);
            Eq(0, okDns.Findings.Count, "ordinary DNS helpers produce no findings");

            // String escapes decode like JavaScript; raw line breaks in strings are errors.
            RuleSet hx = Imported(head + "  if (dnsDomainIs(host, \"\\x2eexample.com\")) return \"DIRECT\";\n" + tail);
            Eq(".example.com", hx.Rules[0].Condition.Args[0], "\\x escape decoded");
            RuleSet ux = Imported(head + "  if (dnsDomainIs(host, \"\\u002eexample.com\")) return \"DIRECT\";\n" + tail);
            Eq(".example.com", ux.Rules[0].Condition.Args[0], "\\u escape decoded");
            Check(!PacImporter.Import(head + "  if (dnsDomainIs(host, \"a\rb\")) return \"DIRECT\";\n" + tail).Ok,
                "raw CR inside a string literal is a syntax error");

            // Generator: stored text (e.g. from an edited .history.json) stays inert.
            RuleSet rs = Imported(head + "  if (dnsDomainIs(host, \".example.com\")) return \"DIRECT\";\n" + tail);
            rs.Name = "n\nvar a = eval(\"1\");";
            rs.Rules[0].Comment = "c\u2028var b = eval(\"2\");";
            rs.Rules[0].Condition.Args[0] = ".example.com\u2028\r\n\"x";
            UnparsedBlock top = new UnparsedBlock();
            top.Reason = "Top-level x\rvar c = eval(\"3\");"; top.RawText = "";
            rs.Unparsed.Add(top);
            UnparsedBlock body = new UnparsedBlock();
            body.Reason = "r */ var d = eval(\"4\"); /*"; body.RawText = "";
            rs.Unparsed.Add(body);
            string gen = PacGenerator.Generate(rs);
            Eq(0, Safety.Analyze(gen).Findings.Count, "injected comment/string text stays inert in generated PAC");
            PacImportResult back = PacImporter.Import(gen);
            Check(back.Ok && back.RuleSet.Rules.Count == 1, "generated PAC re-imports with one rule");
            Check(back.Ok && back.RuleSet.Rules[0].Condition.Args[0] == ".example.com\u2028\r\n\"x",
                "string with line breaks and quotes round-trips exactly");

            // A malformed subject / function name is an error and is never emitted as code.
            RuleSet sub = Imported(head + "  if (dnsDomainIs(host, \".example.com\")) return \"DIRECT\";\n" + tail);
            sub.Rules[0].Condition.Subject = "host) || true || (host";
            Check(Has(Validator.Validate(sub), "COND_BAD_SUBJECT"), "malformed subject is an error");
            Check(!PacGenerator.Generate(sub).Contains("|| true ||"), "malformed subject is never emitted");
            sub.Rules[0].Condition.Subject = "host";
            sub.Rules[0].Condition.Fn = "x) || eval(\"1\") || (y";
            Check(!PacGenerator.Generate(sub).Contains("eval"), "malformed function name is never emitted");
        }

        // Every path that writes a PAC (save, export, history restore, CLI export) goes
        // through Gate.Check, which vets the exact text it returns for writing.
        static void GateChecks()
        {
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isPlainHostName(host)) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".example.com\")) return \"DIRECT\";\n" +
                "  if (isInNet(myIpAddress(), \"10.10.0.0\", \"255.255.255.0\")) return \"PROXY a.example.com:3128; DIRECT\";\n" +
                "  return \"PROXY main.example.com:3128; DIRECT\";\n" +
                "}\n";

            string text;
            Report ok = Gate.Check(Imported(pac), out text);
            Check(!Gate.IsBlocking(ok), "gate: clean rule set passes");
            Check(text.Contains("function FindProxyForURL") && text.Contains("main.example.com:3128"),
                "gate: returns the generated PAC text");
            Check(Safety.Analyze(text).Findings.Count == 0, "gate: returned text is itself clean");

            // A tampered snapshot is blocked, with the finding attributed to its rule.
            RuleSet bad = Imported(pac);
            bad.Rules[1].Condition.Subject = "eval(\"1\")";
            Report br = Gate.Check(bad, out text);
            Check(Gate.IsBlocking(br) && br.HasErrors, "gate: injected call blocks the write");
            Finding hit = First(br, "SEC_DANGEROUS_CALL");
            Check(hit != null && hit.Order == 1, "gate: security finding attributed to rule 1");

            // Same, end to end through a history sidecar on disk (the rollback source).
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wpad-gate-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".history.json");
            try
            {
                Store st = new Store();
                RuleStore.Commit(st, Imported(pac), "tester", "v1");
                RuleStore.Save(tmp, st);
                string json = System.IO.File.ReadAllText(tmp)
                    .Replace("\"Subject\": \"host\"", "\"Subject\": \"host) || eval(\\\"1\\\") || (host\"");
                System.IO.File.WriteAllText(tmp, json);

                Store loaded = RuleStore.Load(tmp);
                Check(RuleStore.Rollback(loaded, loaded.Versions[0].Id, "tester"), "gate: tampered version rolls back in memory");
                Report rr = Gate.Check(loaded.Current, out text);
                Check(Gate.IsBlocking(rr), "gate: tampered history version cannot be written");
                Check(!text.Contains("eval"), "gate: tampered subject never reaches the PAC text");
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { }
            }

            // Info findings (valid narrow-before-broad exceptions) do not block.
            Report info = new Report();
            info.Add(Finding.Make(Severity.Info, "EXCEPTION", "x", null, 0));
            Check(!Gate.IsBlocking(info), "gate: info-only report does not block");
        }

        static void ShadowingChecks()
        {
            // Broad domain before narrow => the narrow rule is unreachable (CRITICAL).
            string broadFirst =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".example.com\")) return \"PROXY p:8080\";\n" +
                "  if (dnsDomainIs(host, \".test.example.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report bf = Shadowing.Analyze(Imported(broadFirst));
            Check(Has(bf, "SHADOW"), "broad-before-narrow flagged as shadow");
            Check(bf.Count(Severity.Critical) >= 1, "shadow is critical");

            // Narrow exception before broad => valid (INFO, no critical).
            string narrowFirst =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".test.example.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".example.com\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Report nf = Shadowing.Analyze(Imported(narrowFirst));
            Check(Has(nf, "EXCEPTION"), "narrow-before-broad flagged as valid exception");
            Eq(0, nf.Count(Severity.Critical), "narrow-before-broad has no critical");

            // Exact duplicate, same action => redundant (WARNING).
            string dupSame =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Shadowing.Analyze(Imported(dupSame)), "DUP_REDUNDANT"), "redundant duplicate flagged");

            // Same condition, conflicting action => later rule never applies (CRITICAL).
            string dupConflict =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Shadowing.Analyze(Imported(dupConflict)), "DUP_CONFLICT"), "conflicting duplicate flagged");

            // Subnet containment: /8 before /16 inside it => shadow.
            string subnet =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isInNet(host, \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  if (isInNet(host, \"10.1.0.0\", \"255.255.0.0\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Shadowing.Analyze(Imported(subnet)), "SHADOW"), "broader subnet shadows narrower");

            // Composite OR rule shadows a leaf contained in one branch.
            string orShadow =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\") || dnsDomainIs(host, \".b.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".sub.a.com\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Check(Has(Shadowing.Analyze(Imported(orShadow)), "SHADOW"), "OR branch shadows contained leaf");

            // Unrelated domains => no findings (no false positives).
            string unrelated =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".b.com\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            Eq(0, Shadowing.Analyze(Imported(unrelated)).Findings.Count, "unrelated rules produce no findings");
        }

        static string SimPac()
        {
            return
                "function FindProxyForURL(url, host) {\n" +
                "  if (isPlainHostName(host)) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".example.com\")) return \"PROXY p1:8080; DIRECT\";\n" +
                "  if (shExpMatch(url, \"http://ads.*/*\")) return \"PROXY block:1\";\n" +
                "  if (isInNet(host, \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  return \"PROXY edge:8080; DIRECT\";\n" +
                "}\n";
        }

        static void SimulatorChecks()
        {
            RuleSet rs = Imported(SimPac());

            // Plain hostname -> first rule fires.
            SimResult a = Simulator.Run(rs, new SimInput("http://intranet/", "intranet"));
            Check(a.Matched != null && a.Matched.Order == 0, "plain host matches rule 0");
            Check(a.ActionString() == "DIRECT", "rule 0 returns DIRECT");

            // Domain match -> second rule, with the proxy chain.
            SimResult b = Simulator.Run(rs, new SimInput("http://www.example.com/", "www.example.com"));
            Check(b.Matched != null && b.Matched.Order == 1, "domain host matches rule 1");
            Check(b.ActionString().Contains("PROXY p1:8080"), "rule 1 returns proxy chain");

            // URL glob match -> third rule.
            SimResult c = Simulator.Run(rs, new SimInput("http://ads.tracker.com/x.gif", "ads.tracker.com"));
            Check(c.Matched != null && c.Matched.Order == 2, "ads URL matches glob rule 2");

            // IP literal in 10/8 -> isInNet rule fires.
            SimResult d = Simulator.Run(rs, new SimInput("http://10.5.5.5/", "10.5.5.5"));
            Check(d.Matched != null && d.Matched.Order == 3, "10/8 IP matches isInNet rule 3");

            // External host needing DNS -> isInNet indeterminate, falls to default, flagged.
            SimResult e = Simulator.Run(rs, new SimInput("http://other.org/", "other.org"));
            Check(e.UsedDefault, "unknown host falls through to default");
            Check(e.ActionString().Contains("PROXY edge:8080"), "default action used");
            Check(e.HasIndeterminateBeforeMatch, "isInNet without DNS flagged indeterminate");

            // Disabled rule is skipped and noted in the trace.
            rs.Rules[0].Enabled = false;
            SimResult f = Simulator.Run(rs, new SimInput("http://intranet/", "intranet"));
            Check(f.Trace.Count > 0 && f.Trace[0].Reason.Contains("disabled"), "disabled rule noted in trace");
            Check(f.Matched == null || f.Matched.Order != 0, "disabled rule 0 does not fire");
        }

        static void StorageChecks()
        {
            string pacA =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            string pacB =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".a.com\")) return \"DIRECT\";\n" +
                "  if (dnsDomainIs(host, \".b.com\")) return \"PROXY p:8080\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";

            Store store = new Store();
            store.Current = new RuleSet();

            RuleSet a = Imported(pacA);
            VersionEntry v1 = RuleStore.Commit(store, a, "admin", "initial");
            RuleSet b = Imported(pacB);
            RuleStore.Commit(store, b, "admin", "add b.com");

            Eq(2, store.Current.Rules.Count, "current is latest commit (2 rules)");
            Eq(2, store.Versions.Count, "two versions recorded");
            Eq(2, store.Audit.Count, "two audit entries after two commits");

            // Rollback to the first version restores the 1-rule state.
            Check(RuleStore.Rollback(store, v1.Id, "admin"), "rollback succeeds");
            Eq(1, store.Current.Rules.Count, "rollback restored 1-rule set");
            Eq(3, store.Audit.Count, "rollback appended audit entry");

            // Save + load round-trip preserves current, versions and audit.
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wpad-store-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try
            {
                RuleStore.Save(path, store);
                Store reloaded = RuleStore.Load(path);
                Eq(1, reloaded.Current.Rules.Count, "reloaded current rule count");
                Eq(2, reloaded.Versions.Count, "reloaded version count");
                Eq(3, reloaded.Audit.Count, "reloaded audit count");
                Eq("dnsDomainIs", reloaded.Current.Rules[0].Condition.Fn, "reloaded condition intact");
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
            }

            // Diff detects added / removed / changed rules (same Ids preserved via copy).
            RuleSet baseSet = Imported(pacB);
            RuleSet edited = Json.Deserialize<RuleSet>(Json.Stringify(baseSet, false));
            edited.Rules[0].Action = ActionText.Parse("PROXY changed:1"); // changed
            edited.Rules.RemoveAt(1);                                     // removed b.com
            Rule added = new Rule();
            added.Id = "newrule0001";
            List<string> dargs = new List<string>(); dargs.Add(".c.com");
            added.Condition = Condition.Single("dnsDomainIs", dargs, false);
            added.Condition.Subject = "host";
            added.Action = ActionText.Parse("DIRECT");
            edited.Rules.Add(added);

            DiffResult diff = RuleStore.Diff(baseSet, edited);
            Eq(1, diff.Added.Count, "diff: one added rule");
            Eq(1, diff.Removed.Count, "diff: one removed rule");
            Eq(1, diff.Changed.Count, "diff: one changed rule");
            Check(!diff.IsEmpty, "diff is non-empty");

            // Identical sets diff to nothing.
            DiffResult same = RuleStore.Diff(baseSet, Json.Deserialize<RuleSet>(Json.Stringify(baseSet, false)));
            Check(same.IsEmpty, "identical sets diff empty");
        }

        static void DuplicateAndDnsChecks()
        {
            // Existing broad domain ".example.com"; adding a narrower "test.example.com" must warn.
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".example.com\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            RuleSet rs = Imported(pac);

            List<string> da = new List<string>(); da.Add("test.example.com");
            Condition narrow = Condition.Single("dnsDomainIs", da, false); narrow.Subject = "host";
            List<string> w1 = Shadowing.CheckCandidate(rs, narrow);
            Check(w1.Count >= 1, "duplicate guard warns: test.example.com already covered by .example.com");

            List<string> db = new List<string>(); db.Add(".other.com");
            Condition unrelated = Condition.Single("dnsDomainIs", db, false); unrelated.Subject = "host";
            Check(Shadowing.CheckCandidate(rs, unrelated).Count == 0, "duplicate guard quiet for unrelated domain");

            // DNS name extraction strips the leading dot and dedupes.
            string pac2 =
                "function FindProxyForURL(url, host) {\n" +
                "  if (dnsDomainIs(host, \".example.com\") || dnsDomainIs(host, \"a.b.com\")) return \"DIRECT\";\n" +
                "  if (isInNet(myIpAddress(), \"10.0.0.0\", \"255.0.0.0\")) return \"DIRECT\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            List<string> names = DnsCheck.NamesFrom(Imported(pac2));
            Eq(2, names.Count, "DNS names: two domains (IP rule ignored)");
            Check(names.Contains("example.com"), "DNS names: leading dot stripped");
            Check(names.Contains("a.b.com"), "DNS names: plain domain kept");
        }

        static void LineMapChecks()
        {
            // A typo'd predicate ("dnsDomainls") parses structurally but is an unknown call,
            // so the security pass flags it by source line. The generator's line map must let
            // the UI translate that line back to the rule order (here: rule 1).
            string pac =
                "function FindProxyForURL(url, host) {\n" +
                "  if (isPlainHostName(host)) return \"DIRECT\";\n" +
                "  if (dnsDomainls(host, \".x.com\")) return \"PROXY p:1\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            RuleSet rs = Imported(pac);
            Eq(2, rs.Rules.Count, "line-map: both rules recognized");

            Dictionary<int, int> map;
            string src = PacGenerator.Generate(rs, out map);
            Report safety = Safety.Analyze(src);

            Finding hit = null;
            for (int i = 0; i < safety.Findings.Count; i++)
                if (safety.Findings[i].Code == "SEC_UNKNOWN_CALL") { hit = safety.Findings[i]; break; }
            Check(hit != null, "line-map: unknown-call warning surfaced");
            Check(hit != null && hit.Line > 0, "line-map: finding carries a source line");

            int order = -99;
            bool mapped = hit != null && map.TryGetValue(hit.Line, out order);
            Check(mapped, "line-map: finding's line is in the rule line map");
            Eq(1, order, "line-map: line resolves to rule order 1");
        }

        static void WorkspaceChecks()
        {
            // Each file's history sidecar sits next to it as "<file>.history.json".
            Eq("C:\\x\\wpad.dat.history.json", RuleStore.SidecarPath("C:\\x\\wpad.dat"),
                "sidecar path is <file>.history.json");

            // The workspace list (open files + active) round-trips through JSON.
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "wpad-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            try
            {
                WorkspaceState ws = new WorkspaceState();
                ws.OpenFiles.Add("C:\\a\\one.dat");
                ws.OpenFiles.Add("C:\\b\\two.pac");
                ws.ActiveFile = "C:\\b\\two.pac";
                RuleStore.SaveWorkspace(tmp, ws);

                WorkspaceState back = RuleStore.LoadWorkspace(tmp);
                Eq(2, back.OpenFiles.Count, "workspace: two open files round-trip");
                Check(back.OpenFiles.Contains("C:\\a\\one.dat"), "workspace: first file kept");
                Eq("C:\\b\\two.pac", back.ActiveFile, "workspace: active file kept");
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { }
            }

            // A missing workspace file loads as empty, not null.
            WorkspaceState none = RuleStore.LoadWorkspace(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-ws-" +
                    Guid.NewGuid().ToString("N") + ".json"));
            Check(none != null && none.OpenFiles.Count == 0, "workspace: missing file => empty list");
        }
    }
}
