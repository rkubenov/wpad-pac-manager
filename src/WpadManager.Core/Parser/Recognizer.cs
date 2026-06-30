using System;
using System.Collections.Generic;
using WpadManager.Core.Model;

namespace WpadManager.Core.Parser
{
    public class PacImportResult
    {
        public RuleSet RuleSet;
        public bool Ok;                  // false only on a hard syntax/lex error
        public string SyntaxError;
        public int SyntaxErrorLine;
        public List<string> Notes = new List<string>();
    }

    // Thrown internally when a construct cannot be mapped to a structured Condition,
    // so the whole statement is preserved verbatim as an UnparsedBlock instead.
    internal class RecognizeException : Exception
    {
        public RecognizeException(string msg) : base(msg) { }
    }

    // Facade: parse "wild" PAC text into our structured model, best-effort,
    // never losing data (unrecognized pieces become UnparsedBlocks).
    public static class PacImporter
    {
        public static PacImportResult Import(string source)
        {
            PacImportResult res = new PacImportResult();
            Program prog;
            try
            {
                prog = JsParser.ParseSource(source);
            }
            catch (ParseError pe)
            {
                res.Ok = false; res.SyntaxError = pe.Message; res.SyntaxErrorLine = pe.Line; return res;
            }
            catch (LexError le)
            {
                res.Ok = false; res.SyntaxError = le.Message; res.SyntaxErrorLine = le.Line; return res;
            }

            Recognizer rec = new Recognizer(source, prog);
            res.RuleSet = rec.Build(res.Notes);
            res.Ok = true;
            return res;
        }
    }

    public class Recognizer
    {
        private readonly string _src;
        private readonly Program _prog;

        public Recognizer(string source, Program prog) { _src = source; _prog = prog; }

        public RuleSet Build(List<string> notes)
        {
            RuleSet rs = new RuleSet();
            rs.Id = NewId();
            rs.Name = "Imported PAC";
            rs.CreatedAt = Iso.Now();
            rs.UpdatedAt = rs.CreatedAt;

            FunctionDecl fn = null;
            for (int i = 0; i < _prog.Body.Count; i++)
            {
                Node n = _prog.Body[i];
                FunctionDecl f = n as FunctionDecl;
                if (f != null && f.Name == "FindProxyForURL") { fn = f; continue; }
                // Preserve helper functions / stray top-level code verbatim.
                AddUnparsed(rs, n, "Top-level code outside FindProxyForURL");
            }

            if (fn == null)
            {
                notes.Add("FindProxyForURL(url, host) not found; whole input kept as raw block.");
                UnparsedBlock ub = new UnparsedBlock();
                ub.Id = NewId();
                ub.RawText = _src;
                ub.SourceLineStart = 1;
                ub.SourceLineEnd = CountLines(_src);
                ub.Reason = "No FindProxyForURL function";
                rs.Unparsed.Add(ub);
                return rs;
            }

            int order = 0;
            bool sawDefault = false;
            List<Node> body = fn.Body.Body;
            for (int i = 0; i < body.Count; i++)
            {
                Node stmt = body[i];

                if (sawDefault)
                {
                    AddUnparsed(rs, stmt, "Unreachable: appears after an unconditional return");
                    continue;
                }

                IfStatement ifs = stmt as IfStatement;
                if (ifs != null && ifs.Else == null)
                {
                    string ret = SimpleReturnString(ifs.Then);
                    if (ret != null)
                    {
                        try
                        {
                            Condition cond = MapCondition(ifs.Test);
                            Rule r = new Rule();
                            r.Id = NewId();
                            r.Order = order++;
                            r.Enabled = true;
                            r.Condition = cond;
                            r.Action = ActionText.Parse(ret);
                            r.Comment = LeadingComment(ifs.Line);
                            r.Author = "import";
                            r.CreatedAt = rs.CreatedAt;
                            r.UpdatedAt = rs.CreatedAt;
                            rs.Rules.Add(r);
                            continue;
                        }
                        catch (RecognizeException rx)
                        {
                            AddUnparsed(rs, stmt, "Condition not recognized: " + rx.Message);
                            continue;
                        }
                    }
                    AddUnparsed(rs, stmt, "if-branch is not a simple string return");
                    continue;
                }

                ReturnStatement rsx = stmt as ReturnStatement;
                if (rsx != null)
                {
                    StringLit lit = rsx.Argument as StringLit;
                    if (lit != null)
                    {
                        rs.DefaultAction = ActionText.Parse(lit.Value);
                        sawDefault = true;
                        continue;
                    }
                    AddUnparsed(rs, stmt, "Default return is not a plain string");
                    continue;
                }

                AddUnparsed(rs, stmt, "Statement not part of the simple if/return shape");
            }

            return rs;
        }

