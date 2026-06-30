using System;
using System.Collections.Generic;
using System.Text;

namespace WpadManager.Core.Parser
{
    public enum TokKind { Ident, Str, Num, Punc, Eof }

    public class Token
    {
        public TokKind Kind;
        public string Text;         // raw lexeme (for Str: the unquoted value is in StringValue)
        public string StringValue;  // decoded value for string literals
        public int Start;           // char offset (inclusive)
        public int End;             // char offset (exclusive)
        public int Line;            // 1-based

        public override string ToString() { return Kind + ":" + Text; }
    }

    public class Comment
    {
        public bool Block;          // true for /* */, false for //
        public string Text;         // inner text, trimmed of markers
        public int Start;
        public int End;
        public int Line;            // 1-based line where the comment begins
    }

    public class LexError : Exception
    {
        public int Offset;
        public int Line;
        public LexError(string msg, int offset, int line) : base(msg) { Offset = offset; Line = line; }
    }

    // A real (non-regex) tokenizer for the JavaScript subset used by PAC files.
    public class Lexer
    {
        private readonly string _s;
        private int _i;
        private int _line;
        private readonly List<Comment> _comments = new List<Comment>();

        public Lexer(string source)
        {
            _s = source != null ? source : "";
            _i = 0;
            _line = 1;
        }

        public List<Comment> Comments { get { return _comments; } }

        // Multi-char punctuators, longest first so the matcher is greedy.
        private static readonly string[] Puncs = new string[]
        {
            "===", "!==", "==", "!=", "<=", ">=", "&&", "||", "++", "--",
            "+=", "-=", "*=", "/=",
            "(", ")", "{", "}", "[", "]", ";", ",", ".", "!", "<", ">",
            "+", "-", "*", "/", "=", "?", ":", "&", "|", "%"
        };

        public List<Token> Tokenize()
        {
            List<Token> toks = new List<Token>();
            while (true)
            {
                SkipTrivia();
                if (_i >= _s.Length)
                {
                    toks.Add(Make(TokKind.Eof, "", _i, _i));
                    break;
                }
                char c = _s[_i];
                if (IsIdentStart(c)) { toks.Add(ReadIdent()); continue; }
                if (c >= '0' && c <= '9') { toks.Add(ReadNumber()); continue; }
                if (c == '"' || c == '\'') { toks.Add(ReadString(c)); continue; }
                Token p = ReadPunc();
                if (p == null) throw new LexError("Unexpected character '" + c + "'", _i, _line);
                toks.Add(p);
            }
            return toks;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '\n') { _line++; _i++; continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/') { ReadLineComment(); continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*') { ReadBlockComment(); continue; }
                break;
            }
        }

        private void ReadLineComment()
        {
            int start = _i;
            int line = _line;
            _i += 2;
            int textStart = _i;
            while (_i < _s.Length && _s[_i] != '\n') _i++;
            Comment cm = new Comment();
            cm.Block = false;
            cm.Text = _s.Substring(textStart, _i - textStart).Trim();
            cm.Start = start;
            cm.End = _i;
            cm.Line = line;
            _comments.Add(cm);
        }

        private void ReadBlockComment()
        {
            int start = _i;
            int line = _line;
            _i += 2;
            int textStart = _i;
            while (_i + 1 < _s.Length && !(_s[_i] == '*' && _s[_i + 1] == '/'))
            {
                if (_s[_i] == '\n') _line++;
                _i++;
            }
            int textEnd = _i;
            if (_i + 1 < _s.Length) _i += 2; else _i = _s.Length; // consume */
            Comment cm = new Comment();
            cm.Block = true;
            cm.Text = _s.Substring(textStart, textEnd - textStart).Trim();
            cm.Start = start;
            cm.End = _i;
            cm.Line = line;
            _comments.Add(cm);
        }

        private Token ReadIdent()
        {
            int start = _i;
            int line = _line;
            while (_i < _s.Length && IsIdentPart(_s[_i])) _i++;
            return Make(TokKind.Ident, _s.Substring(start, _i - start), start, _i, line);
        }

        private Token ReadNumber()
        {
            int start = _i;
            int line = _line;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' ||
                    c == 'x' || c == 'X' || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') ||
                    c == '+' || c == '-')
                {
                    // allow +/- only right after e/E (exponent) to avoid eating operators
                    if ((c == '+' || c == '-'))
                    {
                        char prev = _s[_i - 1];
                        if (prev != 'e' && prev != 'E') break;
                    }
                    _i++;
                }
                else break;
            }
            return Make(TokKind.Num, _s.Substring(start, _i - start), start, _i, line);
        }

        private Token ReadString(char quote)
        {
            int start = _i;
            int line = _line;
            _i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == quote)
                {
                    Token t = Make(TokKind.Str, _s.Substring(start, _i - start), start, _i, line);
                    t.StringValue = sb.ToString();
                    return t;
                }
                if (c == '\n') throw new LexError("Unterminated string literal", start, line);
                if (c == '\\')
                {
                    if (_i >= _s.Length) break;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new LexError("Unterminated string literal", start, line);
        }

        private Token ReadPunc()
        {
            for (int k = 0; k < Puncs.Length; k++)
            {
                string p = Puncs[k];
                if (_i + p.Length <= _s.Length && _s.Substring(_i, p.Length) == p)
                {
                    Token t = Make(TokKind.Punc, p, _i, _i + p.Length);
                    _i += p.Length;
                    return t;
                }
            }
            return null;
        }

        private Token Make(TokKind kind, string text, int start, int end)
        {
            return Make(kind, text, start, end, _line);
        }

        private Token Make(TokKind kind, string text, int start, int end, int line)
        {
            Token t = new Token();
            t.Kind = kind;
            t.Text = text;
            t.Start = start;
            t.End = end;
            t.Line = line;
            return t;
        }

        private static bool IsIdentStart(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '$';
        }

        private static bool IsIdentPart(char c)
        {
            return IsIdentStart(c) || (c >= '0' && c <= '9');
        }
    }
}
