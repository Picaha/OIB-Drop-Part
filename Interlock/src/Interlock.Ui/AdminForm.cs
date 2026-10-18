using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Interlock.Core.Api;

namespace Interlock.Ui
{
    /// <summary>
    /// Users and API keys (ADMIN only). The service still checks every call. PINs and a freshly created API key are only
    /// held in controls and are cleared when the tab is left or the dialog closes; neither is logged or stored.
    /// </summary>
    public sealed class AdminForm : DialogBase
    {
        private readonly ApiClient _client;
        private readonly KeyOnceDisplay _keyOnce = new KeyOnceDisplay();
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };

        // Users
        private readonly DataGridView _usersGrid;
        private readonly Panel _userEditor = new Panel { Dock = DockStyle.Bottom, Height = 150 };
        private readonly TextBox _username = new TextBox();
        private readonly ComboBox _role = new ComboBox();
        private readonly CheckBox _active = new CheckBox { Text = "Active", Checked = true, AutoSize = true };
        private readonly TextBox _pin = new TextBox { UseSystemPasswordChar = true };
        private readonly Label _pinHint = new Label();
        private readonly Button _newUser = new Button { Text = "New user" };
        private readonly Button _saveUser = new Button { Text = "Save" };
        private readonly Label _usersStatus = new Label { Dock = DockStyle.Bottom, Height = 22 };

        // API keys
        private readonly DataGridView _keysGrid;
        private readonly Panel _keyEditor = new Panel { Dock = DockStyle.Bottom, Height = 170 };
        private readonly TextBox _keyName = new TextBox();
        private readonly CheckBox _canUnlock = new CheckBox { Text = "Can unlock", AutoSize = true };
        private readonly Button _createKey = new Button { Text = "Create key" };
        private readonly Button _toggleKey = new Button { Text = "Activate / deactivate selected" };
        private readonly Panel _keyShown = new Panel { Visible = false };
        private readonly TextBox _keyBox = new TextBox { ReadOnly = true };
        private readonly Button _copyKey = new Button { Text = "Copy" };
        private readonly Label _keysStatus = new Label { Dock = DockStyle.Bottom, Height = 22 };

        private IList<UserDto> _users = new UserDto[0];
        private IList<ApiKeyDto> _keys = new ApiKeyDto[0];
        private bool _binding;
        private bool _keyPicked;
        private bool _editingNew = true;

        public AdminForm(ApiClient client, Session session)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (!AdminRules.CanOpen(session)) throw new UnauthorizedAccessException("Administration needs the ADMIN role");
            _client = client;

            Text = "Administration";
            ClientSize = new Size(760, 520);

            _usersGrid = GridKit.ReadOnly(new[] { "Username", "Role", "Active" }, new[] { 260, 160, 100 });
            _usersGrid.MultiSelect = false;
            _keysGrid = GridKit.ReadOnly(new[] { "Id", "Name", "Can unlock", "Active" }, new[] { 60, 300, 100, 100 });
            _keysGrid.MultiSelect = false;

