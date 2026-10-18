using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Interlock.Service
{
    /// <summary>
    /// Leaves a trace of a failed start-up when the log file may not exist yet (for example a configuration error, which is read
    /// before Serilog starts) and the event log has no source: <c>startup-error.txt</c> with the time, the exception type,
    /// message and stack. It is written to the first folder that accepts it: next to the exe, then <c>%ProgramData%\Interlock</c>.
    /// The configuration exceptions never carry the connection string, so the file holds no secrets.
    /// </summary>
    internal static class StartupErrorWriter
    {
        public const string FileName = "startup-error.txt";

        public static IList<string> DefaultFolders()
        {
            return new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Interlock")
            };
        }

        /// <summary>The path written, or null when no folder accepted the file. Never throws.</summary>
        public static string Write(Exception ex, IList<string> folders)
        {
            var text = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append(" Interlock start-up failed").AppendLine()
                .Append(ex == null ? "(no exception)" : ex.ToString()).AppendLine()
                .ToString();

            foreach (var folder in folders ?? new string[0])
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, FileName);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    return path;
                }
                catch (Exception)
                {
                    // Try the next folder.
                }
            }
            return null;
        }
    }
}
