using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>
    /// Creates a lock. The target box takes barcode-scanner input: Enter moves to the reason and never submits. Stations and
    /// lines are loaded when the dialog is shown; if that fails the dialog stays usable for the types that do not need them.
    /// </summary>
    public sealed class NewLockForm : DialogBase
    {
        private const string WholePlant = "Whole plant";

        private readonly ApiClient _client;
        private readonly ComboBox _type = new ComboBox();
        private readonly EnterTextBox _target = new EnterTextBox();
        private readonly ComboBox _station = new ComboBox();
        private readonly RadioButton _left = new RadioButton { Text = "Left", AutoSize = true };
        private readonly RadioButton _right = new RadioButton { Text = "Right", AutoSize = true };
        private readonly RadioButton _both = new RadioButton { Text = "Both", AutoSize = true, Checked = true };
        private readonly ComboBox _line = new ComboBox();
        private readonly TextBox _reason = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();
        private readonly TableLayoutPanel _table = new TableLayoutPanel();
        private readonly Timer _closeTimer = new Timer { Interval = 1500 };

        private readonly Label _targetLabel = new Label { Text = "Target", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _stationLabel = new Label { Text = "Station", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _lanesLabel = new Label { Text = "Lanes", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _lineLabel = new Label { Text = "Line", AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly FlowLayoutPanel _lanes = new FlowLayoutPanel { AutoSize = true, WrapContents = false };

        public NewLockForm(ApiClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            _client = client;

            Text = "New lock";
            ClientSize = new Size(460, 330);

            _type.DropDownStyle = ComboBoxStyle.DropDownList;
            _type.Items.AddRange(NewLockModel.Types);
            _type.SelectedIndex = 0;
            _type.Width = 200;

            _target.Width = 320;
            _station.DropDownStyle = ComboBoxStyle.DropDownList;
            _station.Width = 320;
            _line.DropDownStyle = ComboBoxStyle.DropDownList;
            _line.Items.Add(WholePlant);
            _line.SelectedIndex = 0;
            _line.Width = 320;
            _lanes.Controls.AddRange(new Control[] { _left, _right, _both });

            _reason.Multiline = true;
            _reason.WordWrap = true;
            _reason.ScrollBars = ScrollBars.Vertical;
            _reason.Size = new Size(320, 70);
            _reason.MaxLength = ReasonForm.MaxReasonLength;

            _table.Dock = DockStyle.Top;
            _table.AutoSize = true;
            _table.ColumnCount = 2;
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _table.Padding = new Padding(10, 10, 10, 0);
            AddRow(new Label { Text = "Type", AutoSize = true, Anchor = AnchorStyles.Left }, _type);
            AddRow(_targetLabel, _target);
            AddRow(_stationLabel, _station);
            AddRow(_lanesLabel, _lanes);
            AddRow(_lineLabel, _line);
            AddRow(new Label { Text = "Reason", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left }, _reason);

            _status.SetBounds(12, 252, 436, 36);
            _ok.Text = "Create";
            _ok.SetBounds(264, 292, 88, 28);
            _cancel.Text = "Cancel";
            _cancel.DialogResult = DialogResult.Cancel;
            _cancel.SetBounds(360, 292, 88, 28);

            Controls.AddRange(new Control[] { _table, _status, _ok, _cancel });
            AcceptButton = _ok;
            CancelButton = _cancel;

            _type.SelectedIndexChanged += (s, e) => ApplyVisibility();
            _target.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                _reason.Focus();
            };
            _closeTimer.Tick += (s, e) =>
            {
                _closeTimer.Stop();
                if (!IsClosed) DialogResult = DialogResult.OK;
            };
            FormClosed += (s, e) => _closeTimer.Dispose();
            _ok.Click += async (s, e) => await Guarded(SubmitAsync, _status);
            Shown += async (s, e) => await Guarded(LoadListsAsync, _status);
            ApplyVisibility();
        }

        private void AddRow(Control label, Control field)
        {
            var row = _table.RowCount++;
            _table.RowStyles.Add(new System.Windows.Forms.RowStyle(SizeType.AutoSize));
            _table.Controls.Add(label, 0, row);
            _table.Controls.Add(field, 1, row);
        }

        private NewLockModel ReadModel()
        {
            return new NewLockModel
            {
                Type = _type.SelectedItem as string,
                Target = _target.Text,
                StationPath = _station.SelectedItem as string,
                Lanes = _left.Checked ? "LEFT" : _right.Checked ? "RIGHT" : "BOTH",
                LinePath = _line.SelectedIndex <= 0 ? null : _line.SelectedItem as string,
                Reason = _reason.Text
            };
        }

        private void ApplyVisibility()
        {
            var m = ReadModel();
            _targetLabel.Visible = _target.Visible = m.ShowTarget;
            _stationLabel.Visible = _station.Visible = m.ShowStationPicker;
            _lanesLabel.Visible = _lanes.Visible = m.ShowLanes;
            _lineLabel.Visible = _line.Visible = m.ShowLinePicker;
        }

        private async Task LoadListsAsync()
        {
            var problems = new List<string>();

            try
            {
                var stations = await _client.GetStations();
                if (IsClosed) return;
                foreach (var s in stations)
                    if (s != null && !string.IsNullOrWhiteSpace(s.stationPath)) _station.Items.Add(s.stationPath);
                if (_station.Items.Count > 0) _station.SelectedIndex = 0;
            }
            catch (Exception ex) { problems.Add("stations: " + Problem(ex)); }

            try
            {
                var lines = await _client.GetLines();
                if (IsClosed) return;
                foreach (var l in lines)
                    if (!string.IsNullOrWhiteSpace(l)) _line.Items.Add(l);
            }
            catch (Exception ex) { problems.Add("lines: " + Problem(ex)); }

            if (problems.Count > 0)
                ShowError(_status, "Could not load " + string.Join("; ", problems));
        }

        private static string Problem(Exception ex)
        {
            if (!(ex is ApiException)) UiSafety.Log(ex);
            return UiSafety.StatusFor(ex);
        }

        private async Task SubmitAsync()
        {
            var model = ReadModel();
            var problem = model.Validate();
            if (problem != null)
            {
                ShowError(_status, problem);
                return;
            }

            SetBusy(true);
            ShowInfo(_status, "Working...");
            CreateResult result;
            try
            {
                result = await _client.Create(model.ToDto());
            }
            catch
            {
                if (!IsClosed) SetBusy(false);
                throw;
            }

            if (IsClosed) return;
            SetBusy(false);
            if (result.Created)
            {
                DialogResult = DialogResult.OK;
                return;
            }

            // The same lock already exists: say so inline, keep the inputs frozen and close shortly after.
            var id = result.Lock == null ? "?" : result.Lock.id.ToString(CultureInfo.InvariantCulture);
            ShowInfo(_status, "Already locked (#" + id + ")");
            _table.Enabled = false;
            _ok.Enabled = false;
            _closeTimer.Start();
        }

        private void SetBusy(bool busy)
        {
            SetBusyState(busy);
            _table.Enabled = !busy;
            _ok.Enabled = !busy;
        }
    }
}
