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

            // Comments this tool writes itself (regenerated on every save) are not user text.
            List<Comment> comments = new List<Comment>();
            for (int i = 0; i < _prog.Comments.Count; i++)
                if (!IsGeneratorMarker(_prog.Comments[i])) comments.Add(_prog.Comments[i]);

            // Top-level comments outside every node (file header, notes between helpers).
            List<string> header = new List<string>();
            for (int i = 0; i < comments.Count; i++)
                if (!InsideAny(comments[i], _prog.Body)) header.Add(comments[i].Text);
            rs.HeaderComment = Join(header);

            // The function body in source order: statements, plus "[disabled]" lines this
            // tool wrote for disabled rules (they come back as disabled rules).
            List<BodyItem> items = new List<BodyItem>();
            for (int i = 0; i < fn.Body.Body.Count; i++)
            {
                Node s = fn.Body.Body[i];
                items.Add(new BodyItem(s, null, s.Start, s.End, s.EndLine));
            }
            List<Comment> bodyComments = new List<Comment>();
            for (int i = 0; i < comments.Count; i++)
            {
                Comment c = comments[i];
                if (c.Start <= fn.Body.Start || c.End > fn.Body.End) continue;
                Rule disabled = !InsideAny(c, fn.Body.Body) ? ParseDisabledRule(c) : null;
                if (disabled != null) items.Add(new BodyItem(null, disabled, c.Start, c.End, c.Line));
                else bodyComments.Add(c);
            }
            items.Sort(delegate(BodyItem a, BodyItem b) { return a.Start.CompareTo(b.Start); });

            List<string>[] owned = new List<string>[items.Count];
            List<string> tail = AssignComments(bodyComments, items, owned);

            int order = 0;
            bool sawDefault = false;
            List<string> defaultComment = new List<string>();
            List<UnparsedBlock> waiting = new List<UnparsedBlock>();   // in-body blocks awaiting the next rule
            for (int k = 0; k < items.Count; k++)
            {
                BodyItem item = items[k];
                string comment = Join(owned[k]);

                if (item.Disabled != null)
                {
                    if (sawDefault)
                    {
                        // inert text after the final return: keep it as a comment
                        if (comment != null) defaultComment.Add(comment);
                        continue;
                    }
                    AddRule(rs, item.Disabled, comment, ref order, waiting);
                    continue;
                }

                Node stmt = item.Stmt;
                if (sawDefault)
                {
                    UnparsedBlock after = AddUnparsed(rs, stmt, "Unreachable: appears after an unconditional return");
                    after.Comment = comment;
                    after.AfterDefault = true;
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
                            Rule r = new Rule();
                            r.Condition = MapCondition(ifs.Test);
                            r.Action = ActionText.Parse(ret);
                            r.Enabled = true;
                            AddRule(rs, r, comment, ref order, waiting);
                            continue;
                        }
                        catch (RecognizeException rx)
                        {
                            AddInBody(rs, stmt, "Condition not recognized: " + rx.Message, comment, waiting);
                            continue;
                        }
                    }
                    AddInBody(rs, stmt, "if-branch is not a simple string return", comment, waiting);
                    continue;
                }

                ReturnStatement rsx = stmt as ReturnStatement;
                if (rsx != null)
                {
                    StringLit lit = rsx.Argument as StringLit;
                    if (lit != null)
                    {
                        rs.DefaultAction = ActionText.Parse(lit.Value);
                        if (comment != null) defaultComment.Insert(0, comment);
                        sawDefault = true;
                        continue;
                    }
                    AddInBody(rs, stmt, "Default return is not a plain string", comment, waiting);
                    continue;
                }

                AddInBody(rs, stmt, "Statement not part of the simple if/return shape", comment, waiting);
            }
            defaultComment.AddRange(tail);
            rs.DefaultComment = Join(defaultComment);
            return rs;
        }

        // One element of FindProxyForURL's body: a statement, or a disabled rule read back
        // from its "// [disabled] ..." line.
        private sealed class BodyItem
        {
            public readonly Node Stmt;
            public readonly Rule Disabled;
            public readonly int Start, End, EndLine;

            public BodyItem(Node stmt, Rule disabled, int start, int end, int endLine)
            {
                Stmt = stmt; Disabled = disabled; Start = start; End = end; EndLine = endLine;
            }
        }

        // Give every comment to the item it documents: the item it sits inside (e.g. notes
        // on the lines of a multi-line condition), else the item it trails on the same line,
        // else the next item. Comments after the last item are returned (they go with the
        // default return). Items and comments are both in source order: one forward sweep.
        private static List<string> AssignComments(List<Comment> comments, List<BodyItem> items, List<string>[] owned)
        {
            for (int k = 0; k < owned.Length; k++) owned[k] = new List<string>();
            List<string> tail = new List<string>();
            int p = 0;
            for (int i = 0; i < comments.Count; i++)
            {
                Comment c = comments[i];
                while (p < items.Count && items[p].End <= c.Start) p++;
                int owner;
                if (p < items.Count && items[p].Start <= c.Start) owner = p;                 // inside
                else if (p > 0 && items[p - 1].EndLine == c.Line) owner = p - 1;             // trailing
                else owner = p < items.Count ? p : -1;                                       // leading
                if (owner >= 0) owned[owner].Add(c.Text);
                else tail.Add(c.Text);
            }
            return tail;
        }

        private void AddRule(RuleSet rs, Rule r, string comment, ref int order, List<UnparsedBlock> waiting)
        {
            r.Id = NewId();
            r.Order = order++;
            r.Comment = comment;
            r.Author = "import";
            r.CreatedAt = rs.CreatedAt;
            r.UpdatedAt = rs.CreatedAt;
            rs.Rules.Add(r);
            // Code that sat just before this rule stays just before it.
            for (int i = 0; i < waiting.Count; i++) waiting[i].BeforeRuleId = r.Id;
            waiting.Clear();
        }

        private void AddInBody(RuleSet rs, Node stmt, string reason, string comment, List<UnparsedBlock> waiting)
        {
            UnparsedBlock ub = AddUnparsed(rs, stmt, reason);
            ub.Comment = comment;
            waiting.Add(ub);
        }

        // "[disabled] if (cond) return "action";" -> a disabled rule, or null when the text
        // is not such a line (then it stays an ordinary comment).
        private static Rule ParseDisabledRule(Comment c)
        {
            if (c.Block || !c.Text.StartsWith("[disabled]", StringComparison.Ordinal)) return null;
            string code = c.Text.Substring("[disabled]".Length).Trim();
            try
            {
                Program p = JsParser.ParseSource(code);
                if (p.Body.Count != 1 || p.Comments.Count != 0) return null;
                IfStatement ifs = p.Body[0] as IfStatement;
                if (ifs == null || ifs.Else != null) return null;
                Recognizer sub = new Recognizer(code, p);
                string ret = sub.SimpleReturnString(ifs.Then);
                if (ret == null) return null;
                Rule r = new Rule();
                r.Condition = sub.MapCondition(ifs.Test);
                r.Action = ActionText.Parse(ret);
                r.Enabled = false;
                return r;
            }
            catch (ParseError) { return null; }
            catch (LexError) { return null; }
            catch (RecognizeException) { return null; }
        }

        // Lines PacGenerator writes on every save; reading them back as user comments would
        // duplicate them on each round trip.
        private static bool IsGeneratorMarker(Comment c)
        {
            string t = c.Text;
            if (c.Block) return t.StartsWith("unparsed (", StringComparison.Ordinal);
            return t.StartsWith("PAC file generated by WPAD File Manager", StringComparison.Ordinal) ||
                   t.StartsWith("RuleSet: ", StringComparison.Ordinal) ||
                   t.StartsWith("Generated: ", StringComparison.Ordinal) ||
                   t.StartsWith("[preserved from import]", StringComparison.Ordinal);
        }

        private static bool InsideAny(Comment c, List<Node> nodes)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (c.Start >= nodes[i].Start && c.End <= nodes[i].End) return true;
            return false;
        }

        private static string Join(List<string> parts)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < parts.Count; i++)
                if (!string.IsNullOrEmpty(parts[i])) lines.Add(parts[i]);
            return lines.Count > 0 ? string.Join("\n", lines.ToArray()) : null;
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

        // "a || b || c ..." parses as a left-deep tree as deep as the list is long (a real
        // corporate PAC can list tens of thousands of domains), so flatten it with an explicit
        // stack rather than recursion, which would overflow the stack.
        private void FlattenLogical(LogicalExpr lg, CompositeOp op, List<Condition> outList)
        {
            string want = op == CompositeOp.And ? "&&" : "||";
            Stack<Node> pending = new Stack<Node>();
            pending.Push(lg);
            while (pending.Count > 0)
            {
                Node side = pending.Pop();
                LogicalExpr inner = side as LogicalExpr;
                if (inner != null && inner.Op == want)
                {
                    pending.Push(inner.Right);   // left is handled first, keeping source order
                    pending.Push(inner.Left);
                }
                else outList.Add(MapCondition(side));
            }
        }

        private UnparsedBlock AddUnparsed(RuleSet rs, Node n, string reason)
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
            return ub;
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

