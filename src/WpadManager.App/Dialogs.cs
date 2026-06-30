using System;
using System.Drawing;
using System.Windows.Forms;
using WpadManager.Core.Model;
using WpadManager.Core.Simulate;
using WpadManager.Core.Storage;

namespace WpadManager.App
{
    // Rule editor used for both "Add" and "Edit".
    // Top half is a friendly builder (pick a type, type a value/proxy, click "insert");
    // the resulting condition/action text stays fully editable below so url/host/domain
    // values can be added, changed or removed by hand too.
    internal class RuleEditForm : Form
    {
        private ComboBox _condType;
        private TextBox _condValue;
        private TextBox _condText;

        private ComboBox _actKind;
        private TextBox _actHostPort;
        private CheckBox _actFallback;
        private TextBox _actText;

        private TextBox _comment;
        private CheckBox _enabled;

        public string ConditionText { get { return _condText.Text.Trim(); } }
        public string ActionTextValue { get { return _actText.Text.Trim(); } }
        public string CommentText { get { string s = _comment.Text.Trim(); return s.Length > 0 ? s : null; } }
        public bool RuleEnabled { get { return _enabled.Checked; } }

        public RuleEditForm(Rule existing) : this(existing, "", "DIRECT") { }

        public RuleEditForm(Rule existing, string cond, string act)
        {
            Text = existing == null ? L.T("Новое правило", "New rule") : L.T("Изменить правило", "Edit rule");
            Width = 600; Height = 560;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false;

            // ----- condition builder -----
            Add(new Label(), L.T("Условие — конструктор:", "Condition — builder:"), 12, 12, 560, 18, FontStyle.Bold);

            Add(new Label(), L.T("Тип:", "Type:"), 12, 40, 40, 20, FontStyle.Regular);
            _condType = new ComboBox();
            _condType.DropDownStyle = ComboBoxStyle.DropDownList;
            _condType.Items.AddRange(new object[] {
                L.T("Домен (host)", "Domain (host)"),
                L.T("URL-шаблон", "URL pattern"),
                L.T("Подсеть клиента (CIDR)", "Client subnet (CIDR)"),
                L.T("Простое имя (intranet)", "Plain name (intranet)") });
            _condType.SelectedIndex = 0;
            _condType.SetBounds(56, 38, 200, 24);
            _condType.SelectedIndexChanged += delegate { _condValue.Enabled = _condType.SelectedIndex != 3; };
            Controls.Add(_condType);

            Add(new Label(), L.T("Значение:", "Value:"), 266, 40, 60, 20, FontStyle.Regular);
            _condValue = new TextBox();
            _condValue.SetBounds(330, 38, 160, 24);
            Controls.Add(_condValue);

            Button condInsert = new Button();
            condInsert.Text = L.T("→ в условие", "→ insert");
            condInsert.SetBounds(496, 37, 80, 26);
            condInsert.Click += delegate { _condText.Text = ComposeCondition(); };
            Controls.Add(condInsert);

            Add(new Label(),
                L.T("Условие (каждый домен/URL — отдельной строкой, можно править вручную):",
                    "Condition (one domain/URL per line, editable by hand):"),
                12, 72, 564, 18, FontStyle.Regular);
            _condText = new TextBox();
            _condText.Multiline = true;
            _condText.ScrollBars = ScrollBars.Vertical;
            _condText.WordWrap = false;
            _condText.Font = new Font(FontFamily.GenericMonospace, 9f);
            _condText.SetBounds(12, 92, 564, 140);
            _condText.Text = cond;
            Controls.Add(_condText);

            // ----- action builder -----
            Add(new Label(), L.T("Действие — конструктор:", "Action — builder:"), 12, 242, 560, 18, FontStyle.Bold);

            Add(new Label(), L.T("Тип:", "Type:"), 12, 270, 40, 20, FontStyle.Regular);
            _actKind = new ComboBox();
            _actKind.DropDownStyle = ComboBoxStyle.DropDownList;
            _actKind.Items.AddRange(new object[] { "DIRECT", "PROXY", "SOCKS", "SOCKS5", "HTTPS", "HTTP" });
            _actKind.SelectedIndex = 0;
            _actKind.SetBounds(56, 268, 100, 24);
            _actKind.SelectedIndexChanged += delegate { _actHostPort.Enabled = _actKind.SelectedIndex != 0; };
            Controls.Add(_actKind);

            Add(new Label(), "host:port:", 166, 270, 60, 20, FontStyle.Regular);
            _actHostPort = new TextBox();
            _actHostPort.SetBounds(230, 268, 160, 24);
            _actHostPort.Enabled = false;
            Controls.Add(_actHostPort);

            _actFallback = new CheckBox();
            _actFallback.Text = "+ DIRECT";
            _actFallback.SetBounds(396, 269, 90, 22);
            Controls.Add(_actFallback);

            Button actInsert = new Button();
            actInsert.Text = L.T("→ в действие", "→ insert");
            actInsert.SetBounds(496, 267, 80, 26);
            actInsert.Click += delegate { _actText.Text = ComposeAction(); };
            Controls.Add(actInsert);

            Add(new Label(), L.T("Действие (можно править вручную):", "Action (editable by hand):"),
                12, 300, 560, 18, FontStyle.Regular);
            _actText = new TextBox();
            _actText.SetBounds(12, 320, 564, 24);
            _actText.Text = act;
            Controls.Add(_actText);

            // ----- meta -----
            Add(new Label(), L.T("Комментарий:", "Comment:"), 12, 354, 90, 20, FontStyle.Regular);
            _comment = new TextBox();
            _comment.SetBounds(104, 352, 472, 24);
            if (existing != null && existing.Comment != null) _comment.Text = existing.Comment;
            Controls.Add(_comment);

            _enabled = new CheckBox();
            _enabled.Text = L.T("Правило включено", "Rule enabled");
            _enabled.SetBounds(12, 384, 200, 22);
            _enabled.Checked = existing == null ? true : existing.Enabled;
            Controls.Add(_enabled);

            // ----- buttons -----
            Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
            ok.SetBounds(400, 472, 80, 30);
            Button cancel = new Button(); cancel.Text = L.T("Отмена", "Cancel"); cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(492, 472, 84, 30);
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        private void Add(Label l, string text, int x, int y, int w, int h, FontStyle style)
        {
            l.Text = text; l.SetBounds(x, y, w, h);
            if (style == FontStyle.Bold) l.Font = new Font(l.Font, FontStyle.Bold);
            Controls.Add(l);
        }

        private string ComposeCondition()
        {
            string v = _condValue.Text.Trim().Replace("\"", "\\\"");
            switch (_condType.SelectedIndex)
            {
                case 0: return "dnsDomainIs(host, \"" + v + "\")";
                case 1: return "shExpMatch(url, \"" + v + "\")";
                case 2:
                    string net, mask;
                    if (!ParseCidr(_condValue.Text.Trim(), out net, out mask))
                    {
                        MessageBox.Show(L.T("Подсеть в формате 10.0.0.0/24 или 10.0.0.0/255.255.255.0",
                            "Subnet as 10.0.0.0/24 or 10.0.0.0/255.255.255.0"),
                            L.T("Подсеть", "Subnet"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return _condText.Text;
                    }
                    return "isInNet(myIpAddress(), \"" + net + "\", \"" + mask + "\")";
                case 3: return "isPlainHostName(host)";
                default: return _condText.Text;
            }
        }

        private string ComposeAction()
        {
            if (_actKind.SelectedIndex == 0) return "DIRECT";
            string kind = _actKind.SelectedItem.ToString();
            string hp = _actHostPort.Text.Trim();
            string a = kind + " " + hp;
            if (_actFallback.Checked) a += "; DIRECT";
            return a;
        }

        // Accepts "10.0.0.0/24" or "10.0.0.0/255.255.255.0" or a bare IP.
        internal static bool ParseCidr(string s, out string net, out string mask)
        {
            net = null; mask = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            int slash = s.IndexOf('/');
            if (slash < 0) { net = s; mask = "255.255.255.255"; return IsIp(net); }
            net = s.Substring(0, slash).Trim();
            string rest = s.Substring(slash + 1).Trim();
            if (rest.IndexOf('.') >= 0) mask = rest;
            else
            {
                int p;
                if (!int.TryParse(rest, out p) || p < 0 || p > 32) return false;
                mask = PrefixToMask(p);
            }
            return IsIp(net) && IsIp(mask);
        }

        private static string PrefixToMask(int p)
        {
            uint m = p == 0 ? 0u : (0xFFFFFFFFu << (32 - p));
            return ((m >> 24) & 255) + "." + ((m >> 16) & 255) + "." + ((m >> 8) & 255) + "." + (m & 255);
        }

        private static bool IsIp(string s)
        {
            string[] parts = s.Split('.');
            if (parts.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], out v) || v < 0 || v > 255) return false;
            }
            return true;
        }
    }

