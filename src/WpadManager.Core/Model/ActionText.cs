using System;
using System.Collections.Generic;
using System.Text;

namespace WpadManager.Core.Model
{
    // Parsing/formatting of PAC action strings such as "PROXY p1:8080; SOCKS s:1080; DIRECT".
    // Lenient on parse (keeps malformed tokens for the validator to flag); strict on format.
    public static class ActionText
    {
        public static List<ProxyEntry> Parse(string s)
        {
            List<ProxyEntry> list = new List<ProxyEntry>();
            if (s == null) return list;
            string[] parts = s.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string tok = parts[i].Trim();
                if (tok.Length == 0) continue;
                list.Add(ParseEntry(tok));
            }
            return list;
        }

        private static ProxyEntry ParseEntry(string tok)
        {
            ProxyEntry e = new ProxyEntry();
            // split keyword and remainder on first run of whitespace
            int sp = IndexOfWhitespace(tok);
            string kw = sp < 0 ? tok : tok.Substring(0, sp);
            string rest = sp < 0 ? "" : tok.Substring(sp + 1).Trim();

            switch (kw.ToUpperInvariant())
            {
                case "DIRECT": e.Kind = ProxyKind.Direct; return e;
                case "PROXY": e.Kind = ProxyKind.Proxy; break;
                case "SOCKS": e.Kind = ProxyKind.Socks; break;
                case "SOCKS5": e.Kind = ProxyKind.Socks5; break;
                case "HTTPS": e.Kind = ProxyKind.Https; break;
                case "HTTP": e.Kind = ProxyKind.Http; break;
                default:
                    // unknown keyword; keep raw so validation can report it
                    e.Kind = ProxyKind.Proxy;
                    e.Host = tok;
                    e.Port = 0;
                    return e;
            }

            int colon = rest.LastIndexOf(':');
            if (colon < 0)
            {
                e.Host = rest;
                e.Port = 0;
            }
            else
            {
                e.Host = rest.Substring(0, colon);
                int port;
                if (int.TryParse(rest.Substring(colon + 1), out port)) e.Port = port;
                else e.Port = 0;
            }
            return e;
        }

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == ' ' || s[i] == '\t') return i;
            return -1;
        }

        public static string Format(List<ProxyEntry> entries)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(FormatEntry(entries[i]));
            }
            if (sb.Length == 0) sb.Append("DIRECT");
            return sb.ToString();
        }

        public static string FormatEntry(ProxyEntry e)
        {
            if (e.Kind == ProxyKind.Direct) return "DIRECT";
            string kw;
            switch (e.Kind)
            {
                case ProxyKind.Proxy: kw = "PROXY"; break;
                case ProxyKind.Socks: kw = "SOCKS"; break;
                case ProxyKind.Socks5: kw = "SOCKS5"; break;
                case ProxyKind.Https: kw = "HTTPS"; break;
                case ProxyKind.Http: kw = "HTTP"; break;
                default: kw = "PROXY"; break;
            }
            string hostPort = e.Host != null ? e.Host : "";
            if (e.Port > 0) hostPort = hostPort + ":" + e.Port;
            return kw + " " + hostPort;
        }
    }
}