        // then-branch is a return of a string literal, possibly wrapped in a 1-statement block.
        private string SimpleReturnString(Node then)
        {
            ReturnStatement r = then as ReturnStatement;
            if (r == null)
            {
                Block b = then as Block;
                if (b != null && b.Body.Count == 1) r = b.Body[0] as ReturnStatement;
            }
            if (r == null) return null;
            StringLit s = r.Argument as StringLit;
            return s != null ? s.Value : null;
        }

        private Condition MapCondition(Node expr)
        {
            UnaryExpr u = expr as UnaryExpr;
            if (u != null && u.Op == "!")
            {
                Condition inner = MapCondition(u.Arg);
                inner.Negate = !inner.Negate;
                return inner;
            }

            LogicalExpr lg = expr as LogicalExpr;
            if (lg != null)
            {
                CompositeOp op = lg.Op == "&&" ? CompositeOp.And : CompositeOp.Or;
                List<Condition> kids = new List<Condition>();
                FlattenLogical(lg, op, kids);
                return Condition.Composite(op, kids, false);
            }

            CallExpr call = expr as CallExpr;
            if (call != null)
            {
                Identifier id = call.Callee as Identifier;
                if (id == null)
                    throw new RecognizeException("call on non-identifier callee");
                List<string> args = new List<string>();
                string subject = null;
                for (int i = 0; i < call.Args.Count; i++)
                {
                    Node a = call.Args[i];
                    StringLit s = a as StringLit;
                    if (s != null) { args.Add(s.Value); continue; }
                    NumberLit num = a as NumberLit;
                    if (num != null) { args.Add(num.Raw); continue; }
                    Identifier ai = a as Identifier;
                    if (ai != null) { if (subject == null) subject = ai.Name; continue; } // host/url placeholder
                    CallExpr ca = a as CallExpr;
                    if (ca != null)
                    {
                        // A nested call used as the subject, e.g. isInNet(myIpAddress(), ...)
                        // or dnsDomainIs(dnsResolve(host), ...). Keep its source text verbatim
                        // so it both validates and round-trips back out unchanged.
                        if (subject == null) subject = Slice(ca);
                        continue;
                    }
                    MemberExpr me = a as MemberExpr;
                    if (me != null) { if (subject == null) subject = Slice(me); continue; }
                    throw new RecognizeException("unsupported argument expression");
                }
                Condition single = Condition.Single(id.Name, args, false);
                single.Subject = subject;
                return single;
            }

            throw new RecognizeException(expr.GetType().Name + " is not a PAC predicate");
        }

        private void FlattenLogical(LogicalExpr lg, CompositeOp op, List<Condition> outList)
        {
            string want = op == CompositeOp.And ? "&&" : "||";
            AddSide(lg.Left, want, op, outList);
            AddSide(lg.Right, want, op, outList);
        }

        private void AddSide(Node side, string want, CompositeOp op, List<Condition> outList)
        {
            LogicalExpr inner = side as LogicalExpr;
            if (inner != null && inner.Op == want)
                FlattenLogical(inner, op, outList);
            else
                outList.Add(MapCondition(side));
        }

        private string LeadingComment(int stmtLine)
        {
            string best = null;
            int bestLine = -1;
            for (int i = 0; i < _prog.Comments.Count; i++)
            {
                Comment c = _prog.Comments[i];
                if (c.Line == stmtLine - 1 && c.Line > bestLine)
                {
                    best = c.Text;
                    bestLine = c.Line;
                }
            }
            return best;
        }

        private void AddUnparsed(RuleSet rs, Node n, string reason)
        {
            UnparsedBlock ub = new UnparsedBlock();
            ub.Id = NewId();
            int start = n.Start;
            int len = n.End - n.Start;
            if (start < 0) start = 0;
            if (start + len > _src.Length) len = _src.Length - start;
            ub.RawText = len > 0 ? _src.Substring(start, len) : "";
            ub.SourceLineStart = n.Line;
            ub.SourceLineEnd = n.EndLine;
            ub.Reason = reason;
            rs.Unparsed.Add(ub);
        }

        // Extract the verbatim source text spanned by a node (used to preserve
        // nested-call subjects like myIpAddress() exactly as written).
        private string Slice(Node n)
        {
            int start = n.Start;
            int len = n.End - n.Start;
            if (start < 0) start = 0;
            if (start > _src.Length) return "";
            if (start + len > _src.Length) len = _src.Length - start;
            return len > 0 ? _src.Substring(start, len) : "";
        }

        private static int CountLines(string s)
        {
            int n = 1;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') n++;
            return n;
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }
    }
}

