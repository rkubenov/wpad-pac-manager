using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WpadManager.Core.Model;
using WpadManager.Core.Parser;
using WpadManager.Core.Generator;
using WpadManager.Core.Validate;
using WpadManager.Core.Analyze;
using WpadManager.Core.Simulate;
using WpadManager.Core.Storage;
using WpadManager.Core.Resolve;

namespace WpadManager.App
{
    // Main GUI window. PacMagic-style: a grid of rules on top, a findings panel below,
    // and a toolbar. The app is a workspace of several open documents: each opened
    // .pac/.dat is one Store with its OWN current rules + history/audit, persisted in a
    // sidecar "<file>.history.json" next to that file. A small wpad-workspace.json next to
    // the .exe remembers which files are open, which is active, and the UI language.
    // The UI is bilingual (RU/EN) via L.T(...) and rebuilds in place when the language changes.
    internal class MainForm : Form
    {
        private readonly string _workspacePath;
        private readonly List<Store> _docs = new List<Store>();
        private int _active = -1;
        private bool _suppressSelect = false;
        private bool _appliedDark = false;
        private readonly RuleSet _emptySet = new RuleSet();

        private DataGridView _grid;
        private ListBox _findings;
        private ToolStripStatusLabel _status;
        private ToolStripComboBox _files;

        public MainForm()
        {
            _workspacePath = RuleStore.WorkspacePath();
            // Language and theme must be known before building the (localized) controls.
            WorkspaceState saved = RuleStore.LoadWorkspace(_workspacePath);
            L.Set(saved.Language);
            Theme.Set(saved.Theme);

            Text = "WPAD / PAC File Manager";
            Width = 1040; Height = 700;
            StartPosition = FormStartPosition.CenterScreen;
            // Reuse the icon embedded in the .exe for the title bar / taskbar.
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildAllUi();
            FitWidthToToolbar();
            LoadWorkspace();
            RefreshAll();

            // "Match Windows" follows the system light/dark switch (and high contrast) live.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // SystemEvents is static: unsubscribe or it keeps this form alive.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
            base.OnFormClosed(e);
        }

