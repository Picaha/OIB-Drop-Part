using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>The active locks, the filters, the actions and the health strip.</summary>
    public sealed class MainForm : Form
    {
        private const string All = "All";
        private static readonly string[] LockTypes = { "FEEDER", "PU", "PN", "MPN", "SUPPLIER", "CONVEYOR" };

        private readonly ApiClient _client;
        private readonly Session _session;

        private readonly ComboBox _type = new ComboBox();
        private readonly ComboBox _line = new ComboBox();
        private readonly TextBox _search = new TextBox();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Button _new = new Button();
        private readonly Button _release = new Button();
        private readonly Button _retry = new Button();
        private readonly Button _history = new Button();
        private readonly Button _admin = new Button();
        private readonly Button _logout = new Button();

        private readonly StatusStrip _strip = new StatusStrip();
        private readonly ToolStripStatusLabel _service = new ToolStripStatusLabel("Connecting...");
        private readonly ToolStripStatusLabel _sc = new ToolStripStatusLabel("SC");
        private readonly ToolStripStatusLabel _lc = new ToolStripStatusLabel("LineControl");
        private readonly ToolStripStatusLabel _db = new ToolStripStatusLabel("DB");
        private readonly ToolStripStatusLabel _failed = new ToolStripStatusLabel("FAILED: -");
        private readonly ToolStripStatusLabel _alarm = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        private readonly Timer _lockTimer = new Timer { Interval = 5000 };
        private readonly Timer _healthTimer = new Timer { Interval = 15000 };
        private readonly Timer _searchDelay = new Timer { Interval = 400 };
        private readonly RefreshGate _lockGate = new RefreshGate();
        private readonly RefreshGate _healthGate = new RefreshGate();

        private bool _refreshAgain;
        private bool _binding;
        private bool _closed;
        private bool _linesLoaded;
        private IList<LockDto> _current = new LockDto[0];

        public MainForm(ApiClient client, Session session)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (session == null) throw new ArgumentNullException(nameof(session));
            _client = client;
            _session = session;

            Text = "Interlock - " + session.Username + " (" + session.Role + ")";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 640);
            MinimumSize = new Size(900, 400);

            BuildLayout();
            WireEvents();
            UpdateButtons();
        }

        /// <summary>True when the program should show the login screen again (logout, or the session ended).</summary>
        public bool ReturnToLogin { get; private set; }

        /// <summary>Why the login screen comes back, shown there; null for a plain logout.</summary>
        public string LoginNotice { get; private set; }

        // The dialogs are hooks so a test can replace them; the defaults are the real dialogs.
        public Action<IWin32Window, ApiClient, Session> NewLockDialog { get; set; } = Dialogs.NewLock;
        public Action<IWin32Window, ApiClient, LockDto> ReleaseDialog { get; set; } = Dialogs.Release;
        public Action<IWin32Window, ApiClient, LockDto> HistoryDialog { get; set; } = Dialogs.History;
        public Action<IWin32Window, ApiClient, Session> AdminDialog { get; set; } = Dialogs.Admin;

        // ---- layout ------------------------------------------------------------------------------------------------

        private void BuildLayout()
        {
            _type.DropDownStyle = ComboBoxStyle.DropDownList;
            _type.Items.Add(All);
            _type.Items.AddRange(LockTypes);
            _type.SelectedIndex = 0;
            _type.Width = 110;

            _line.DropDownStyle = ComboBoxStyle.DropDownList;
            _line.Items.Add(All);
            _line.SelectedIndex = 0;
            _line.Width = 200;

            _search.Width = 200;

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
            filters.Controls.Add(new Label { Text = "Type", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            filters.Controls.Add(_type);
            filters.Controls.Add(new Label { Text = "Line", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
            filters.Controls.Add(_line);
            filters.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
            filters.Controls.Add(_search);

            _new.Text = "New lock";
            _release.Text = "Release";
            _retry.Text = "Retry";
            _history.Text = "History";
            _admin.Text = "Admin";
            _logout.Text = "Log out";
            _retry.Visible = false;
            _admin.Visible = _session.IsAdmin;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), WrapContents = false };
            foreach (var b in new[] { _new, _release, _retry, _history, _admin, _logout })
            {
                b.Width = 90;
                buttons.Controls.Add(b);
            }

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.BackgroundColor = SystemColors.Window;
            AddColumn("#", 60);
            AddColumn("Type", 80);
            AddColumn("Target", 150);
            AddColumn("Lanes", 60);
            AddColumn("Line", 160);
            AddColumn("Reason", 220);
            AddColumn("Source", 90);
            AddColumn("By", 90);
            AddColumn("Created (local)", 140);
            AddColumn("State", 90);
            AddColumn("Error", 260);

            _strip.Items.AddRange(new ToolStripItem[] { _service, new ToolStripSeparator(), _sc, _lc, _db, new ToolStripSeparator(), _failed, new ToolStripSeparator(), _alarm });
            SetDot(_sc, "SC", Dot.Unknown);
            SetDot(_lc, "LineControl", Dot.Unknown);
            SetDot(_db, "DB", Dot.Unknown);

            Controls.Add(_grid);
            Controls.Add(filters);
            Controls.Add(buttons);
            Controls.Add(_strip);
        }

        private void AddColumn(string header, int width)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private void WireEvents()
        {
            _client.Unauthorized += OnUnauthorized;
            Load += async (s, e) => await UiSafety.RunAsync(OnLoadAsync);
            FormClosed += (s, e) => Shutdown();

            _lockTimer.Tick += async (s, e) => await UiSafety.RunAsync(() => RefreshLocksAsync(false));
            _healthTimer.Tick += async (s, e) => await UiSafety.RunAsync(RefreshHealthAsync);
            _searchDelay.Tick += async (s, e) => { _searchDelay.Stop(); await UiSafety.RunAsync(() => RefreshLocksAsync(true)); };

            _type.SelectedIndexChanged += async (s, e) => await RefreshLocksAsync(true);
            _line.SelectedIndexChanged += async (s, e) => await RefreshLocksAsync(true);
            _search.TextChanged += (s, e) => { _searchDelay.Stop(); _searchDelay.Start(); };
            _grid.SelectionChanged += (s, e) => { if (!_binding) UpdateButtons(); };

            _new.Click += async (s, e) => { Dialog(() => NewLockDialog(this, _client, _session)); await RefreshLocksAsync(true); };
            _release.Click += async (s, e) => { var l = Selected(); if (MainRules.CanRelease(_session, l)) { Dialog(() => ReleaseDialog(this, _client, l)); await RefreshLocksAsync(true); } };
            _history.Click += (s, e) => { var l = Selected(); Dialog(() => HistoryDialog(this, _client, l)); };
            _admin.Click += (s, e) => Dialog(() => AdminDialog(this, _client, _session));
            _retry.Click += async (s, e) => await UiSafety.RunAsync(RetryAsync);
            _logout.Click += (s, e) => { ReturnToLogin = true; Close(); };
        }

        /// <summary>Runs a dialog hook; a failure is logged and shown as one neutral message.</summary>
        private void Dialog(Action show)
        {
            if (!UiSafety.Run(show) && !_closed)
                MessageBox.Show(this, "Something went wrong. See ui-error.log for details.", "Interlock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---- refresh -----------------------------------------------------------------------------------------------

        private async System.Threading.Tasks.Task OnLoadAsync()
        {
            _lockTimer.Start();
            _healthTimer.Start();
            await LoadLinesAsync();
            await RefreshLocksAsync(true);
            await RefreshHealthAsync();
        }

        private async System.Threading.Tasks.Task LoadLinesAsync()
        {
            if (_linesLoaded || _closed) return;
            try
            {
                var lines = await _client.GetLines();
                if (_closed) return;
                var keep = _line.Text;
                _binding = true;
                try
                {
                    _line.Items.Clear();
                    _line.Items.Add(All);
                    foreach (var l in lines) _line.Items.Add(l);
                    var index = _line.Items.IndexOf(keep);
                    _line.SelectedIndex = index < 0 ? 0 : index;
                }
                finally { _binding = false; }
                _linesLoaded = true;
            }
            catch (Exception ex) { UiSafety.Log(ex); /* retried on the next tick; the grid shows the service state */ }
        }

        /// <summary>Reloads the grid. A tick (<paramref name="force"/> false) is skipped while a refresh runs; a filter change is run again afterwards.</summary>
        private async System.Threading.Tasks.Task RefreshLocksAsync(bool force)
        {
            if (_closed || _binding) return;
            if (!_lockGate.TryEnter())
            {
                if (force) _refreshAgain = true;
                return;
            }

            try
            {
                if (!_linesLoaded) await LoadLinesAsync();
                var locks = await _client.GetLocks("ACTIVE", MainRules.FilterValue(_type.Text), MainRules.FilterValue(_line.Text), _search.Text);
                if (_closed) return;
                Bind(locks);
                _service.Text = locks.Count + " active lock(s), updated " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                if (!(ex is ApiException)) UiSafety.Log(ex);
                if (_closed) return;
                _service.Text = UiSafety.StatusFor(ex);
            }
            finally
            {
                _lockGate.Exit();
            }

            if (_refreshAgain && !_closed)
            {
                _refreshAgain = false;
                await RefreshLocksAsync(false);
            }
        }

        private async System.Threading.Tasks.Task RefreshHealthAsync()
        {
            if (_closed || !_healthGate.TryEnter()) return;
            try
            {
                var summary = HealthSummary.From(await _client.GetHealth());
                if (_closed) return;
                SetDot(_sc, "SC", summary.SetupCenter);
                SetDot(_lc, "LineControl", summary.LineControl);
                SetDot(_db, "DB", summary.Db);
                _failed.Text = "FAILED: " + summary.Failed;
                _failed.ForeColor = summary.Failed > 0 ? Color.Firebrick : SystemColors.ControlText;
                _alarm.Text = summary.LatestAlarm.Length == 0 ? "" : "Alarm: " + summary.LatestAlarm;
            }
            catch (Exception ex)
            {
                if (!(ex is ApiException)) UiSafety.Log(ex);
                if (_closed) return;
                SetDot(_sc, "SC", Dot.Unknown);
                SetDot(_lc, "LineControl", Dot.Unknown);
                SetDot(_db, "DB", Dot.Unknown);
                _failed.Text = "FAILED: -";
                _alarm.Text = "";
            }
            finally
            {
                _healthGate.Exit();
            }
        }

        private static void SetDot(ToolStripStatusLabel label, string name, Dot state)
        {
            label.Text = "● " + name;
            label.ForeColor = state == Dot.Ok ? Color.ForestGreen : state == Dot.Bad ? Color.Firebrick : Color.Gray;
        }

        /// <summary>Fills the grid and keeps the selected lock and the scroll position.</summary>
        private void Bind(IList<LockDto> locks)
        {
            var selectedId = Selected() == null ? (long?)null : Selected().id;
            var firstRow = _grid.FirstDisplayedScrollingRowIndex;
            var column = _grid.CurrentCell == null ? 0 : _grid.CurrentCell.ColumnIndex;

            _binding = true;
            _grid.SuspendLayout();
            try
            {
                _current = locks;
                _grid.Rows.Clear();
                foreach (var l in locks)
                {
                    var index = _grid.Rows.Add(
                        l.id, l.type, l.target, l.lanes, l.linePath, l.reason, l.source, l.createdBy,
                        MainRules.LocalTime(l.createdAt), l.enforceState, l.enforceError);
                    var row = _grid.Rows[index];
                    row.Tag = l;
                    row.DefaultCellStyle.BackColor = RowStyle.BackColorFor(l.enforceState);
                    row.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
                    row.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
                }

                _grid.ClearSelection();
                if (selectedId.HasValue)
                {
                    foreach (DataGridViewRow row in _grid.Rows)
                    {
                        if (((LockDto)row.Tag).id == selectedId.Value)
                        {
                            row.Selected = true;
                            _grid.CurrentCell = row.Cells[column];
                            break;
                        }
                    }
                }

                if (firstRow >= 0 && firstRow < _grid.Rows.Count)
                {
                    try { _grid.FirstDisplayedScrollingRowIndex = firstRow; }
                    catch (InvalidOperationException) { /* grid not visible yet */ }
                }
            }
            finally
            {
                _grid.ResumeLayout();
                _binding = false;
            }

            UpdateButtons();
        }

        // ---- buttons -----------------------------------------------------------------------------------------------

        private LockDto Selected()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as LockDto;
        }

        private void UpdateButtons()
        {
            var l = Selected();
            _release.Enabled = MainRules.CanRelease(_session, l);
            _retry.Visible = MainRules.CanRetry(l);
            _history.Enabled = true;
        }

        private async System.Threading.Tasks.Task RetryAsync()
        {
            var l = Selected();
            if (!MainRules.CanRetry(l)) return;
            _retry.Enabled = false;
            try
            {
                await _client.Retry(l.id);
            }
            catch (Exception ex)
            {
                if (!(ex is ApiException)) UiSafety.Log(ex);
                if (!_closed)
                    MessageBox.Show(this, ex is ApiException ? ex.Message : "Unexpected error - see log", "Retry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!_closed) _retry.Enabled = true;
            }
            await RefreshLocksAsync(true);
        }

        // ---- session end -------------------------------------------------------------------------------------------

        private void OnUnauthorized(object sender, EventArgs e)
        {
            // Raised on whatever thread finished the call.
            if (_closed || IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(EndSession)); }
                catch (InvalidOperationException) { /* form already gone */ }
            }
            else
            {
                EndSession();
            }
        }

        private void EndSession()
        {
            if (_closed) return;
            ReturnToLogin = true;
            LoginNotice = "Your session has ended. Please sign in again.";
            // An open dialog is modal on this form; close it first so the form can end.
            foreach (var owned in OwnedForms)
            {
                try { owned.Close(); }
                catch (Exception ex) { UiSafety.Log(ex); }
            }
            Close();
        }

        private void Shutdown()
        {
            _closed = true;
            _lockTimer.Stop();
            _healthTimer.Stop();
            _searchDelay.Stop();
            _client.Unauthorized -= OnUnauthorized;
            _client.Token = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closed = true;
                _lockTimer.Dispose();
                _healthTimer.Dispose();
                _searchDelay.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
