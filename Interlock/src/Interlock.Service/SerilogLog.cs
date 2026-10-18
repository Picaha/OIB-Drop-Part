using System;
using System.Globalization;
using System.IO;
using Interlock.Core.Ports;
using Serilog;
using Serilog.Core;

namespace Interlock.Service
{
    /// <summary>
    /// <see cref="ILog"/> over a Serilog rolling file: <c>interlock-&lt;yyyyMMdd&gt;.log</c> in the log directory, one file per day,
    /// 30 files kept, every event written through. An error's exception (type, message, stack) goes to the file only; the
    /// optional console echo (for <c>--console</c>) shows the message line. Messages are written as given, never parsed as a
    /// Serilog template.
    /// </summary>
    public sealed class SerilogLog : ILog, IDisposable
    {
        private const string FileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        private readonly Logger _file;
        private readonly bool _echo;
        private readonly object _consoleGate = new object();

        public SerilogLog(string logDir, bool echoToConsole)
        {
            if (string.IsNullOrWhiteSpace(logDir))
                throw new ArgumentException("A log directory is required.", nameof(logDir));

            Directory.CreateDirectory(logDir);
            _file = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(logDir, "interlock-.log"),
                    outputTemplate: FileTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: false,
                    buffered: false)
                .CreateLogger();

            _echo = echoToConsole;
        }

        public void Info(string m)
        {
            _file.Information("{M:l}", m);
            Echo("INF", m);
        }

        public void Warn(string m)
        {
            _file.Warning("{M:l}", m);
            Echo("WRN", m);
        }

        public void Error(string m, Exception ex)
        {
            _file.Error(ex, "{M:l}", m);
            Echo("ERR", ex == null ? m : m + " (" + ex.GetType().Name + ")");
        }

        public void Dispose()
        {
            _file.Dispose();
        }

        private void Echo(string level, string m)
        {
            if (!_echo)
                return;
            lock (_consoleGate)
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [" + level + "] " + m);
        }
    }
}
