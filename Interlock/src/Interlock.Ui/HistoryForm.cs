using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>Read-only history: released locks, the actions of one lock, and the scan decisions. Nothing is edited here.</summary>
    public sealed class HistoryForm : DialogBase
    {
        private readonly ApiClient _client;
        private readonly LockDto _selected;
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };

        // Released locks
        private readonly DataGridView _released;
        private readonly Button _releasedRefresh = new Button { Text = "Refresh", Width = 90 };
        private readonly Label _releasedStatus = new Label { Dock = DockStyle.Bottom, Height = 22 };
        private readonly Panel _releasedBar = new Panel { Dock = DockStyle.Top, Height = 36 };

        // Actions
        private readonly DataGridView _actions;
        private readonly EnterTextBox _lockId = new EnterTextBox { Width = 90 };
        private readonly Button _actionsLoad = new Button { Text = "Load", Width = 90 };
        private readonly Label _lockSummary = new Label { AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
        private readonly Label _actionsStatus = new Label { Dock = DockStyle.Bottom, Height = 22 };
        private readonly FlowLayoutPanel _actionsBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 0, 0) };

        // Decisions
        private readonly DataGridView _decisions;
        private readonly TextBox _uid = new TextBox { Width = 140 };
        private readonly TextBox _pn = new TextBox { Width = 140 };
        private readonly DateTimePicker _from = new DateTimePicker { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false, Width = 120 };
        private readonly DateTimePicker _to = new DateTimePicker { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Checked = false, Width = 120 };
        private readonly Button _search = new Button { Text = "Search", Width = 90 };
        private readonly Label _decisionsStatus = new Label { Dock = DockStyle.Bottom, Height = 22 };
        private readonly FlowLayoutPanel _decisionsBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 0, 0) };

        private bool _decisionsLoaded;
        private bool _binding;

        public HistoryForm(ApiClient client, LockDto selected)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            _client = client;
            _selected = selected;

            Text = "History";
            ClientSize = new Size(1100, 560);

            _released = GridKit.ReadOnly(
                new[] { "#", "Type", "Target", "Lanes", "Line", "Reason", "Source", "By", "Created (local)", "State", "Error", "Released by", "Released (local)", "Release reason" },
                new[] { 60, 80, 140, 60, 140, 180, 80, 80, 130, 80, 160, 90, 130, 200 });
            _actions = GridKit.ReadOnly(
                new[] { "Time (local)", "Action", "Success", "Attempt", "Error" },
                new[] { 150, 160, 70, 70, 560 });
            _decisions = GridKit.ReadOnly(
                new[] { "Time (local)", "Method", "Result", "UID", "PN", "MPN", "Supplier", "Line", "Machine", "Feeder", "Message", "Locks" },
                new[] { 130, 90, 70, 120, 100, 100, 90, 120, 90, 80, 300, 70 });

            BuildReleasedTab();
            BuildActionsTab();
            BuildDecisionsTab();
            Controls.Add(_tabs);

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 30 };
            Controls.Add(close);
            CancelButton = close;

            _releasedRefresh.Click += async (s, e) => await RunLoad(_releasedBar, _releasedStatus, LoadReleasedAsync);
            _actionsLoad.Click += async (s, e) => await RunLoad(_actionsBar, _actionsStatus, LoadActionsAsync);
            _search.Click += async (s, e) => await RunLoad(_decisionsBar, _decisionsStatus, LoadDecisionsAsync);
            _lockId.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunLoad(_actionsBar, _actionsStatus, LoadActionsAsync); } };
            _uid.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunLoad(_decisionsBar, _decisionsStatus, LoadDecisionsAsync); } };
            _pn.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RunLoad(_decisionsBar, _decisionsStatus, LoadDecisionsAsync); } };
            // Only a click or a key press of the user copies a lock number into the box; filling the grid never does.
            _released.CellClick += (s, e) => PickReleased();
            _released.KeyUp += (s, e) => { if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) PickReleased(); };
            _released.CellDoubleClick += async (s, e) =>
            {
                if (e.RowIndex < 0 || SelectedReleased() == null) return;
                PickReleased();
                _tabs.SelectedIndex = 1;
                await RunLoad(_actionsBar, _actionsStatus, LoadActionsAsync);
            };
            _tabs.SelectedIndexChanged += async (s, e) =>
            {
                if (_tabs.SelectedIndex == 2 && !_decisionsLoaded)
                    await RunLoad(_decisionsBar, _decisionsStatus, LoadDecisionsAsync);
            };
            Shown += async (s, e) => await OnShownAsync();
        }

        private void BuildReleasedTab()
        {
            var page = new TabPage("Released locks");
            _releasedRefresh.Location = new Point(6, 6);
            _releasedBar.Controls.Add(_releasedRefresh);
            page.Controls.Add(_released);
            page.Controls.Add(_releasedBar);
            page.Controls.Add(_releasedStatus);
            _tabs.TabPages.Add(page);
        }

        private void BuildActionsTab()
        {
            var page = new TabPage("Actions of a lock");
            _actionsBar.Controls.Add(new Label { Text = "Lock #", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _actionsBar.Controls.Add(_lockId);
            _actionsBar.Controls.Add(_actionsLoad);
            _actionsBar.Controls.Add(_lockSummary);
            if (_selected != null) _lockId.Text = _selected.id.ToString(CultureInfo.InvariantCulture);
            page.Controls.Add(_actions);
            page.Controls.Add(_actionsBar);
            page.Controls.Add(_actionsStatus);
            _tabs.TabPages.Add(page);
        }

        private void BuildDecisionsTab()
        {
            var page = new TabPage("Decisions");
            _decisionsBar.Controls.Add(new Label { Text = "UID", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            _decisionsBar.Controls.Add(_uid);
            _decisionsBar.Controls.Add(new Label { Text = "PN", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
            _decisionsBar.Controls.Add(_pn);
            _decisionsBar.Controls.Add(new Label { Text = "From", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
            _decisionsBar.Controls.Add(_from);
            _decisionsBar.Controls.Add(new Label { Text = "To", AutoSize = true, Margin = new Padding(8, 6, 4, 0) });
            _decisionsBar.Controls.Add(_to);
            _decisionsBar.Controls.Add(_search);
            page.Controls.Add(_decisions);
            page.Controls.Add(_decisionsBar);
            page.Controls.Add(_decisionsStatus);
            _tabs.TabPages.Add(page);
        }

        // ---- loading -----------------------------------------------------------------------------------------------

        private async Task OnShownAsync()
        {
            if (_selected != null)
            {
                _tabs.SelectedIndex = 1;
                await RunLoad(_actionsBar, _actionsStatus, LoadActionsAsync);
            }
            await RunLoad(_releasedBar, _releasedStatus, LoadReleasedAsync);
        }

        /// <summary>Runs one load with the tab's bar disabled (no double submit); errors go to the tab's status line.</summary>
        private async Task RunLoad(Control bar, Label status, Func<Label, Task> body)
        {
            if (!bar.Enabled || IsClosed) return;
            bar.Enabled = false;
            ShowInfo(status, "Loading...");
            try { await Guarded(() => body(status), status); }
            finally { if (!IsClosed) bar.Enabled = true; }
        }

        private LockDto SelectedReleased()
        {
            return _released.SelectedRows.Count == 0 ? null : _released.SelectedRows[0].Tag as LockDto;
        }

        private void PickReleased()
        {
            _lockId.Text = HistoryLockSelection.NextIdText(_lockId.Text, SelectedReleased(), !_binding);
        }

        private async Task LoadReleasedAsync(Label status)
        {
            var locks = await _client.GetLocks("RELEASED", null, null, null);
            if (IsClosed) return;
            _binding = true;
            try
            {
                _released.Rows.Clear();
                foreach (var l in locks)
                {
                    var i = _released.Rows.Add(l.id, l.type, l.target, l.lanes, l.linePath, l.reason, l.source, l.createdBy,
                        MainRules.LocalTime(l.createdAt), l.enforceState, l.enforceError, l.releasedBy, MainRules.LocalTime(l.releasedAt), l.releaseReason);
                    _released.Rows[i].Tag = l;
                }
                _released.ClearSelection();
            }
            finally { _binding = false; }
            ShowInfo(status, locks.Count + " released lock(s)");
        }

        private async Task LoadActionsAsync(Label status)
        {
            long id;
            if (!long.TryParse(_lockId.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                ShowError(status, "Enter a lock number");
                return;
            }

            var detail = await _client.GetLock(id);
            if (IsClosed) return;
            _actions.Rows.Clear();
            var lockDto = detail == null ? null : detail.@lock;
            _lockSummary.Text = lockDto == null ? "" : "#" + lockDto.id + "  " + lockDto.type + "  " + lockDto.target + "  " + lockDto.status;
            var actions = detail == null || detail.actions == null ? new ActionDto[0] : detail.actions;
            foreach (var a in actions)
                _actions.Rows.Add(MainRules.LocalTime(a.createdAt), a.action, a.success ? "yes" : "no", a.attempt, a.error);
            _actions.ClearSelection();
            ShowInfo(status, actions.Length + " action(s)");
        }

        private async Task LoadDecisionsAsync(Label status)
        {
            var filter = new DecisionFilter
            {
                Uid = _uid.Text,
                Pn = _pn.Text,
                From = _from.Checked ? _from.Value.Date : (DateTime?)null,
                To = _to.Checked ? _to.Value.Date : (DateTime?)null
            };
            var problem = filter.Validate();
            if (problem != null)
            {
                ShowError(status, problem);
                return;
            }

            var found = await _client.GetDecisions(filter.UidValue, filter.PnValue, filter.FromUtc(), filter.ToUtc());
            if (IsClosed) return;
            var rows = DecisionFilter.NewestFirst(found);
            _decisionsLoaded = true;
            _decisions.Rows.Clear();
            foreach (var d in rows)
                _decisions.Rows.Add(MainRules.LocalTime(d.createdAt), d.method, d.result, d.uid, d.pn, d.mpn, d.supplier, d.linePath, d.machineId, d.feederId, d.message, d.matchedLockIds);
            _decisions.ClearSelection();
            ShowInfo(status, rows.Count + " row(s)" + (found.Count > rows.Count ? " (newest " + DecisionFilter.MaxRows + " of " + found.Count + ")" : ""));
        }
    }
}
