using System;
using System.Collections.Generic;

namespace WpadManager.Core.Parser
{
    public class ParseError : Exception
    {
        public int Offset;
        public int Line;
        public ParseError(string msg, int offset, int line) : base(msg) { Offset = offset; Line = line; }
    }

    // Recursive-descent parser for the PAC JavaScript subset, with standard
    // operator-precedence climbing for expressions. Produces an AST (see Ast.cs).
    public class JsParser
    {
        private readonly List<Token> _t;
        private int _p;

        // Syntactic nesting (blocks, parentheses, unary operators, call arguments) deeper
        // than this is a parse error. Real PAC files nest a handful of levels; without a cap
        // a crafted file would overflow the stack, which .NET cannot catch. Long flat lists
        // such as "a || b || c ..." are parsed iteratively and do not count.
        private const int MaxDepth = 200;
        private int _depth;

        public JsParser(List<Token> tokens) { _t = tokens; _p = 0; }

        private void Enter()
        {
            if (++_depth > MaxDepth)
                throw new ParseError("Nesting is too deep (more than " + MaxDepth + " levels)", Cur.Start, Cur.Line);
        }

        private void Leave() { _depth--; }

        public static Program ParseSource(string source)
        {
            Lexer lex = new Lexer(source);
            List<Token> toks = lex.Tokenize();
            JsParser parser = new JsParser(toks);
            Program prog = parser.ParseProgram();
            prog.Comments = lex.Comments;
            return prog;
        }

        // True when `text` is exactly one operand — an identifier (host), a call
        // (myIpAddress(), dnsResolve(host)) or a member access — and nothing else: no second
        // statement, no operators, no comments. Used to vet a condition's Subject before it
        // is written back out as code, since a stored model can be hand-edited.
        public static bool IsSimpleOperand(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            Program p;
            try { p = ParseSource(text); }
            catch (ParseError) { return false; }
            catch (LexError) { return false; }
            if (p.Body.Count != 1 || p.Comments.Count != 0) return false;
            ExpressionStatement es = p.Body[0] as ExpressionStatement;
            if (es == null || es.End != es.Expr.End) return false;   // no trailing ';'
            return es.Expr is Identifier || es.Expr is CallExpr || es.Expr is MemberExpr;
        }

        private Token Cur { get { return _t[_p]; } }
        private bool AtEof { get { return Cur.Kind == TokKind.Eof; } }

        private bool IsPunc(string s) { return Cur.Kind == TokKind.Punc && Cur.Text == s; }
        private bool IsIdent(string s) { return Cur.Kind == TokKind.Ident && Cur.Text == s; }

        private Token Next() { Token t = _t[_p]; if (_p < _t.Count - 1) _p++; return t; }

        private Token Expect(string s)
        {
            if (Cur.Text != s || (Cur.Kind != TokKind.Punc && Cur.Kind != TokKind.Ident))
                throw new ParseError("Expected '" + s + "' but found '" + Cur.Text + "'", Cur.Start, Cur.Line);
            return Next();
        }

        private Token PrevToken() { return _t[_p > 0 ? _p - 1 : 0]; }

        // ---------- Statements ----------

        public Program ParseProgram()
        {
            Program prog = new Program();
            prog.Start = Cur.Start;
            prog.Line = Cur.Line;
            while (!AtEof)
            {
                Node n = ParseStatement();
                if (n != null) prog.Body.Add(n);
            }
            prog.End = Cur.End;
            prog.EndLine = Cur.Line;
            return prog;
        }

        private Node ParseStatement()
        {
            Enter();
            Node n = ParseStatementInner();
            Leave();
            return n;
        }

        private Node ParseStatementInner()
        {
            if (IsPunc(";")) { Next(); return null; }
            if (IsPunc("{")) return ParseBlock();
            if (IsIdent("function")) return ParseFunctionDecl();
            if (IsIdent("if")) return ParseIf();
            if (IsIdent("return")) return ParseReturn();
            if (IsIdent("var")) return ParseVar();
            return ParseExpressionStatement();
        }

        private FunctionDecl ParseFunctionDecl()
        {
            Token kw = Next(); // function
            FunctionDecl fn = new FunctionDecl();
            fn.Start = kw.Start; fn.Line = kw.Line;
            if (Cur.Kind == TokKind.Ident) fn.Name = Next().Text;
            Expect("(");
            while (!IsPunc(")") && !AtEof)
            {
                if (Cur.Kind == TokKind.Ident) fn.Params.Add(Next().Text);
                if (IsPunc(",")) Next();
                else break;
            }
            Expect(")");
            fn.Body = ParseBlock();
            fn.End = fn.Body.End; fn.EndLine = fn.Body.EndLine;
            return fn;
        }

