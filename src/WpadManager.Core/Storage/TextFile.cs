using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WpadManager.Core.Storage
{
    // Reading and writing the files this tool manages (PAC files, history, workspace).
    //  - Read detects the encoding: a BOM if present, otherwise strict UTF-8, otherwise the
    //    system ANSI code page (legacy PAC files are often Windows-1251). Writing back with
    //    the same encoding means a save never re-encodes — and garbles — existing comments.
    //  - Write is atomic: the text goes to a temp file in the same folder, which then
    //    replaces the target in one step (File.Replace keeps the target's ACLs and
    //    attributes). A crash, full disk or dropped network share mid-write leaves the old
    //    file intact — important for a wpad.dat that every client in the network loads.
    //  - Fingerprint lets the app notice that someone else changed a file since it was read.
    public static class TextFile
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Read(string path)
        {
            Encoding enc;
            return Read(path, out enc);
        }

        public static string Read(string path, out Encoding encoding)
        {
            return Decode(File.ReadAllBytes(path), out encoding);
        }

        public static string Decode(byte[] bytes, out Encoding encoding)
        {
            int n = bytes.Length;
            if (n >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(true);
                return Utf8NoBom.GetString(bytes, 3, n - 3);
            }
            if (n >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = new UnicodeEncoding(false, true);
                return encoding.GetString(bytes, 2, n - 2);
            }
            if (n >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = new UnicodeEncoding(true, true);
                return encoding.GetString(bytes, 2, n - 2);
            }
            try
            {
                string s = new UTF8Encoding(false, true).GetString(bytes);   // throws on invalid UTF-8
                encoding = Utf8NoBom;
                return s;
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Default;   // the ANSI code page, e.g. Windows-1251
                return encoding.GetString(bytes);
            }
        }

        // Bytes to write: the encoding's BOM (only if the file had one) plus the text.
        public static byte[] Encode(string text, Encoding encoding)
        {
            byte[] pre = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(text ?? "");
            byte[] all = new byte[pre.Length + body.Length];
            Buffer.BlockCopy(pre, 0, all, 0, pre.Length);
            Buffer.BlockCopy(body, 0, all, pre.Length, body.Length);
            return all;
        }

        // The encoding to write `text` with: `preferred` when it can represent every
        // character, otherwise BOM-less UTF-8 (a BOM could break a PAC file for a browser
        // that decodes it as Latin-1, and '?' replacements would silently lose text).
        public static Encoding EncodingFor(string text, Encoding preferred)
        {
            if (preferred == null) return Utf8NoBom;
            if (preferred is UTF8Encoding || preferred is UnicodeEncoding) return preferred;
            Encoding strict = Encoding.GetEncoding(preferred.CodePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            try
            {
                strict.GetBytes(text ?? "");
                return preferred;
            }
            catch (EncoderFallbackException)
            {
                return Utf8NoBom;
            }
        }

        public static void Write(string path, string text)
        {
            Write(path, text, Utf8NoBom);
        }

        public static void Write(string path, string text, Encoding encoding)
        {
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            byte[] data = Encode(text, EncodingFor(text, encoding));
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(full)) File.Replace(tmp, full, null, true);
                else File.Move(tmp, full);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
        }

        // SHA-256 of the file's bytes, or null when it does not exist.
        public static string Fingerprint(string path)
        {
            if (!File.Exists(path)) return null;
            return Fingerprint(File.ReadAllBytes(path));
        }

        public static string Fingerprint(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
