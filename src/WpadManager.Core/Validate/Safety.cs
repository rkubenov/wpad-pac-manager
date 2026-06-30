using System;
using System.Collections.Generic;
using WpadManager.Core.Model;
using WpadManager.Core.Parser;

namespace WpadManager.Core.Validate
{
    // Import-time security gate. PAC files are JavaScript, so a "wild" import could
    // in principle smuggle in eval/XMLHttpRequest/ActiveX/network calls. We parse the
    // source to a real AST and flag anything outside the safe PAC subset:
    //   - calls to dangerous globals (eval, Function, XMLHttpRequest, ActiveXObject, ...)
    //   - method calls / property access on host objects (document, window, process, ...)
    //   - calls to functions that are neither PAC helpers nor declared in this file
    // This is advisory for the operator; the interpreter is whitelist-limited anyway,
    // so unknown constructs are safe-by-construction, but we surface them explicitly.
    public static class Safety
    {
        private static readonly HashSet<string> DangerousGlobals = BuildDangerous();

        private static HashSet<string> BuildDangerous()
        {
            HashSet<string> s = new HashSet<string>(StringComparer.Ordinal);
            string[] names = new string[]
            {
                "eval", "Function", "setTimeout", "setInterval", "setImmediate",
                "require", "import", "fetch", "XMLHttpRequest", "ActiveXObject",
                "importScripts", "WScript", "GetObject", "execScript", "Worker",
                "document", "window", "globalThis", "global", "process", "self",
                "navigator", "location", "localStorage", "WScriptShell"
            };
            for (int i = 0; i < names.Length; i++) s.Add(names[i]);
            return s;
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
            Walk(prog, declared, report);
            return report;
        }

        private static void CollectDeclaredFns(Node n, HashSet<string> declared)
        {
            if (n == null) return;
            FunctionDecl fd = n as FunctionDecl;
            if (fd != null && fd.Name != null) declared.Add(fd.Name);
            foreach (Node c in Children(n)) CollectDeclaredFns(c, declared);
        }

        private static void Walk(Node n, HashSet<string> declared, Report report)
        {
            if (n == null) return;

            CallExpr call = n as CallExpr;
            if (call != null) InspectCall(call, declared, report);

            MemberExpr mem = n as MemberExpr;
            if (mem != null)
            {
                string root = RootName(mem);
                if (root != null && DangerousGlobals.Contains(root))
                    Add(report, Severity.Critical, "SEC_HOST_ACCESS",
                        "Access to host object '" + root + "' is not allowed in a PAC file.", n.Line);
            }

            foreach (Node c in Children(n)) Walk(c, declared, report);
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
                else if (!PacFunctions.IsKnown(id.Name) && !declared.Contains(id.Name))
                {
                    Add(report, Severity.Warning, "SEC_UNKNOWN_CALL",
                        "Call to '" + id.Name + "' is not a PAC helper or a function defined in this file.",
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
            }
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
            if (vs != null) { if (vs.Inits != null) list.AddRange(vs.Inits); return list; }

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
            if (me != null) { if (me.Obj != null) list.Add(me.Obj); return list; }

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