        private Block ParseBlock()
        {
            Token open = Expect("{");
            Block b = new Block();
            b.Start = open.Start; b.Line = open.Line;
            while (!IsPunc("}") && !AtEof)
            {
                Node n = ParseStatement();
                if (n != null) b.Body.Add(n);
            }
            Token close = Expect("}");
            b.End = close.End; b.EndLine = close.Line;
            return b;
        }

        private IfStatement ParseIf()
        {
            Token kw = Next(); // if
            IfStatement s = new IfStatement();
            s.Start = kw.Start; s.Line = kw.Line;
            Expect("(");
            s.Test = ParseExpression();
            Expect(")");
            s.Then = ParseStatement();
            Node endNode = s.Then;
            if (IsIdent("else"))
            {
                Next();
                s.Else = ParseStatement();
                endNode = s.Else;
            }
            if (endNode != null) { s.End = endNode.End; s.EndLine = endNode.EndLine; }
            else { s.End = PrevToken().End; s.EndLine = PrevToken().Line; }
            return s;
        }

        private ReturnStatement ParseReturn()
        {
            Token kw = Next(); // return
            ReturnStatement s = new ReturnStatement();
            s.Start = kw.Start; s.Line = kw.Line;
            if (!IsPunc(";") && !IsPunc("}") && !AtEof)
                s.Argument = ParseExpression();
            s.End = PrevToken().End; s.EndLine = PrevToken().Line;
            if (IsPunc(";")) { Token sc = Next(); s.End = sc.End; s.EndLine = sc.Line; }
            return s;
        }

        private VarStatement ParseVar()
        {
            Token kw = Next(); // var
            VarStatement s = new VarStatement();
            s.Start = kw.Start; s.Line = kw.Line;
            while (true)
            {
                if (Cur.Kind != TokKind.Ident) break;
                s.Names.Add(Next().Text);
                if (IsPunc("="))
                {
                    Next();
                    s.Inits.Add(ParseExpression());
                }
                else s.Inits.Add(null);
                if (IsPunc(",")) { Next(); continue; }
                break;
            }
            s.End = PrevToken().End; s.EndLine = PrevToken().Line;
            if (IsPunc(";")) { Token sc = Next(); s.End = sc.End; s.EndLine = sc.Line; }
            return s;
        }

        private ExpressionStatement ParseExpressionStatement()
        {
            ExpressionStatement s = new ExpressionStatement();
            s.Start = Cur.Start; s.Line = Cur.Line;
            s.Expr = ParseExpression();
            s.End = PrevToken().End; s.EndLine = PrevToken().Line;
            if (IsPunc(";")) { Token sc = Next(); s.End = sc.End; s.EndLine = sc.Line; }
            return s;
        }

        // ---------- Expressions (precedence climbing) ----------

        private Node ParseExpression() { return ParseAssignment(); }

        private Node ParseAssignment()
        {
            Enter();
            Node left = ParseConditional();
            if (IsPunc("=") || IsPunc("+=") || IsPunc("-=") || IsPunc("*=") || IsPunc("/="))
            {
                string op = Next().Text;
                Node right = ParseAssignment();
                left = MakeBinary(op, left, right);
            }
            Leave();
            return left;
        }

        private Node ParseConditional()
        {
            Node test = ParseLogicalOr();
            if (IsPunc("?"))
            {
                Next();
                Node cons = ParseAssignment();
                Expect(":");
                Node alt = ParseAssignment();
                ConditionalExpr c = new ConditionalExpr();
                c.Test = test; c.Consequent = cons; c.Alternate = alt;
                c.Start = test.Start; c.Line = test.Line; c.End = alt.End; c.EndLine = alt.EndLine;
                return c;
            }
            return test;
        }

        private Node ParseLogicalOr()
        {
            Node left = ParseLogicalAnd();
            while (IsPunc("||"))
            {
                Next();
                Node right = ParseLogicalAnd();
                left = MakeLogical("||", left, right);
            }
            return left;
        }

        private Node ParseLogicalAnd()
        {
            Node left = ParseEquality();
            while (IsPunc("&&"))
            {
                Next();
                Node right = ParseEquality();
                left = MakeLogical("&&", left, right);
            }
            return left;
        }

        private Node ParseEquality()
        {
            Node left = ParseRelational();
            while (IsPunc("==") || IsPunc("!=") || IsPunc("===") || IsPunc("!=="))
            {
                string op = Next().Text;
                Node right = ParseRelational();
                left = MakeBinary(op, left, right);
            }
            return left;
        }

