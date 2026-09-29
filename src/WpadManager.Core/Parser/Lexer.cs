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

        // JavaScript line terminators. A // comment ends at ANY of these, not only at '\n';
        // treating e.g. a lone '\r' as comment text would hide the code after it from the
        // Safety pass while a browser happily executes it.
        public static bool IsLineTerminator(char c)
        {
            return c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029';
        }

        // Advance past one line terminator at _i (CRLF counts as a single line break).
        private void ConsumeLineTerminator()
        {
            if (_s[_i] == '\r' && _i + 1 < _s.Length && _s[_i + 1] == '\n') _i++;
            _i++;
            _line++;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (IsLineTerminator(c)) { ConsumeLineTerminator(); continue; }
                if (c == ' ' || c == '\t' || c == '\f' || c == '\v') { _i++; continue; }
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
            while (_i < _s.Length && !IsLineTerminator(_s[_i])) _i++;
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
                if (IsLineTerminator(_s[_i])) ConsumeLineTerminator();
                else _i++;
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
                // A raw line break is a syntax error in a JS string; U+2028/U+2029 are only
                // legal since ES2019, so older PAC engines would reject them too.
                if (IsLineTerminator(c)) throw new LexError("Line break inside string literal", start, line);
                if (c == '\\')
                {
                    if (_i >= _s.Length) break;
                    ReadEscape(sb, start, line);
                }
                else sb.Append(c);
            }
            throw new LexError("Unterminated string literal", start, line);
        }

        // Decode one escape (the backslash is already consumed) exactly as JavaScript does,
        // so the value we analyze and re-emit is the value the browser sees.
        private void ReadEscape(StringBuilder sb, int start, int line)
        {
            char e = _s[_i];
            if (IsLineTerminator(e))
            {
                ConsumeLineTerminator();   // line continuation: contributes nothing
                return;
            }
            _i++;
            switch (e)
            {
                case 'n': sb.Append('\n'); return;
                case 't': sb.Append('\t'); return;
                case 'r': sb.Append('\r'); return;
                case 'b': sb.Append('\b'); return;
                case 'f': sb.Append('\f'); return;
                case 'v': sb.Append('\v'); return;
                case 'x': sb.Append((char)ReadHex(2, start, line)); return;
                case 'u':
                    if (_i < _s.Length && _s[_i] == '{')
                    {
                        _i++;
                        int end = _s.IndexOf('}', _i);
                        if (end < 0 || end == _i || end - _i > 6)
                            throw new LexError("Invalid \\u{...} escape", start, line);
                        int cp = ReadHex(end - _i, start, line);
                        _i++; // }
                        if (cp > 0x10FFFF) throw new LexError("Invalid \\u{...} escape", start, line);
                        if (cp <= 0xFFFF) sb.Append((char)cp);   // incl. lone surrogates, as JS allows
                        else sb.Append(char.ConvertFromUtf32(cp));
                    }
                    else sb.Append((char)ReadHex(4, start, line));
                    return;
                case '0':
                    if (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9')
                        throw new LexError("Octal escapes are not supported", start, line);
                    sb.Append('\0');
                    return;
                default:
                    if (e >= '1' && e <= '7')
                        throw new LexError("Octal escapes are not supported", start, line);
                    sb.Append(e);   // \\ \" \' \/ and identity escapes
                    return;
            }
        }

        private int ReadHex(int digits, int start, int line)
        {
            if (_i + digits > _s.Length) throw new LexError("Invalid hex escape", start, line);
            int v = 0;
            for (int k = 0; k < digits; k++)
            {
                int d = HexValue(_s[_i + k]);
                if (d < 0) throw new LexError("Invalid hex escape", start, line);
                v = v * 16 + d;
            }
            _i += digits;
            return v;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
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
