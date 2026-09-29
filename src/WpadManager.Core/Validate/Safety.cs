using System;
using System.Collections.Generic;
using WpadManager.Core.Model;
using WpadManager.Core.Parser;

namespace WpadManager.Core.Validate
{
    // Import-time security gate. PAC files are JavaScript, so a "wild" import could
    // in principle smuggle in eval/XMLHttpRequest/ActiveX/network calls. We parse the
    // source to a real AST and flag anything outside the safe PAC subset:
    //   - calls to, or bare references of, dangerous globals (eval, Function, this, ...)
    //   - property access on host objects and prototype-chain escapes (.constructor, ...)
    //   - redefinition of PAC built-ins (dnsResolve = eval; function dnsDomainIs(){...})
    //   - JS keywords our subset parser does not model (while, new, try, ...): what we
    //     parsed would no longer be what the browser runs, so the file cannot be vetted
    //   - DNS lookups of computed names (dnsResolve(host + ".x.evil")) and names built
    //     from host/url — the classic way to leak every visited host to an outside DNS
    //   - calls to functions that are neither PAC helpers nor declared in this file
    // The analysis is only as good as the parser's agreement with real JavaScript; the
    // lexer therefore follows JS for line terminators and string escapes, and anything
    // it does not understand is a syntax error (fail closed).
    public static class Safety
    {
        private static readonly HashSet<string> DangerousGlobals = Set(
            "eval", "Function", "setTimeout", "setInterval", "setImmediate",
            "require", "import", "fetch", "XMLHttpRequest", "ActiveXObject",
            "importScripts", "WScript", "GetObject", "execScript", "Worker",
            "document", "window", "globalThis", "global", "process", "self",
            "navigator", "location", "localStorage", "WScriptShell", "this");

        // Reserved words the subset parser has no grammar for. It would misread them as
        // plain identifiers (e.g. `while (x) {}` as a call to "while"), so seeing one
        // means our AST no longer matches what the browser executes.
        private static readonly HashSet<string> UnsupportedKeywords = Set(
            "new", "delete", "typeof", "void", "in", "instanceof", "while", "for", "do",
            "switch", "case", "default", "break", "continue", "try", "catch", "finally",
            "throw", "with", "class", "const", "let", "yield", "await", "export",
            "debugger", "super", "extends", "function", "else", "arguments");

        // Properties that reach the Function constructor or rewrite the prototype chain.
        private static readonly HashSet<string> DangerousProps = Set(
            "constructor", "__proto__", "prototype", "__defineGetter__", "__defineSetter__",
            "__lookupGetter__", "__lookupSetter__", "caller", "callee");

        // PAC helpers that send their first argument to the DNS resolver.
        private static readonly HashSet<string> DnsLookups = Set(
            "dnsResolve", "dnsResolveEx", "isResolvable", "isResolvableEx", "isInNet", "isInNetEx");