        private Node ParseRelational()
        {
            Node left = ParseAdditive();
            while (IsPunc("<") || IsPunc(">") || IsPunc("<=") || IsPunc(">="))
            {
                string op = Next().Text;
                Node right = ParseAdditive();
                left = MakeBinary(op, left, right);
            }
            return left;
        }

        private Node ParseAdditive()
        {
            Node left = ParseMultiplicative();
            while (IsPunc("+") || IsPunc("-"))
            {
                string op = Next().Text;
                Node right = ParseMultiplicative();
                left = MakeBinary(op, left, right);
            }
            return left;
        }

        private Node ParseMultiplicative()
        {
            Node left = ParseUnary();
            while (IsPunc("*") || IsPunc("/") || IsPunc("%"))
            {
                string op = Next().Text;
                Node right = ParseUnary();
                left = MakeBinary(op, left, right);
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (IsPunc("!") || IsPunc("-") || IsPunc("+"))
            {
                Token op = Next();
                Enter();
                Node arg = ParseUnary();
                Leave();
                UnaryExpr u = new UnaryExpr();
                u.Op = op.Text; u.Arg = arg;
                u.Start = op.Start; u.Line = op.Line; u.End = arg.End; u.EndLine = arg.EndLine;
                return u;
            }
            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            Node node = ParsePrimary();
            while (true)
            {
                if (IsPunc("."))
                {
                    Next();
                    if (Cur.Kind != TokKind.Ident)
                        throw new ParseError("Expected property name", Cur.Start, Cur.Line);
                    Token prop = Next();
                    MemberExpr m = new MemberExpr();
                    m.Obj = node; m.Prop = prop.Text;
                    m.Start = node.Start; m.Line = node.Line; m.End = prop.End; m.EndLine = prop.Line;
                    node = m;
                }
                else if (IsPunc("("))
                {
                    Next();
                    CallExpr call = new CallExpr();
                    call.Callee = node;
                    call.Start = node.Start; call.Line = node.Line;
                    while (!IsPunc(")") && !AtEof)
                    {
                        call.Args.Add(ParseAssignment());
                        if (IsPunc(",")) Next();
                        else break;
                    }
                    Token close = Expect(")");
                    call.End = close.End; call.EndLine = close.Line;
                    node = call;
                }
                else if (IsPunc("["))
                {
                    // index access (rare in PAC). The index expression MUST stay in the AST:
                    // dropping it would hide e.g. host[eval("...")] from the Safety pass.
                    Next();
                    Node index = ParseExpression();
                    Token close = Expect("]");
                    MemberExpr m = new MemberExpr();
                    m.Obj = node; m.Prop = "[]"; m.Index = index;
                    m.Start = node.Start; m.Line = node.Line; m.End = close.End; m.EndLine = close.Line;
                    node = m;
                }
                else break;
            }
            return node;
        }

        private Node ParsePrimary()
        {
            Token t = Cur;
            if (IsPunc("("))
            {
                Next();
                Node e = ParseExpression();
                Expect(")");
                return e;
            }
            if (t.Kind == TokKind.Str)
            {
                Next();
                StringLit s = new StringLit();
                s.Value = t.StringValue; s.Start = t.Start; s.End = t.End; s.Line = t.Line; s.EndLine = t.Line;
                return s;
            }
            if (t.Kind == TokKind.Num)
            {
                Next();
                NumberLit n = new NumberLit();
                n.Raw = t.Text; n.Start = t.Start; n.End = t.End; n.Line = t.Line; n.EndLine = t.Line;
                return n;
            }
            if (t.Kind == TokKind.Ident)
            {
                Next();
                Identifier id = new Identifier();
                id.Name = t.Text; id.Start = t.Start; id.End = t.End; id.Line = t.Line; id.EndLine = t.Line;
                return id;
            }
            throw new ParseError("Unexpected token '" + t.Text + "'", t.Start, t.Line);
        }

        private Node MakeBinary(string op, Node l, Node r)
        {
            BinaryExpr b = new BinaryExpr();
            b.Op = op; b.Left = l; b.Right = r;
            b.Start = l.Start; b.Line = l.Line; b.End = r.End; b.EndLine = r.EndLine;
            return b;
        }

        private Node MakeLogical(string op, Node l, Node r)
        {
            LogicalExpr lg = new LogicalExpr();
            lg.Op = op; lg.Left = l; lg.Right = r;
            lg.Start = l.Start; lg.Line = l.Line; lg.End = r.End; lg.EndLine = r.EndLine;
            return lg;
        }
    }
}
