using System;
using System.Drawing;
using System.Windows.Forms;

namespace Interlock.Ui
{
    /// <summary>Username and PIN. Enter submits. The result is <see cref="Session"/> and a token set on the client.</summary>
    public sealed class LoginForm : Form
    {
        private readonly ApiClient _client;
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _pin = new TextBox();
        private readonly Button _login = new Button();
        private readonly Label _message = new Label();

        public LoginForm(ApiClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            _client = client;

            Text = "Interlock - Sign in";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(340, 190);

            var userLabel = new Label { Text = "Username", Left = 16, Top = 18, Width = 90 };
            _user.SetBounds(112, 14, 210, 24);
            var pinLabel = new Label { Text = "PIN", Left = 16, Top = 54, Width = 90 };
            _pin.SetBounds(112, 50, 210, 24);
            _pin.UseSystemPasswordChar = true;
            _message.SetBounds(16, 88, 306, 44);
            _message.ForeColor = Color.Firebrick;
            _login.Text = "Sign in";
            _login.SetBounds(222, 144, 100, 28);
            _login.Click += async (s, e) => await SubmitAsync();

            Controls.AddRange(new Control[] { userLabel, _user, pinLabel, _pin, _message, _login });
            AcceptButton = _login;
        }

        /// <summary>Set after a successful sign-in.</summary>
        public Session Session { get; private set; }

        /// <summary>Shown above the fields, for example "Session expired".</summary>
        public string Notice
        {
            get { return _message.Text; }
            set { _message.Text = value ?? ""; }
        }

        private async System.Threading.Tasks.Task SubmitAsync()
        {
            var username = _user.Text.Trim();
            if (username.Length == 0 || _pin.Text.Length == 0)
            {
                _message.Text = "Enter the username and the PIN";
                return;
            }

            SetBusy(true);
            _message.Text = "";
            try
            {
                _client.Token = null;
                var dto = await _client.Login(username, _pin.Text);
                Session = Session.FromLogin(username, dto);
                _client.Token = dto.token;
                _pin.Clear();
                DialogResult = DialogResult.OK;
            }
            catch (ApiException ex)
            {
                _message.Text = ex.Message;
                Failed();
            }
            catch (InvalidOperationException ex)
            {
                _message.Text = ex.Message;
                Failed();
            }
            catch (Exception ex)
            {
                UiSafety.Log(ex);
                _message.Text = "Sign-in failed";
                Failed();
            }
            finally
            {
                if (!IsDisposed)
                {
                    SetBusy(false);
                    if (DialogResult != DialogResult.OK) _pin.Focus();
                }
            }
        }

        private void Failed()
        {
            _pin.Clear();
        }

        private void SetBusy(bool busy)
        {
            _user.Enabled = !busy;
            _pin.Enabled = !busy;
            _login.Enabled = !busy;
            UseWaitCursor = busy;
        }
    }
}