    // Simulator dialog: URL + Host + optional client IP (for isInNet(myIpAddress(), ...)).
    internal class SimulateForm : Form
    {
        private readonly RuleSet _rs;
        private TextBox _url, _host, _myip, _result;

        public SimulateForm(RuleSet rs)
        {
            _rs = rs;
            Text = L.T("Симулятор", "Simulator");
            Width = 640; Height = 520;
            StartPosition = FormStartPosition.CenterParent;

            _url = new TextBox(); _url.SetBounds(120, 12, 490, 24); _url.Text = "http://"; Controls.Add(_url);
            // Host is the domain part of the URL; the browser derives it the same way,
            // so we auto-extract it and show it read-only rather than asking for it.
            _host = new TextBox(); _host.SetBounds(120, 42, 490, 24);
            _host.ReadOnly = true; _host.BackColor = SystemColors.Control; Controls.Add(_host);
            _myip = new TextBox(); _myip.SetBounds(120, 72, 200, 24); Controls.Add(_myip);
            _url.TextChanged += delegate { _host.Text = ExtractHost(_url.Text); };
            _host.Text = ExtractHost(_url.Text);
            Lbl("URL:", 12, 14, 100);
            Lbl(L.T("Host (из URL):", "Host (from URL):"), 12, 44, 100);
            Lbl(L.T("IP клиента:", "Client IP:"), 12, 74, 100);
            Lbl(L.T("(host берётся из URL; IP клиента нужен только для правил isInNet(myIpAddress()))",
                    "(host comes from the URL; client IP is only needed for isInNet(myIpAddress()) rules)"),
                12, 100, 600);

            Button run = new Button();
            run.Text = L.T("Запустить", "Run");
            run.SetBounds(120, 124, 120, 30);
            run.Click += delegate { RunSim(); };
            Controls.Add(run);

            _result = new TextBox();
            _result.Multiline = true;
            _result.ReadOnly = true;
            _result.ScrollBars = ScrollBars.Vertical;
            _result.Font = new Font(FontFamily.GenericMonospace, 9f);
            _result.SetBounds(12, 164, 600, 290);
            Controls.Add(_result);

            Button close = new Button();
            close.Text = L.T("Закрыть", "Close"); close.DialogResult = DialogResult.Cancel;
            close.SetBounds(528, 460, 84, 28);
            Controls.Add(close);
            CancelButton = close;
        }