            BuildUsersTab();
            BuildKeysTab();
            Controls.Add(_tabs);

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 30 };
            Controls.Add(close);
            CancelButton = close;

            _usersGrid.SelectionChanged += (s, e) => { if (!_binding) LoadEditor(SelectedUser()); };
            _newUser.Click += (s, e) => BeginNewUser();
            _saveUser.Click += async (s, e) => await Run(_userEditor, _usersStatus, SaveUserAsync, true);
            _createKey.Click += async (s, e) => await Run(_keyEditor, _keysStatus, CreateKeyAsync, true);
            _toggleKey.Click += async (s, e) => await Run(_keyEditor, _keysStatus, ToggleKeyAsync, true);
            _keysGrid.CellClick += (s, e) => { if (e.RowIndex >= 0) _keyPicked = true; };
            _keysGrid.KeyUp += (s, e) => { if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) _keyPicked = true; };
            _copyKey.Click += (s, e) => CopyKey();
            _tabs.SelectedIndexChanged += (s, e) => ClearSecrets();
            FormClosing += (s, e) => ClearSecrets();
            Shown += async (s, e) =>
            {
                await Run(_userEditor, _usersStatus, () => ReloadUsersAsync(null));
                await Run(_keyEditor, _keysStatus, ReloadKeysAsync);
            };
        }

        // ---- layout ------------------------------------------------------------------------------------------------

        private void BuildUsersTab()
        {
            _role.DropDownStyle = ComboBoxStyle.DropDownList;
            _role.Items.AddRange(AdminRules.Roles);
            _role.SelectedIndex = 0;

            _username.SetBounds(90, 10, 200, 24);
            _role.SetBounds(380, 10, 130, 24);
            _active.Location = new Point(530, 12);
            _pin.SetBounds(90, 44, 200, 24);
            _pinHint.SetBounds(300, 46, 440, 20);
            _newUser.SetBounds(10, 80, 100, 28);
            _saveUser.SetBounds(120, 80, 100, 28);
            _userEditor.Controls.AddRange(new Control[]
            {
                new Label { Text = "Username", Left = 10, Top = 14, Width = 76 }, _username,
                new Label { Text = "Role", Left = 330, Top = 14, Width = 46 }, _role, _active,
                new Label { Text = "PIN", Left = 10, Top = 48, Width = 76 }, _pin, _pinHint,
                _newUser, _saveUser
            });

            var page = new TabPage("Users");
            page.Controls.Add(_usersGrid);
            page.Controls.Add(_userEditor);
            page.Controls.Add(_usersStatus);
            _tabs.TabPages.Add(page);
            BeginNewUser();
        }

        private void BuildKeysTab()
        {
            _keyName.SetBounds(90, 10, 200, 24);
            _canUnlock.Location = new Point(310, 12);
            _createKey.SetBounds(430, 8, 110, 28);
            _toggleKey.SetBounds(550, 8, 190, 28);

            var warning = new Label
            {
                Text = KeyOnceDisplay.Warning,
                ForeColor = Color.Firebrick,
                Font = new Font(Font, FontStyle.Bold),
                Left = 0, Top = 0, Width = 700, Height = 20
            };
            _keyBox.SetBounds(0, 26, 600, 24);
            _copyKey.SetBounds(610, 24, 90, 28);
            _keyShown.SetBounds(10, 50, 730, 60);
            _keyShown.Controls.AddRange(new Control[] { warning, _keyBox, _copyKey });

            _keyEditor.Controls.AddRange(new Control[]
            {
                new Label { Text = "Key name", Left = 10, Top = 14, Width = 76 }, _keyName, _canUnlock, _createKey, _toggleKey, _keyShown
            });

            var page = new TabPage("API keys");
            page.Controls.Add(_keysGrid);
            page.Controls.Add(_keyEditor);
            page.Controls.Add(_keysStatus);
            _tabs.TabPages.Add(page);
        }

        // ---- plumbing ----------------------------------------------------------------------------------------------

        /// <summary>Runs one call with the editor disabled (no double submit); errors are shown on the tab's status line.</summary>
        private async Task Run(Control editor, Label status, Func<Task> body, bool mutating = false)
        {
            if (!editor.Enabled || IsClosed) return;
            editor.Enabled = false;
            if (mutating) SetBusyState(true);
            try { await Guarded(body, status); }
            finally
            {
                if (mutating) SetBusyState(false);
                if (!IsClosed) editor.Enabled = true;
            }
        }

        private void ClearSecrets()
        {
            _pin.Clear();
            _keyOnce.Clear();
            _keyBox.Clear();
            _keyShown.Visible = false;
        }

        // ---- users -------------------------------------------------------------------------------------------------

        private UserDto SelectedUser()
        {
            return _usersGrid.SelectedRows.Count == 0 ? null : _usersGrid.SelectedRows[0].Tag as UserDto;
        }

        private void BeginNewUser()
        {
            _editingNew = true;
            _username.ReadOnly = false;
            _username.Clear();
            _role.SelectedIndex = 0;
            _active.Checked = true;
            _pin.Clear();
            _pinHint.Text = "Required for a new user (at least " + AdminRules.MinPin + " characters)";
            if (_usersGrid.SelectedRows.Count > 0) { _binding = true; _usersGrid.ClearSelection(); _binding = false; }
            _username.Focus();
        }

        private void LoadEditor(UserDto user)
        {
            if (user == null) return;
            _editingNew = false;
            _username.ReadOnly = true;
            _username.Text = user.username;
            var index = _role.Items.IndexOf(user.role);
            _role.SelectedIndex = index; // an unknown role selects nothing: Save is blocked until one is chosen
            _active.Checked = user.active != false;
            _pin.Clear();
            _pinHint.Text = "Leave empty to keep the PIN";
        }

        private async Task ReloadUsersAsync(string select)
        {
            var users = await _client.GetUsers();
            if (IsClosed) return;
            _users = users;
            _binding = true;
            try
            {
                _usersGrid.Rows.Clear();
                foreach (var u in users)
                {
                    var i = _usersGrid.Rows.Add(u.username, u.role, u.active == false ? "no" : "yes");
                    _usersGrid.Rows[i].Tag = u;
                }
                _usersGrid.ClearSelection();
                if (select != null)
                    foreach (DataGridViewRow row in _usersGrid.Rows)
                        if (string.Equals(((UserDto)row.Tag).username, select, StringComparison.OrdinalIgnoreCase)) { row.Selected = true; break; }
            }
            finally { _binding = false; }
            if (select != null) LoadEditor(SelectedUser());
        }

        private async Task SaveUserAsync()
        {
            var problem = AdminRules.ValidateUser(_editingNew, _username.Text, _role.SelectedItem as string, _pin.Text, _users.Select(u => u.username));
            if (problem != null)
            {
                ShowError(_usersStatus, problem);
                return;
            }

            var dto = AdminRules.ToUserDto(_username.Text, _role.SelectedItem as string, _active.Checked, _pin.Text);
            ShowInfo(_usersStatus, "Saving...");
            var saved = await _client.SaveUser(dto);
            _pin.Clear();
            if (IsClosed) return;
            ShowInfo(_usersStatus, "Saved " + dto.username);
            await ReloadUsersAsync(saved != null && saved.username != null ? saved.username : dto.username);
        }

        // ---- API keys ----------------------------------------------------------------------------------------------

        private ApiKeyDto SelectedKey()
        {
            // Only a row the user chose counts: the grid may select row 0 by itself when it is first shown.
            return !_keyPicked || _keysGrid.SelectedRows.Count == 0 ? null : _keysGrid.SelectedRows[0].Tag as ApiKeyDto;
        }

        private async Task ReloadKeysAsync()
        {
            var keys = await _client.GetApiKeys();
            if (IsClosed) return;
            _keys = keys;
            var selectedId = SelectedKey() == null ? (long?)null : SelectedKey().id;
            _keysGrid.Rows.Clear();
            foreach (var k in keys)
            {
                var i = _keysGrid.Rows.Add(k.id, k.name, k.canUnlock ? "yes" : "no", k.active ? "yes" : "no");
                _keysGrid.Rows[i].Tag = k;
            }
            _keysGrid.CurrentCell = null;
            _keysGrid.ClearSelection();
            _keyPicked = false;
            if (selectedId.HasValue)
                foreach (DataGridViewRow row in _keysGrid.Rows)
                    if (((ApiKeyDto)row.Tag).id == selectedId.Value) { row.Selected = true; _keyPicked = true; break; }
        }

        private async Task CreateKeyAsync()
        {
            var problem = AdminRules.ValidateKeyName(_keyName.Text);
            if (problem != null)
            {
                ShowError(_keysStatus, problem);
                return;
            }

            ClearSecrets();
            ShowInfo(_keysStatus, "Creating...");
            var created = await _client.CreateApiKey(new ApiKeyDto { name = _keyName.Text.Trim(), canUnlock = _canUnlock.Checked, active = true });
            if (IsClosed) return;

            _keyOnce.Show(created == null ? null : created.key);
            if (_keyOnce.HasKey)
            {
                _keyBox.Text = _keyOnce.Key;
                _keyShown.Visible = true;
                _keyBox.Focus();
                _keyBox.SelectAll();
                ShowInfo(_keysStatus, "Key '" + _keyName.Text.Trim() + "' created");
            }
            else
            {
                ShowError(_keysStatus, "The service did not return a key");
            }
            _keyName.Clear();
            _canUnlock.Checked = false;
            await ReloadKeysAsync();
        }

        private async Task ToggleKeyAsync()
        {
            var key = SelectedKey();
            if (key == null)
            {
                ShowError(_keysStatus, "Select a key first");
                return;
            }

            ShowInfo(_keysStatus, "Saving...");
            await _client.SetApiKeyActive(key.id, !key.active);
            if (IsClosed) return;
            ShowInfo(_keysStatus, "Key '" + key.name + "' is now " + (key.active ? "inactive" : "active"));
            await ReloadKeysAsync();
        }

        private void CopyKey()
        {
            if (!_keyOnce.HasKey) return;
            try
            {
                Clipboard.SetText(_keyOnce.Key);
                ShowInfo(_keysStatus, "Copied to the clipboard");
            }
            catch (ExternalException)
            {
                ShowError(_keysStatus, "The clipboard is busy. Select the key and copy it with Ctrl+C.");
            }
        }
    }
}
