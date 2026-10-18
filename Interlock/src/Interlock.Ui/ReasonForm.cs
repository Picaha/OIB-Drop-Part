using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Interlock.Ui
{
    /// <summary>
    /// Asks for a required reason (at most 500 characters). With a <c>submit</c> callback the dialog runs it on OK and stays open,
    /// showing the error inline, when it fails (for example 403 or 409 from a release); without one it just returns OK.
    /// </summary>
    public sealed class ReasonForm : DialogBase
    {
        public const int MaxReasonLength = 500;

        private readonly Func<string, Task> _submit;
        private readonly TextBox _reason = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();

        public ReasonForm(string title, Func<string, Task> submit)
        {
            _submit = submit;
            Text = title ?? "Reason";
            ClientSize = new Size(440, 190);

            var label = new Label { Text = "Reason (required)", Left = 12, Top = 10, Width = 400 };
            _reason.SetBounds(12, 32, 416, 80);
            _reason.Multiline = true;
            _reason.WordWrap = true;
            _reason.ScrollBars = ScrollBars.Vertical;
            _reason.MaxLength = MaxReasonLength;
            _status.SetBounds(12, 118, 416, 34);
            _ok.Text = "OK";
            _ok.SetBounds(248, 152, 88, 28);
            _cancel.Text = "Cancel";
            _cancel.DialogResult = DialogResult.Cancel;
            _cancel.SetBounds(340, 152, 88, 28);
            _ok.Click += async (s, e) => await Guarded(SubmitAsync, _status);

            Controls.AddRange(new Control[] { label, _reason, _status, _ok, _cancel });
            AcceptButton = _ok;
            CancelButton = _cancel;
        }

        /// <summary>The trimmed text.</summary>
        public string Reason { get { return _reason.Text.Trim(); } }

        private async Task SubmitAsync()
        {
            var reason = Reason;
            if (reason.Length == 0)
            {
                ShowError(_status, "Enter a reason");
                return;
            }

            if (_submit == null)
            {
                DialogResult = DialogResult.OK;
                return;
            }

            SetBusy(true);
            ShowInfo(_status, "Working...");
            var done = false;
            await Guarded(async () => { await _submit(reason); done = true; }, _status);
            if (IsClosed) return;
            SetBusy(false);
            if (done) DialogResult = DialogResult.OK;
        }

        private void SetBusy(bool busy)
        {
            SetBusyState(busy);
            _reason.Enabled = !busy;
            _ok.Enabled = !busy;
            if (!busy) _reason.Focus();
        }
    }
}