        private void Lbl(string text, int x, int y, int w)
        {
            Label l = new Label(); l.Text = text; l.SetBounds(x, y, w, 18); Controls.Add(l);
        }

        // Pull the host out of a URL the same way a browser would: strip scheme,
        // userinfo, path/query/fragment and port. "http://u@www.x.com:8080/p" -> "www.x.com".
        internal static string ExtractHost(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url.Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int cut = s.IndexOfAny(new char[] { '/', '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);
            int at = s.IndexOf('@');
            if (at >= 0) s = s.Substring(at + 1);
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);
            return s.Trim();
        }

        private void RunSim()
        {
            SimInput input = new SimInput(_url.Text.Trim(), ExtractHost(_url.Text));
            string ip = _myip.Text.Trim();
            if (ip.Length > 0) input.MyIp = ip;
            input.Now = DateTime.Now;

            SimResult r = Simulator.Run(_rs, input);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("URL : " + input.Url);
            sb.AppendLine("HOST: " + input.Host);
            if (ip.Length > 0) sb.AppendLine("IP  : " + ip);
            sb.AppendLine();
            sb.AppendLine(L.T("Трасса (по порядку):", "Trace (in order):"));
            for (int i = 0; i < r.Trace.Count; i++)
            {
                SimStep s = r.Trace[i];
                sb.AppendLine("  " + L.T("правило ", "rule ") + s.Order + "  [" + s.Result + "]  " + s.Reason);
            }
            sb.AppendLine();
            sb.AppendLine(r.Matched != null
                ? ("➤ " + L.T("Сработало правило ", "Matched rule ") + r.Matched.Order)
                : ("➤ " + L.T("Сработало: <по умолчанию>", "Matched: <default>")));
            sb.AppendLine("➤ " + L.T("Прокси: ", "Proxy: ") + r.ActionString());
            if (r.HasIndeterminateBeforeMatch)
                sb.AppendLine("\n⚠ " + L.T(
                    "Более раннее правило неопределимо (нет IP клиента / DNS / время) — в реальной сети результат может отличаться. Укажите IP клиента, если используются правила по подсети.",
                    "An earlier rule is indeterminate (no client IP / DNS / time) — the real network result may differ. Provide the client IP if subnet rules are used."));
            _result.Text = sb.ToString();
        }
    }

    // History / rollback dialog (newest first).
    internal class HistoryForm : Form
    {
        public string SelectedVersionId;
        private readonly Store _store;
        private ListBox _list;

        public HistoryForm(Store store)
        {
            _store = store;
            Text = L.T("История версий", "Version history");
            Width = 680; Height = 460;
            StartPosition = FormStartPosition.CenterParent;

            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.Font = new Font(FontFamily.GenericMonospace, 8.5f);
            for (int i = _store.Versions.Count - 1; i >= 0; i--)
            {
                VersionEntry v = _store.Versions[i];
                int rules = v.Snapshot != null ? v.Snapshot.Rules.Count : 0;
                _list.Items.Add(v.Timestamp + "  " + v.Author + "  (" + rules + L.T(" правил)  ", " rules)  ") +
                    (v.Note != null ? v.Note : ""));
            }
            Controls.Add(_list);

            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 46;
            Button rollback = new Button(); rollback.Text = L.T("Восстановить выбранный вариант", "Restore selected version");
            rollback.SetBounds(10, 9, 240, 28);
            rollback.Click += delegate { DoRollback(); };
            Button close = new Button(); close.Text = L.T("Закрыть", "Close"); close.DialogResult = DialogResult.Cancel;
            close.SetBounds(576, 9, 84, 28);
            bottom.Controls.Add(rollback); bottom.Controls.Add(close);
            Controls.Add(bottom);
        }

        private void DoRollback()
        {
            int sel = _list.SelectedIndex;
            if (sel < 0) return;
            int idx = _store.Versions.Count - 1 - sel; // list is newest-first
            SelectedVersionId = _store.Versions[idx].Id;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