        private static HashSet<string> Set(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        // PAC built-ins plus the entry points: redefining any of them silently changes what
        // every rule means (and what our simulator/shadowing analysis assumes).
        private static bool IsBuiltin(string name)
        {
            return PacFunctions.IsKnown(name) || name == "FindProxyForURL" || name == "FindProxyForURLEx";
        }

        // Analyze raw PAC text. On a hard parse error returns a single SYNTAX finding
        // (an unparseable file cannot be vetted and must not be trusted).
        public static Report Analyze(string source)
        {
            Report report = new Report();
            Program prog;
            try
            {
                prog = JsParser.ParseSource(source);
            }
            catch (ParseError pe)
            {
                Finding f = Finding.Make(Severity.Error, "SEC_SYNTAX",
                    "Source does not parse and cannot be security-vetted: " + pe.Message, null, -1);
                f.Line = pe.Line;
                report.Add(f);
                return report;
            }
            catch (LexError le)
            {
                Finding f = Finding.Make(Severity.Error, "SEC_SYNTAX",
                    "Source does not tokenize and cannot be security-vetted: " + le.Message, null, -1);
                f.Line = le.Line;
                report.Add(f);
                return report;
            }

            HashSet<string> declared = new HashSet<string>(StringComparer.Ordinal);
            CollectDeclaredFns(prog, declared);
            Walk(prog, null, declared, report);
            return report;
        }

        private static void CollectDeclaredFns(Node n, HashSet<string> declared)
        {
            if (n == null) return;
            FunctionDecl fd = n as FunctionDecl;
            if (fd != null && fd.Name != null) declared.Add(fd.Name);
            foreach (Node c in Children(n)) CollectDeclaredFns(c, declared);
        }

        private static void Walk(Node n, Node parent, HashSet<string> declared, Report report)
        {
            if (n == null) return;

            FunctionDecl fd = n as FunctionDecl;
            if (fd != null && fd.Name != null && fd.Name != "FindProxyForURL" && fd.Name != "FindProxyForURLEx" &&
                IsBuiltin(fd.Name))
                Add(report, Severity.Critical, "SEC_BUILTIN_OVERRIDE",
                    "Function '" + fd.Name + "' redefines a PAC built-in.", n.Line);

            VarStatement vs = n as VarStatement;
            if (vs != null)
                for (int i = 0; i < vs.Names.Count; i++)
                    if (IsBuiltin(vs.Names[i]) || DangerousGlobals.Contains(vs.Names[i]))
                        Add(report, Severity.Critical, "SEC_BUILTIN_OVERRIDE",
                            "'var " + vs.Names[i] + "' shadows a PAC built-in or global.", n.Line);

            Identifier id = n as Identifier;
            if (id != null) InspectIdentifier(id, parent, report);

            CallExpr call = n as CallExpr;
            if (call != null) InspectCall(call, declared, report);

            MemberExpr mem = n as MemberExpr;
            if (mem != null) InspectMember(mem, report);

            BinaryExpr bin = n as BinaryExpr;
            if (bin != null) InspectBinary(bin, parent, report);

            foreach (Node c in Children(n)) Walk(c, n, declared, report);
        }

        private static void InspectIdentifier(Identifier id, Node parent, Report report)
        {
            if (UnsupportedKeywords.Contains(id.Name))
            {
                Add(report, Severity.Error, "SEC_UNSUPPORTED",
                    "'" + id.Name + "' is not supported by the PAC subset parser; the file cannot be security-vetted.",
                    id.Line);
                return;
            }
            if (!DangerousGlobals.Contains(id.Name)) return;

            // Calls and member roots get their own, more specific finding.
            CallExpr pc = parent as CallExpr;
            if (pc != null && pc.Callee == id) return;
            MemberExpr pm = parent as MemberExpr;
            if (pm != null && pm.Obj == id) return;

            Add(report, Severity.Critical, "SEC_DANGEROUS_REF",
                "Reference to '" + id.Name + "' is not allowed in a PAC file.", id.Line);
        }

        private static void InspectCall(CallExpr call, HashSet<string> declared, Report report)
        {
            Identifier id = call.Callee as Identifier;
            if (id != null)
            {
                if (DangerousGlobals.Contains(id.Name))
                {
                    Add(report, Severity.Critical, "SEC_DANGEROUS_CALL",
                        "Call to '" + id.Name + "' is forbidden in a PAC file.", call.Line);
                }
                else if (UnsupportedKeywords.Contains(id.Name))
                {
                    // already reported as SEC_UNSUPPORTED by the identifier check
                }
                else if (!PacFunctions.IsKnown(id.Name) && !declared.Contains(id.Name))
                {
                    Add(report, Severity.Warning, "SEC_UNKNOWN_CALL",
                        "Call to '" + id.Name + "' is not a PAC helper or a function defined in this file.",
                        call.Line);
                }

                if (DnsLookups.Contains(id.Name) && call.Args.Count > 0 && !IsPlainDnsArgument(call.Args[0]))
                {
                    Add(report, Severity.Error, "SEC_DNS_COMPUTED",
                        "'" + id.Name + "' looks up a computed name; this can leak visited hosts to an outside DNS server.",
                        call.Line);
                }
                return;
            }

            MemberExpr me = call.Callee as MemberExpr;
            if (me != null)
            {
                string root = RootName(me);
                string label = root != null ? root : "<expr>";
                Severity sev = (root != null && DangerousGlobals.Contains(root))
                    ? Severity.Critical : Severity.Warning;
                Add(report, sev, "SEC_METHOD_CALL",
                    "Method call on '" + label + "." + me.Prop + "' is outside the PAC subset.", call.Line);
                return;
            }

            // f()() and similar: the function being called is itself computed.
            Add(report, Severity.Warning, "SEC_INDIRECT_CALL",
                "Call of a computed function value is outside the PAC subset.", call.Line);
        }

        // What a DNS helper may look up: a variable (normally host), a literal, or the result
        // of another call (dnsResolve(host), myIpAddress()) whose own arguments are checked.
        private static bool IsPlainDnsArgument(Node a)
        {
            if (a is Identifier || a is StringLit || a is NumberLit) return true;
            CallExpr c = a as CallExpr;
            return c != null && c.Callee is Identifier;
        }

        private static void InspectMember(MemberExpr mem, Report report)
        {
            string root = RootName(mem);
            if (root != null && DangerousGlobals.Contains(root))
                Add(report, Severity.Critical, "SEC_HOST_ACCESS",
                    "Access to host object '" + root + "' is not allowed in a PAC file.", mem.Line);

            string prop = mem.Prop;
            if (mem.Index != null)
            {
                StringLit lit = mem.Index as StringLit;
                if (lit != null) prop = lit.Value;
                else if (!(mem.Index is NumberLit))
                {
                    Add(report, Severity.Warning, "SEC_COMPUTED_MEMBER",
                        "Computed property access [...] cannot be vetted.", mem.Line);
                    return;
                }
            }
            if (prop != null && DangerousProps.Contains(prop))
                Add(report, Severity.Critical, "SEC_PROTO_ACCESS",
                    "Access to '" + prop + "' can reach the Function constructor and is not allowed.", mem.Line);
        }

        private static void InspectBinary(BinaryExpr bin, Node parent, Report report)
        {
            bool assign = bin.Op == "=" || bin.Op == "+=" || bin.Op == "-=" || bin.Op == "*=" || bin.Op == "/=";
            Identifier target = bin.Left as Identifier;
            if (assign && target != null && (IsBuiltin(target.Name) || DangerousGlobals.Contains(target.Name)))
                Add(report, Severity.Critical, "SEC_BUILTIN_OVERRIDE",
                    "Assignment to '" + target.Name + "' replaces a PAC built-in or global.", bin.Line);

            // Building a new name from host/url is how a PAC exfiltrates browsing data
            // (to DNS or anywhere else). Report once, at the outermost '+' of a chain.
            if (bin.Op == "+" || bin.Op == "+=")
            {
                BinaryExpr pb = parent as BinaryExpr;
                bool nested = pb != null && (pb.Op == "+" || pb.Op == "+=");
                if (!nested && ConcatUsesHostOrUrl(bin))
                    Add(report, Severity.Warning, "SEC_NAME_BUILD",
                        "A new string is built from host/url; check it cannot leak browsing data.", bin.Line);
            }
        }

        private static bool ConcatUsesHostOrUrl(Node n)
        {
            Identifier id = n as Identifier;
            if (id != null) return id.Name == "host" || id.Name == "url";
            BinaryExpr b = n as BinaryExpr;
            if (b != null && (b.Op == "+" || b.Op == "+="))
                return ConcatUsesHostOrUrl(b.Left) || ConcatUsesHostOrUrl(b.Right);
            return false;
        }

        // Walk a MemberExpr/CallExpr chain down to the base identifier name.
        private static string RootName(Node n)
        {
            while (true)
            {
                Identifier id = n as Identifier;
                if (id != null) return id.Name;
                MemberExpr m = n as MemberExpr;
                if (m != null) { n = m.Obj; continue; }
                CallExpr c = n as CallExpr;
                if (c != null) { n = c.Callee; continue; }
                return null;
            }
        }

        // Enumerate the direct child nodes of any AST node (structural, type-driven).
        private static IEnumerable<Node> Children(Node n)
        {
            List<Node> list = new List<Node>();

            Program prog = n as Program;
            if (prog != null) { list.AddRange(prog.Body); return list; }

            FunctionDecl fd = n as FunctionDecl;
            if (fd != null) { if (fd.Body != null) list.Add(fd.Body); return list; }

            Block b = n as Block;
            if (b != null) { list.AddRange(b.Body); return list; }

            IfStatement ifs = n as IfStatement;
            if (ifs != null)
            {
                if (ifs.Test != null) list.Add(ifs.Test);
                if (ifs.Then != null) list.Add(ifs.Then);
                if (ifs.Else != null) list.Add(ifs.Else);
                return list;
            }

            ReturnStatement ret = n as ReturnStatement;
            if (ret != null) { if (ret.Argument != null) list.Add(ret.Argument); return list; }

            VarStatement vs = n as VarStatement;
            if (vs != null)
            {
                if (vs.Inits != null)
                    for (int i = 0; i < vs.Inits.Count; i++) if (vs.Inits[i] != null) list.Add(vs.Inits[i]);
                return list;
            }

            ExpressionStatement es = n as ExpressionStatement;
            if (es != null) { if (es.Expr != null) list.Add(es.Expr); return list; }

            CallExpr call = n as CallExpr;
            if (call != null)
            {
                if (call.Callee != null) list.Add(call.Callee);
                if (call.Args != null) list.AddRange(call.Args);
                return list;
            }

            MemberExpr me = n as MemberExpr;
            if (me != null)
            {
                if (me.Obj != null) list.Add(me.Obj);
                if (me.Index != null) list.Add(me.Index);
                return list;
            }

            LogicalExpr lg = n as LogicalExpr;
            if (lg != null) { if (lg.Left != null) list.Add(lg.Left); if (lg.Right != null) list.Add(lg.Right); return list; }

            BinaryExpr bin = n as BinaryExpr;
            if (bin != null) { if (bin.Left != null) list.Add(bin.Left); if (bin.Right != null) list.Add(bin.Right); return list; }

            UnaryExpr un = n as UnaryExpr;
            if (un != null) { if (un.Arg != null) list.Add(un.Arg); return list; }

            ConditionalExpr ce = n as ConditionalExpr;
            if (ce != null)
            {
                if (ce.Test != null) list.Add(ce.Test);
                if (ce.Consequent != null) list.Add(ce.Consequent);
                if (ce.Alternate != null) list.Add(ce.Alternate);
                return list;
            }

            return list; // Identifier / StringLit / NumberLit have no child nodes
        }

        private static void Add(Report report, Severity sev, string code, string msg, int line)
        {
            Finding f = Finding.Make(sev, code, msg, null, -1);
            f.Line = line;
            report.Add(f);
        }
    }
}
