using System;
using System.IO;
using System.Text;

namespace Interlock.Ui
{
    /// <summary>Turns any exception into text for the status strip, writes the error log and throttles the neutral message box.</summary>
    public static class UiSafety
    {
        public const string UnexpectedText = "Unexpected error - see log";
        public const string UnreachableText = "Service unreachable";

        /// <summary>The status-strip text for a failed refresh. Never contains stack text.</summary>
        public static string StatusFor(Exception ex)
        {
            var api = ex as ApiException;
            if (api == null) return UnexpectedText;
            return api.Status == 0 ? UnreachableText : api.Message;
        }

        /// <summary>Runs <paramref name="body"/>; returns null on success, otherwise the status text. Never throws.</summary>
        public static async System.Threading.Tasks.Task<string> RunAsync(Func<System.Threading.Tasks.Task> body)
        {
            try
            {
                await body();
                return null;
            }
            catch (Exception ex)
            {
                Log(ex);
                return StatusFor(ex);
            }
        }

        /// <summary>Runs <paramref name="body"/>; returns false (after logging) when it threw. Never throws.</summary>
        public static bool Run(Action body)
        {
            try
            {
                body();
                return true;
            }
            catch (Exception ex)
            {
                Log(ex);
                return false;
            }
        }

        /// <summary>Appends type, message and stack to ui-error.log (next to the exe, else %LocalAppData%\Interlock). Never throws.</summary>
        public static void Log(Exception ex)
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine;
            foreach (var dir in new[] { AppDomain.CurrentDomain.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Interlock") })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "ui-error.log"), text, Encoding.UTF8);
                    return;
                }
                catch (Exception) { /* try the next place */ }
            }
        }
    }

    /// <summary>Allows an action at most once per interval (for the "unexpected error" message box).</summary>
    public sealed class NoticeThrottle
    {
        private readonly TimeSpan _interval;
        private readonly Func<DateTime> _now;
        private DateTime? _last;

        public NoticeThrottle(TimeSpan interval, Func<DateTime> now = null)
        {
            _interval = interval;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public bool TryAcquire()
        {
            var t = _now();
            lock (this)
            {
                if (_last.HasValue && t - _last.Value < _interval) return false;
                _last = t;
                return true;
            }
        }
    }
}
