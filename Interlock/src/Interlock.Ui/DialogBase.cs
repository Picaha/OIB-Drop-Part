using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Interlock.Ui
{
    /// <summary>The common frame of the dialogs: modal, centered on the parent, fixed size, not in the taskbar.</summary>
    public abstract class DialogBase : Form
    {
        protected DialogBase()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            FormClosing += (s, e) => { if (ShouldBlockClose(Busy, _forced)) e.Cancel = true; };
            FormClosed += (s, e) => IsClosed = true;
        }

        private bool _forced;

        /// <summary>True while a call that changes data runs: the dialog cannot be cancelled, so its answer is never lost.</summary>
        protected bool Busy { get; private set; }

        public static bool ShouldBlockClose(bool busy, bool forced)
        {
            return busy && !forced;
        }

        /// <summary>Marks a call as running (or finished): blocks Cancel/Esc/close while it runs.</summary>
        protected void SetBusyState(bool busy)
        {
            Busy = busy;
            var cancel = CancelButton as Button;
            if (cancel != null) cancel.Enabled = !busy;
        }

        /// <summary>Used by the owner when the session ends; closes even while a call runs.</summary>
        public void CloseForced()
        {
            _forced = true;
            Close();
        }

        /// <summary>True once the dialog is closed; async continuations must not touch controls then.</summary>
        protected bool IsClosed { get; private set; }

        /// <summary>
        /// Runs <paramref name="body"/>; a failure becomes one line of text in <paramref name="status"/> (the already sanitised
        /// ApiException message, or a neutral text after logging). Never throws, never opens a message box.
        /// </summary>
        protected async Task Guarded(Func<Task> body, Label status)
        {
            try
            {
                await body();
            }
            catch (Exception ex)
            {
                if (!(ex is ApiException)) UiSafety.Log(ex);
                if (!IsClosed) ShowError(status, UiSafety.StatusFor(ex));
            }
        }

        protected static void ShowError(Label status, string text)
        {
            status.ForeColor = Color.Firebrick;
            status.Text = text ?? "";
        }

        protected static void ShowInfo(Label status, string text)
        {
            status.ForeColor = SystemColors.ControlText;
            status.Text = text ?? "";
        }
    }

    /// <summary>A single-line text box that hands Enter to the form's key handler instead of its default button (barcode scanners send Enter).</summary>
    public sealed class EnterTextBox : TextBox
    {
        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || base.IsInputKey(keyData);
        }
    }

    /// <summary>Read-only, copy-friendly grids.</summary>
    public static class GridKit
    {
        public static DataGridView ReadOnly(string[] headers, int[] widths)
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
                BackgroundColor = SystemColors.Window
            };
            for (var i = 0; i < headers.Length; i++)
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = headers[i], Width = widths[i], SortMode = DataGridViewColumnSortMode.NotSortable });
            return grid;
        }
    }
}