        private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && Theme.IsDark != _appliedDark) RebuildUi();
            });
        }

        // ---- active document accessors ----

        private Store Doc
        {
            get { return (_active >= 0 && _active < _docs.Count) ? _docs[_active] : null; }
        }

        private RuleSet Current
        {
            get { Store d = Doc; return (d != null && d.Current != null) ? d.Current : _emptySet; }
        }

        private string SourcePath
        {
            get { Store d = Doc; return d != null ? d.SourcePath : null; }
        }

        // ---- layout ----

        private void BuildAllUi()
        {
            BuildToolbar();
            BuildGrid();
            BuildFindings();
            BuildStatusBar();
            Theme.Apply(this);
            _appliedDark = Theme.IsDark;
        }

        // Open wide enough that no toolbar button starts out in the overflow menu (the Russian
        // labels are long), but never wider than the screen's working area.
        private void FitWidthToToolbar()
        {
            foreach (Control c in Controls)
            {
                ToolStrip ts = c as ToolStrip;
                if (ts == null || ts is StatusStrip) continue;
                // ToolStrip.GetPreferredSize under-reports once items overflow, so add them up.
                int need = ts.Padding.Horizontal + (Width - ClientSize.Width) + 24;
                foreach (ToolStripItem it in ts.Items)
                    need += it.GetPreferredSize(Size.Empty).Width + it.Margin.Horizontal;
                int max = Screen.FromControl(this).WorkingArea.Width;
                Width = Math.Min(Math.Max(Width, need), max);
            }
        }

        // Rebuild the whole window after a language or theme change (state lives in _docs).
        private void RebuildUi()
        {
            Control[] old = new Control[Controls.Count];
            Controls.CopyTo(old, 0);
            Controls.Clear();
            for (int i = 0; i < old.Length; i++) old[i].Dispose();   // free their window handles
            BuildAllUi();
            RefreshAll();
        }

        private void BuildToolbar()
        {
            ToolStrip ts = new ToolStrip();
            ts.GripStyle = ToolStripGripStyle.Hidden;
            AddButton(ts, L.T("Импорт .pac/.dat", "Import .pac/.dat"), ImportPac);
            AddButton(ts, L.T("Новый файл", "New file"), NewFile);
            AddButton(ts, L.T("Экспорт как…", "Export as…"), ExportPac);
            ts.Items.Add(new ToolStripSeparator());

            ts.Items.Add(new ToolStripLabel(L.T("Файл:", "File:")));
            _files = new ToolStripComboBox();
            _files.DropDownStyle = ComboBoxStyle.DropDownList;
            _files.Width = 230;
            _files.SelectedIndexChanged += FileSelected;
            ts.Items.Add(_files);
            AddButton(ts, L.T("Закрыть файл", "Close file"), CloseFile);
            ts.Items.Add(new ToolStripSeparator());

            AddButton(ts, L.T("Добавить", "Add"), AddRule);
            AddButton(ts, L.T("Изменить", "Edit"), EditRule);
            AddButton(ts, L.T("Удалить", "Delete"), DeleteRule);
            AddButton(ts, L.T("Вкл/выкл", "On/off"), ToggleEnabled);
            ts.Items.Add(new ToolStripSeparator());
            AddButton(ts, L.T("Проверить", "Check"), RunChecks);
            AddButton(ts, L.T("DNS-проверка", "DNS check"), DnsResolveAll);
            AddButton(ts, L.T("Симулятор", "Simulator"), OpenSimulate);
            ts.Items.Add(new ToolStripSeparator());
            AddButton(ts, L.T("Сохранить в файл", "Save to file"), SaveToFile);
            AddButton(ts, L.T("История", "History"), OpenHistory);

            // Language and theme share one drop-down so the busy toolbar keeps its room.
            ToolStripDropDownButton settings = new ToolStripDropDownButton(L.T("Настройки", "Settings"));
            settings.Alignment = ToolStripItemAlignment.Right;
            settings.Overflow = ToolStripItemOverflow.Never;   // always reachable, even in a narrow window

            ToolStripMenuItem lang = new ToolStripMenuItem(L.T("Язык", "Language"));
            lang.DropDownItems.Add(Choice("Русский", L.Current == AppLang.Ru,
                delegate { SetLanguage(AppLang.Ru); }));
            lang.DropDownItems.Add(Choice("English", L.Current == AppLang.En,
                delegate { SetLanguage(AppLang.En); }));

            ToolStripMenuItem theme = new ToolStripMenuItem(L.T("Тема", "Theme"));
            theme.DropDownItems.Add(Choice(L.T("Как в Windows", "Match Windows"), Theme.Mode == ThemeMode.System,
                delegate { SetTheme(ThemeMode.System); }));
            theme.DropDownItems.Add(Choice(L.T("Светлая", "Light"), Theme.Mode == ThemeMode.Light,
                delegate { SetTheme(ThemeMode.Light); }));
            theme.DropDownItems.Add(Choice(L.T("Тёмная", "Dark"), Theme.Mode == ThemeMode.Dark,
                delegate { SetTheme(ThemeMode.Dark); }));

            settings.DropDownItems.Add(lang);
            settings.DropDownItems.Add(theme);
            ts.Items.Add(settings);

            Controls.Add(ts);
        }

        private static ToolStripMenuItem Choice(string text, bool isChecked, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Checked = isChecked;
            mi.Click += onClick;
            return mi;
        }

        private void AddButton(ToolStrip ts, string text, EventHandler handler)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.Click += handler;
            ts.Items.Add(b);
        }

        private void BuildGrid()
        {
            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.RowHeadersVisible = false;
            _grid.CellDoubleClick += GridDoubleClick;

            _grid.Columns.Add(Col("order", "#", 40));
            _grid.Columns.Add(Col("enabled", L.T("Вкл", "On"), 50));
            _grid.Columns.Add(Col("cond", L.T("Условие", "Condition"), 320));
            _grid.Columns.Add(Col("action", L.T("Действие", "Action"), 200));
            _grid.Columns.Add(Col("comment", L.T("Комментарий", "Comment"), 200));
            _grid.Columns["cond"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.Controls.Add(_grid);
            Controls.Add(p);
        }

        private DataGridViewTextBoxColumn Col(string name, string header, int width)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.Name = name; c.HeaderText = header; c.Width = width;
            c.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            return c;
        }

        private void BuildFindings()
        {
            _findings = new ListBox();
            _findings.Dock = DockStyle.Bottom;
            _findings.Height = 170;
            _findings.HorizontalScrollbar = true;
            _findings.Font = new Font(FontFamily.GenericMonospace, 8.5f);
            // Owner-drawn so each line is colored by its severity (in both themes).
            _findings.DrawMode = DrawMode.OwnerDrawFixed;
            _findings.ItemHeight = TextRenderer.MeasureText("Ag", _findings.Font).Height + 2;
            _findings.DrawItem += FindingsDrawItem;
            Controls.Add(_findings);
        }

        private void FindingsDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            string line = _findings.Items[e.Index].ToString();
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Palette p = Theme.P;
            Color back = selected ? p.Selection : _findings.BackColor;
            Color fore = selected ? p.SelectionText : Theme.SeverityColor(line, _findings.ForeColor);
            using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, line, e.Font, e.Bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            e.DrawFocusRectangle();
        }

        private void BuildStatusBar()
        {
            StatusStrip ss = new StatusStrip();
            _status = new ToolStripStatusLabel();
            // Spring: take the free width and clip a long path, instead of the StatusStrip
            // hiding the whole label once the text is wider than the window.
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            ss.Items.Add(_status);
            Controls.Add(ss);
        }

        // ---- language / theme ----

        private void SetLanguage(AppLang lang)
        {
            if (L.Current == lang) return;
            L.Current = lang;
            SaveWorkspace();
            // Defer the rebuild so we are not tearing down the menu inside its own event.
            this.BeginInvoke((MethodInvoker)delegate { RebuildUi(); });
        }

        private void SetTheme(ThemeMode mode)
        {
            if (Theme.Mode == mode) return;
            Theme.Mode = mode;
            SaveWorkspace();
            this.BeginInvoke((MethodInvoker)delegate { RebuildUi(); });
        }

        // ---- workspace (multi-file) ----

        // On launch: reopen every file recorded in the workspace, attaching each one's
        // sidecar history. Falls back to a one-time import of the old single-file store.
        private void LoadWorkspace()
        {
            WorkspaceState ws = RuleStore.LoadWorkspace(_workspacePath);

            if (ws.OpenFiles.Count == 0)
            {
                // Migrate the previous single-file store, if it pointed at a real file.
                Store old = RuleStore.Load(RuleStore.DefaultPath());
                if (!string.IsNullOrEmpty(old.SourcePath) && File.Exists(old.SourcePath))
                {
                    Store d = OpenDocument(old.SourcePath, false);
                    if (d != null) { _docs.Add(d); _active = 0; SaveWorkspace(); }
                }
                return;
            }

            for (int i = 0; i < ws.OpenFiles.Count; i++)
            {
                Store d = OpenDocument(ws.OpenFiles[i], false);
                if (d != null) _docs.Add(d);
            }
            _active = _docs.Count > 0 ? 0 : -1;
            if (ws.ActiveFile != null)
                for (int i = 0; i < _docs.Count; i++)
                    if (PathEq(_docs[i].SourcePath, ws.ActiveFile)) { _active = i; break; }
        }

        // Parse a .pac/.dat from disk and attach its sidecar history. The file on disk is the
        // source of truth for the rules; the sidecar contributes versions/audit. interactive
        // controls whether errors pop dialogs (false during silent startup reload).
        private Store OpenDocument(string path, bool interactive)
        {
            if (!File.Exists(path))
            {
                if (interactive)
                    MessageBox.Show(L.T("Файл не найден:\n", "File not found:\n") + path,
                        L.T("Открытие", "Open"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            string src;
            try { src = File.ReadAllText(path); }
            catch (Exception ex)
            {
                if (interactive)
                    MessageBox.Show(L.T("Не удалось прочитать файл:\n", "Could not read file:\n") + ex.Message,
                        L.T("Открытие", "Open"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            PacImportResult res = PacImporter.Import(src);
            if (!res.Ok)
            {
                if (interactive)
                    MessageBox.Show(L.T("Синтаксическая ошибка (строка ", "Syntax error (line ") +
                        res.SyntaxErrorLine + "):\n" + res.SyntaxError,
                        L.T("Импорт", "Import"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            string sidecar = RuleStore.SidecarPath(path);
            Store d = RuleStore.Load(sidecar);   // existing history for this file, or empty
            d.SourcePath = path;
            d.Current = res.RuleSet;             // rules come from the .dat itself
            if (d.Versions.Count == 0)
            {
                RuleStore.Commit(d, d.Current, Environment.UserName,
                    L.T("Импорт: ", "Import: ") + Path.GetFileName(path));
                try { RuleStore.Save(sidecar, d); } catch { }
            }
            return d;
        }

        private void SaveWorkspace()
        {
            WorkspaceState ws = new WorkspaceState();
            for (int i = 0; i < _docs.Count; i++) ws.OpenFiles.Add(_docs[i].SourcePath);
            ws.ActiveFile = SourcePath;
            ws.Language = L.Code();
            ws.Theme = Theme.Code();
            try { RuleStore.SaveWorkspace(_workspacePath, ws); } catch { }
        }

        private void FileSelected(object sender, EventArgs e)
        {
            if (_suppressSelect) return;
            int idx = _files.SelectedIndex;
            if (idx < 0 || idx >= _docs.Count) return;
            _active = idx;
            SaveWorkspace();
            RefreshGrid();
            SetStatus();
            RunChecks(null, null);
        }

        private void CloseFile(object sender, EventArgs e)
        {
            if (Doc == null) return;
            if (MessageBox.Show(
                L.T("Закрыть файл «", "Close file “") + Path.GetFileName(SourcePath) +
                L.T("»?\n\nФайл и его история на диске останутся; он лишь убирается из списка.",
                    "”?\n\nThe file and its history on disk remain; it is only removed from the list."),
                L.T("Закрыть файл", "Close file"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _docs.RemoveAt(_active);
            _active = _docs.Count > 0 ? 0 : -1;
            SaveWorkspace();
            RefreshAll();
        }

        private bool AlreadyOpen(string path)
        {
            for (int i = 0; i < _docs.Count; i++)
                if (PathEq(_docs[i].SourcePath, path)) { _active = i; return true; }
            return false;
        }

        private static bool PathEq(string a, string b)
        {
            if (a == null || b == null) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ---- data refresh ----

        private void RefreshAll()
        {
            RebuildFileSelector();
            RefreshGrid();
            SetStatus();
            RunChecks(null, null);
        }

        private void RebuildFileSelector()
        {
            _suppressSelect = true;
            _files.Items.Clear();
            for (int i = 0; i < _docs.Count; i++)
            {
                string p = _docs[i].SourcePath;
                _files.Items.Add(string.IsNullOrEmpty(p) ? L.T("(без имени)", "(unnamed)") : Path.GetFileName(p));
            }
            if (_active >= 0 && _active < _docs.Count) _files.SelectedIndex = _active;
            _suppressSelect = false;
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            List<Rule> rules = Current.Rules;
            for (int i = 0; i < rules.Count; i++)
            {
                Rule r = rules[i];
                string cond = r.Condition != null ? PacGenerator.GenCondition(r.Condition) : L.T("(пусто)", "(empty)");
                string act = ActionText.Format(r.Action);
                int idx = _grid.Rows.Add(r.Order, r.Enabled ? "✓" : "—", cond, act, r.Comment != null ? r.Comment : "");
                if (!r.Enabled) _grid.Rows[idx].DefaultCellStyle.ForeColor = Theme.P.TextMuted;
            }
            // The default action row — editable via double-click or the "По умолчанию…" button.
            string def = ActionText.Format(Current.DefaultAction);
            int di = _grid.Rows.Add("→", "", L.T("(по умолчанию)", "(default)"), def,
                L.T("двойной клик — изменить", "double-click to edit"));
            _grid.Rows[di].DefaultCellStyle.ForeColor = Theme.P.Accent;
        }

        private bool IsDefaultRow(int rowIndex)
        {
            return rowIndex == Current.Rules.Count; // the last row, after all rules
        }

        private void SetStatus()
        {
            if (Doc == null)
            {
                _status.Text = L.T("Нет открытых файлов. Импортируйте .pac/.dat или создайте новый.",
                    "No files open. Import a .pac/.dat or create a new one.");
                return;
            }
            _status.Text =
                L.T("Файл: ", "File: ") + SourcePath +
                L.T("   |   правил: ", "   |   rules: ") + Current.Rules.Count +
                L.T("   |   версий: ", "   |   versions: ") + Doc.Versions.Count +
                L.T("   |   открыто файлов: ", "   |   files open: ") + _docs.Count;
        }

        private int SelectedRuleIndex()
        {
            if (_grid.SelectedRows.Count == 0) return -1;
            int idx = _grid.SelectedRows[0].Index;
            if (idx < 0 || idx >= Current.Rules.Count) return -1; // last row = default
            return idx;
        }

        private bool RequireDoc()
        {
            if (Doc != null) return true;
            MessageBox.Show(L.T("Сначала откройте файл (Импорт) или создайте новый.",
                "Open a file (Import) or create a new one first."),
                L.T("Нет файла", "No file"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        // ---- import / new / export ----

        private void ImportPac(object sender, EventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = "PAC / WPAD files (*.pac;*.dat;*.js;*.txt)|*.pac;*.dat;*.js;*.txt|All files (*.*)|*.*";
            if (d.ShowDialog() != DialogResult.OK) return;

            if (AlreadyOpen(d.FileName))
            {
                SaveWorkspace();
                RefreshAll();
                MessageBox.Show(L.T("Этот файл уже открыт — переключаюсь на него.",
                    "This file is already open — switching to it."),
                    L.T("Импорт", "Import"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Store doc = OpenDocument(d.FileName, true);
            if (doc == null) return;
            _docs.Add(doc);
            _active = _docs.Count - 1;
            SaveWorkspace();
            RefreshAll();

            string note = doc.Current.Unparsed.Count > 0
                ? (L.T("\n\nНераспознанных блоков (сохранены дословно): ",
                       "\n\nUnrecognized blocks (kept verbatim): ") + doc.Current.Unparsed.Count)
                : "";
            MessageBox.Show(L.T("Импортировано правил: ", "Rules imported: ") + doc.Current.Rules.Count + note,
                L.T("Импорт", "Import"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void NewFile(object sender, EventArgs e)
        {
            SaveFileDialog d = new SaveFileDialog();
            d.Filter = "PAC file (*.pac)|*.pac|WPAD file (*.dat)|*.dat|All files (*.*)|*.*";
            d.FileName = "proxy.pac";
            if (d.ShowDialog() != DialogResult.OK) return;

            if (AlreadyOpen(d.FileName)) { SaveWorkspace(); RefreshAll(); return; }

            RuleSet rs = new RuleSet();
            try { File.WriteAllText(d.FileName, PacGenerator.Generate(rs)); }
            catch (Exception ex)
            {
                MessageBox.Show(L.T("Не удалось создать файл:\n", "Could not create file:\n") + ex.Message,
                    L.T("Новый файл", "New file"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Store doc = new Store();
            doc.Current = rs;
            doc.SourcePath = d.FileName;
            RuleStore.Commit(doc, rs, Environment.UserName, L.T("Создан новый файл", "New file created"));
            try { RuleStore.Save(RuleStore.SidecarPath(d.FileName), doc); } catch { }

            _docs.Add(doc);
            _active = _docs.Count - 1;
            SaveWorkspace();
            RefreshAll();
        }

        private void ExportPac(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            SaveFileDialog d = new SaveFileDialog();
            d.Filter = "PAC file (*.pac)|*.pac|WPAD file (*.dat)|*.dat|All files (*.*)|*.*";
            d.FileName = !string.IsNullOrEmpty(SourcePath) ? Path.GetFileName(SourcePath) : "proxy.pac";

            // An exported file is as deployable as a saved one, so it passes the same gate.
            string pac;
            Report rep = Gate.Check(Current, out pac);
            if (HasBlockingIssues(rep))
            {
                MessageBox.Show(
                    L.T("Экспорт отменён: в файле есть проблемы, которые нужно исправить.\n\n",
                        "Export cancelled: the file has problems that must be fixed.\n\n") +
                    BlockingSummary(rep),
                    L.T("Экспорт заблокирован", "Export blocked"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (d.ShowDialog() != DialogResult.OK) return;
            try
            {
                File.WriteAllText(d.FileName, pac);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.T("Не удалось записать файл:\n", "Could not write file:\n") + ex.Message,
                    L.T("Экспорт", "Export"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(L.T("Сохранено: ", "Saved: ") + d.FileName, L.T("Экспорт", "Export"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- save back to the source file + history ----

        private void SaveToFile(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;

            // Refuse to write a file that still has problems — fix them first. The text that
            // passed the gate is exactly what gets written below.
            string pac;
            Report rep = Gate.Check(Current, out pac);
            if (HasBlockingIssues(rep))
            {
                MessageBox.Show(
                    L.T("Сохранение отменено: в файле есть проблемы, которые нужно исправить.\n\n",
                        "Save cancelled: the file has problems that must be fixed.\n\n") +
                    BlockingSummary(rep) +
                    L.T("\nУстраните ошибки и предупреждения (панель проверки внизу), затем сохраните снова.",
                        "\nFix the errors and warnings (check panel below), then save again."),
                    L.T("Сохранение заблокировано", "Save blocked"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string note = MainForm.Prompt(L.T("Сохранить", "Save"),
                L.T("Примечание к версии (для истории):", "Version note (for history):"), "");
            if (note == null) return;

            try
            {
                // Snapshot into this file's history, write the .dat, update its sidecar.
                RuleStore.Commit(Doc, Current, Environment.UserName, note);
                File.WriteAllText(SourcePath, pac);
                RuleStore.Save(RuleStore.SidecarPath(SourcePath), Doc);
                SaveWorkspace();
                SetStatus();
                MessageBox.Show(L.T("Изменения записаны в файл:\n", "Changes written to file:\n") + SourcePath +
                    L.T("\n\nИстория этого файла: ", "\n\nHistory of this file: ") + RuleStore.SidecarPath(SourcePath),
                    L.T("Сохранение", "Save"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L.T("Не удалось записать файл:\n", "Could not write file:\n") + ex.Message,
                    L.T("Сохранение", "Save"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- rule CRUD ----

        private void AddRule(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            using (RuleEditForm f = new RuleEditForm(null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string err;
                Rule r = BuildRule(f.ConditionText, f.ActionTextValue, out err);
                if (r == null) { WarnBuild(err); return; }

                // Duplicate / overlap guard: warn if the new rule is already covered by
                // (or would shadow) an existing one, e.g. adding "test.example.com" when ".example.com" exists.
                List<string> overlaps = Shadowing.CheckCandidate(Current, r.Condition);
                if (overlaps.Count > 0)
                {
                    string msg = L.T("Похоже, правило пересекается с существующими:\n\n - ",
                        "This rule seems to overlap existing ones:\n\n - ") +
                        string.Join("\n - ", overlaps.ToArray()) +
                        L.T("\n\nВсё равно добавить?", "\n\nAdd anyway?");
                    if (MessageBox.Show(msg, L.T("Проверка дубликатов", "Duplicate check"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }

                r.Comment = f.CommentText;
                r.Enabled = f.RuleEnabled;
                r.Order = Current.Rules.Count;
                r.CreatedAt = Iso.Now();
                r.UpdatedAt = r.CreatedAt;
                Current.Rules.Add(r);
                AfterMutation();
            }
        }

        private void EditRule()
        {
            if (!RequireDoc()) return;
            int i = SelectedRuleIndex();
            if (i < 0)
            {
                MessageBox.Show(L.T("Выберите правило в таблице.", "Select a rule in the table."),
                    L.T("Изменить", "Edit"));
                return;
            }
            Rule cur = Current.Rules[i];
            string condText = cur.Condition != null ? PacGenerator.GenConditionPretty(cur.Condition) : "";
            using (RuleEditForm f = new RuleEditForm(cur, condText, ActionText.Format(cur.Action)))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string err;
                Rule built = BuildRule(f.ConditionText, f.ActionTextValue, out err);
                if (built == null) { WarnBuild(err); return; }
                // keep identity/order/created; replace meaning.
                cur.Condition = built.Condition;
                cur.Action = built.Action;
                cur.Comment = f.CommentText;
                cur.Enabled = f.RuleEnabled;
                cur.UpdatedAt = Iso.Now();
                AfterMutation();
            }
        }

        private void EditRule(object sender, EventArgs e) { EditRule(); }

        // Edit the default (fall-through) action — the final return in FindProxyForURL.
        // Reached by double-clicking the "(default)" row in the grid.
        private void EditDefault()
        {
            if (!RequireDoc()) return;
            string cur = ActionText.Format(Current.DefaultAction);
            string txt = MainForm.Prompt(
                L.T("Действие по умолчанию", "Default action"),
                L.T("Что вернуть, когда ни одно правило не сработало (напр. DIRECT или PROXY host:3128; DIRECT):",
                    "What to return when no rule matches (e.g. DIRECT or PROXY host:3128; DIRECT):"),
                cur);
            if (txt == null) return;
            List<ProxyEntry> parsed = ActionText.Parse(txt);
            if (parsed.Count == 0) parsed = ActionText.Parse("DIRECT");
            Current.DefaultAction = parsed;
            AfterMutation();
        }

        private void GridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && IsDefaultRow(e.RowIndex)) { EditDefault(); return; }
            if (SelectedRuleIndex() >= 0) EditRule();
        }

        private void DeleteRule(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            int i = SelectedRuleIndex();
            if (i < 0) return;
            if (MessageBox.Show(L.T("Удалить правило ", "Delete rule ") + i + "?",
                L.T("Удаление", "Delete"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Current.Rules.RemoveAt(i);
            Reorder();
            AfterMutation();
        }

        private void ToggleEnabled(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            int i = SelectedRuleIndex();
            if (i < 0) return;
            Current.Rules[i].Enabled = !Current.Rules[i].Enabled;
            Current.Rules[i].UpdatedAt = Iso.Now();
            AfterMutation();
        }

        private void Reorder()
        {
            for (int i = 0; i < Current.Rules.Count; i++) Current.Rules[i].Order = i;
        }

        // After any edit: refresh UI + re-run checks. (Persisting is an explicit "Сохранить".)
        private void AfterMutation()
        {
            RefreshGrid();
            SetStatus();
            RunChecks(null, null);
        }

        private void WarnBuild(string err)
        {
            MessageBox.Show(L.T("Не удалось разобрать условие/действие: ",
                "Could not parse the condition/action: ") + err,
                L.T("Ошибка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // Build a Rule from condition + action text by reusing the real parser.
        private Rule BuildRule(string cond, string act, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(cond)) { error = L.T("пустое условие", "empty condition"); return null; }
            string a = act != null ? act : "DIRECT";
            string wrapped =
                "function FindProxyForURL(url, host) {\n" +
                "  if (" + cond + ") return \"" + a.Replace("\"", "\\\"") + "\";\n" +
                "  return \"DIRECT\";\n" +
                "}\n";
            PacImportResult res = PacImporter.Import(wrapped);
            if (!res.Ok) { error = L.T("синтаксис (строка ", "syntax (line ") + res.SyntaxErrorLine + ")"; return null; }
            if (res.RuleSet.Rules.Count == 0) { error = L.T("условие не распознано", "condition not recognized"); return null; }
            return res.RuleSet.Rules[0];
        }

        // ---- validation panel ----

        // The combined diagnostics for the current rule set (safety + structure + shadowing),
        // from the same write gate that save / export / restore go through.
        private Report BuildReport()
        {
            string pac;
            return Gate.Check(Current, out pac);
        }

        // A file is "clean enough" to save / simulate only if it has no Warning, Error or
        // Critical findings. (Info — e.g. valid narrow-before-broad exceptions — is allowed.)
        private static bool HasBlockingIssues(Report rep)
        {
            return Gate.IsBlocking(rep);
        }

        private static string Loc(Finding f)
        {
            if (f.Order >= 0) return L.T("правило ", "rule ") + f.Order;
            if (f.Line > 0) return L.T("строка ", "line ") + f.Line;
            return "";
        }

        private static string BlockingSummary(Report rep)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < rep.Findings.Count; i++)
            {
                Finding f = rep.Findings[i];
                if (f.Severity == Severity.Info) continue;
                string loc = Loc(f);
                sb.AppendLine(" - [" + f.Severity.ToString().ToUpperInvariant() + "] " +
                    (loc.Length > 0 ? loc + " — " : "") + f.Message);
            }
            return sb.ToString();
        }

        private void RunChecks(object sender, EventArgs e)
        {
            Report rep = BuildReport();
            List<string> lines = new List<string>();

            if (rep.Findings.Count == 0)
            {
                lines.Add(L.T("Проблем не найдено. OK.", "No problems found. OK."));
            }
            else
            {
                lines.Add(L.T("Найдено: ", "Found: ") + rep.Count(Severity.Critical) + " critical, " +
                    rep.Count(Severity.Error) + " error, " + rep.Count(Severity.Warning) +
                    " warning, " + rep.Count(Severity.Info) + " info");
                for (int i = 0; i < rep.Findings.Count; i++)
                {
                    Finding f = rep.Findings[i];
                    string loc = Loc(f);
                    lines.Add("[" + f.Severity.ToString().ToUpperInvariant() + "] " +
                        (loc.Length > 0 ? loc + " — " : "") + f.Message);
                }
            }

            // An owner-drawn list cannot measure its items, so size the horizontal scroll here.
            int widest = 0;
            for (int i = 0; i < lines.Count; i++)
                widest = Math.Max(widest, TextRenderer.MeasureText(lines[i], _findings.Font).Width);

            _findings.BeginUpdate();
            _findings.Items.Clear();
            _findings.Items.AddRange(lines.ToArray());
            _findings.HorizontalExtent = widest + 8;
            _findings.EndUpdate();
        }

        // ---- DNS resolve: is each rule's domain still live? ----

        private void DnsResolveAll(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            List<string> names = DnsCheck.NamesFrom(Current);
            if (names.Count == 0)
            {
                MessageBox.Show(L.T("В правилах нет доменов (dnsDomainIs / localHostOrDomainIs) для DNS-проверки.",
                    "No domains (dnsDomainIs / localHostOrDomainIs) in the rules to check."),
                    L.T("DNS-проверка", "DNS check"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DnsCheck.ClearCache();
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int ok = 0, bad = 0;
            for (int i = 0; i < names.Count; i++)
            {
                DnsResult r = DnsCheck.Resolve(names[i], 2000);
                if (r.Ok)
                {
                    ok++;
                    sb.AppendLine("OK    " + r.Name + "  ->  " + string.Join(", ", r.Addresses.ToArray()));
                }
                else
                {
                    bad++;
                    sb.AppendLine("FAIL  " + r.Name + "  —  " + r.Error +
                        L.T("   (правило может быть устаревшим)", "   (rule may be stale)"));
                }
            }
            Cursor.Current = old;

            string head = L.T("Проверено доменов: ", "Domains checked: ") + names.Count +
                L.T("   |   резолвится: ", "   |   resolves: ") + ok +
                L.T("   |   не резолвится: ", "   |   fails: ") + bad + "\r\n\r\n";
            ShowText(L.T("DNS-проверка", "DNS check"), head + sb.ToString());
        }

        // A simple read-only scrollable text dialog for longer reports.
        private void ShowText(string title, string text)
        {
            Form f = new Form();
            f.Text = title; f.Width = 680; f.Height = 480;
            f.StartPosition = FormStartPosition.CenterParent;
            TextBox t = new TextBox();
            t.Multiline = true; t.ReadOnly = true; t.ScrollBars = ScrollBars.Both; t.WordWrap = false;
            t.Dock = DockStyle.Fill;
            t.Font = new Font(FontFamily.GenericMonospace, 9f);
            t.Text = text;
            f.Controls.Add(t);
            Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 42;
            Button close = new Button(); close.Text = L.T("Закрыть", "Close"); close.DialogResult = DialogResult.OK;
            close.SetBounds(580, 7, 84, 28); bottom.Controls.Add(close);
            f.Controls.Add(bottom);
            f.AcceptButton = close;
            Theme.Apply(f);
            f.ShowDialog(this);
            f.Dispose();
        }

        // ---- simulator ----

        private void OpenSimulate(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;

            // A file with problems must not be simulated — the result would be misleading.
            Report rep = BuildReport();
            if (HasBlockingIssues(rep))
            {
                MessageBox.Show(
                    L.T("Симуляция недоступна: в файле есть проблемы.\n\n",
                        "Simulation unavailable: the file has problems.\n\n") +
                    BlockingSummary(rep) +
                    L.T("\nИсправьте их, затем запустите симулятор снова.",
                        "\nFix them, then run the simulator again."),
                    L.T("Симулятор заблокирован", "Simulator blocked"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (SimulateForm f = new SimulateForm(Current))
            {
                f.ShowDialog(this);
            }
        }

        // ---- history / rollback (per active file) ----

        private void OpenHistory(object sender, EventArgs e)
        {
            if (!RequireDoc()) return;
            if (Doc.Versions.Count == 0)
            {
                MessageBox.Show(L.T("История пуста. Сначала сохраните файл.",
                    "History is empty. Save the file first."),
                    L.T("История", "History"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (HistoryForm h = new HistoryForm(Doc))
            {
                if (h.ShowDialog(this) == DialogResult.OK && h.SelectedVersionId != null)
                {
                    RuleStore.Rollback(Doc, h.SelectedVersionId, Environment.UserName);
                    RefreshGrid();
                    SetStatus();
                    RunChecks(null, null);

                    // The history lives in an editable JSON sidecar, so a restored version gets
                    // the same gate as a normal save before it may overwrite the live file.
                    string pac;
                    Report rep = Gate.Check(Current, out pac);
                    if (HasBlockingIssues(rep))
                    {
                        MessageBox.Show(
                            L.T("Вариант восстановлен в редакторе, но файл НЕ перезаписан: в нём есть проблемы.\n\n",
                                "The version was restored in the editor, but the file was NOT rewritten: it has problems.\n\n") +
                            BlockingSummary(rep) +
                            L.T("\nИсправьте их (панель проверки внизу) и нажмите «Сохранить в файл».",
                                "\nFix them (check panel below) and press “Save to file”."),
                            L.T("Восстановление", "Restore"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Reflect the restored variant in the actual source file + sidecar too.
                    if (!string.IsNullOrEmpty(SourcePath))
                    {
                        try
                        {
                            File.WriteAllText(SourcePath, pac);
                            RuleStore.Save(RuleStore.SidecarPath(SourcePath), Doc);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(L.T("Восстановлено в приложении, но файл переписать не удалось:\n",
                                "Restored in the app, but the file could not be rewritten:\n") + ex.Message,
                                L.T("Восстановление", "Restore"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    MessageBox.Show(L.T("Вариант восстановлен.", "Version restored."),
                        L.T("История", "History"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        // ---- a tiny modal text prompt (WinForms has none built in) ----

        internal static string Prompt(string title, string label, string initial)
        {
            Form f = new Form();
            f.Text = title; f.Width = 520; f.Height = 170;
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false;

            Label l = new Label(); l.Text = label; l.SetBounds(12, 12, 480, 36);
            TextBox t = new TextBox(); t.Text = initial; t.SetBounds(12, 52, 480, 24);
            Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
            ok.SetBounds(316, 90, 80, 28);
            Button cancel = new Button(); cancel.Text = L.T("Отмена", "Cancel"); cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(412, 90, 80, 28);

            f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            Theme.Apply(f);

            using (f)
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
        }
    }
}
