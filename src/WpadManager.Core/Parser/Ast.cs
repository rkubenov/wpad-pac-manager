using System;
using System.Collections.Generic;

namespace WpadManager.Core.Parser
{
    // Minimal AST for the PAC JavaScript subset. Each node carries source span
    // (char offsets + 1-based lines) so the recognizer can emit precise
    // "unparsed block" ranges and validators can point at lines.
    public abstract class Node
    {
        public int Start;
        public int End;
        public int Line;
        public int EndLine;
    }

    public class Program : Node
    {
        public List<Node> Body = new List<Node>();
        public List<Comment> Comments = new List<Comment>();
    }

    public class FunctionDecl : Node
    {
        public string Name;
        public List<string> Params = new List<string>();
        public Block Body;
    }

    public class Block : Node
    {
        public List<Node> Body = new List<Node>();
    }

    public class IfStatement : Node
    {
        public Node Test;     // expression
        public Node Then;     // statement (often a Block or a ReturnStatement)
        public Node Else;     // statement or null
    }

    public class ReturnStatement : Node
    {
        public Node Argument; // expression or null
    }

    public class VarStatement : Node
    {
        public List<string> Names = new List<string>();
        public List<Node> Inits = new List<Node>();
    }

    public class ExpressionStatement : Node
    {
        public Node Expr;
    }

    // ----- Expressions -----

    public class Identifier : Node
    {
        public string Name;
    }

    public class StringLit : Node
    {
        public string Value;
    }

    public class NumberLit : Node
    {
        public string Raw;
    }

    public class CallExpr : Node
    {
        public Node Callee;
        public List<Node> Args = new List<Node>();
    }

    public class MemberExpr : Node
    {
        public Node Obj;
        public string Prop;
    }

    public class LogicalExpr : Node
    {
        public string Op;   // "&&" or "||"
        public Node Left;
        public Node Right;
    }

    public class BinaryExpr : Node
    {
        public string Op;   // ==, !=, ===, !==, <, >, <=, >=, +, -, *, /, %
        public Node Left;
        public Node Right;
    }

    public class UnaryExpr : Node
    {
        public string Op;   // "!", "-", "+"
        public Node Arg;
    }

    public class ConditionalExpr : Node
    {
        public Node Test;
        public Node Consequent;
        public Node Alternate;
    }
}
