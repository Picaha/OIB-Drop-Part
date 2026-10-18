using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Interlock.Core.Model;
using Interlock.Core.Ports;

namespace Interlock.Service.Data
{
    /// <summary>
    /// The lock spool on disk: locks created while the database was down, one JSON object per line (UTF-8, no BOM), written
    /// with <see cref="DataContractJsonSerializer"/>. <see cref="Append"/> is durable when it returns. <see cref="ReadAll"/>
    /// skips blank lines and lines it cannot read, so one damaged line never hides the others. Enums are stored as their
    /// database text and dates as ISO 8601 strings (<c>DateTime.ToString("o")</c>); the serializer's own date format fails
    /// for an unset date in time zones east of UTC. The row id is not stored: a spooled lock has none yet.
    /// Safe to use from several threads.
    /// </summary>
    public sealed class JsonlLockSpool : ILockSpool
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(SpoolLine));
        private static readonly byte[] LineBreak = { (byte)'\n' };

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly ILog _log;

        public JsonlLockSpool(string path, ILog log = null)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A spool path is required.", nameof(path));

            _path = Path.GetFullPath(path);
            _log = log;
        }

        /// <summary>Writes the lock as one line, creating the directory when it is missing, and flushes it to disk before it returns.</summary>
        public void Append(LockRecord l)
        {
            if (l == null)
                throw new ArgumentNullException(nameof(l));

            var record = Serialize(l);

            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
                {
                    var torn = EndsInTornLine(file);
                    file.Seek(0, SeekOrigin.End);
                    // A crash can leave a half-written last line; the new record must start on a line of its own.
                    if (torn)
                        file.Write(LineBreak, 0, LineBreak.Length);
                    file.Write(record, 0, record.Length);
                    file.Flush(true);
                }
            }
        }

        /// <summary>The spooled locks in the order they were appended, each with <c>Id == 0</c>. A missing file is an empty spool.</summary>
        public IList<LockRecord> ReadAll()
        {
            var locks = new List<LockRecord>();

            lock (_gate)
            {
                try
                {
                    using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(file, Utf8, true))
                    {
                        string line;
                        var number = 0;
                        while ((line = reader.ReadLine()) != null)
                        {
                            number++;
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            try
                            {
                                locks.Add(Parse(line));
                            }
                            catch (Exception ex)
                            {
                                if (_log != null)
                                    _log.Warn("Skipped line " + number + " of the lock spool " + _path + ": " + ex.Message);
                            }
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            return locks;
        }

        /// <summary>Deletes the spool file. A missing file is fine.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
        }

        private static bool EndsInTornLine(FileStream file)
        {
            if (file.Length == 0)
                return false;
            file.Seek(-1, SeekOrigin.End);
            return file.ReadByte() != '\n';
        }

        private static byte[] Serialize(LockRecord l)
        {
            using (var buffer = new MemoryStream())
            {
                Serializer.WriteObject(buffer, ToLine(l));
                buffer.Write(LineBreak, 0, LineBreak.Length);
                return buffer.ToArray();
            }
        }

        private static LockRecord Parse(string line)
        {
            SpoolLine parsed;
            using (var buffer = new MemoryStream(Utf8.GetBytes(line)))
                parsed = (SpoolLine)Serializer.ReadObject(buffer);
            if (parsed == null)
                throw new FormatException("The line holds no lock.");
            return FromLine(parsed);
        }

        private static SpoolLine ToLine(LockRecord l)
        {
            return new SpoolLine
            {
                Type = DbEnum.ToDb(l.Type),
                Target = l.Target,
                TargetNorm = l.TargetNorm,
                Lanes = l.Lanes.HasValue ? DbEnum.ToDb(l.Lanes.Value) : null,
                LinePath = l.LinePath,
                Reason = l.Reason,
                Source = DbEnum.ToDb(l.Source),
                CreatedBy = l.CreatedBy,
                CreatedAtUtc = FormatDate(l.CreatedAtUtc),
                Status = DbEnum.ToDb(l.Status),
                ReleasedBy = l.ReleasedBy,
                ReleasedAtUtc = l.ReleasedAtUtc.HasValue ? FormatDate(l.ReleasedAtUtc.Value) : null,
                ReleaseReason = l.ReleaseReason,
                EnforceState = DbEnum.ToDb(l.EnforceState),
                EnforceError = l.EnforceError
            };
        }

        private static LockRecord FromLine(SpoolLine s)
        {
            if (string.IsNullOrWhiteSpace(s.Target) || string.IsNullOrWhiteSpace(s.TargetNorm))
                throw new FormatException("The lock has no target.");

            return new LockRecord
            {
                Id = 0,
                Type = DbEnum.ParseLockType(s.Type),
                Target = s.Target,
                TargetNorm = s.TargetNorm,
                Lanes = s.Lanes == null ? (Lanes?)null : DbEnum.ParseLanes(s.Lanes),
                LinePath = s.LinePath,
                Reason = s.Reason,
                Source = DbEnum.ParseSource(s.Source),
                CreatedBy = s.CreatedBy,
                CreatedAtUtc = ParseDate(s.CreatedAtUtc),
                Status = DbEnum.ParseStatus(s.Status),
                ReleasedBy = s.ReleasedBy,
                ReleasedAtUtc = s.ReleasedAtUtc == null ? (DateTime?)null : ParseDate(s.ReleasedAtUtc),
                ReleaseReason = s.ReleaseReason,
                EnforceState = DbEnum.ParseEnforceState(s.EnforceState),
                EnforceError = s.EnforceError
            };
        }

        private static string FormatDate(DateTime value)
        {
            return value.ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseDate(string text)
        {
            return DateTime.ParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        /// <summary>The shape of one spool line. Optional members are left out of the line when they are empty.</summary>
        [DataContract]
        private sealed class SpoolLine
        {
            [DataMember(Name = "type")]
            public string Type { get; set; }

            [DataMember(Name = "target")]
            public string Target { get; set; }

            [DataMember(Name = "targetNorm")]
            public string TargetNorm { get; set; }

            [DataMember(Name = "lanes", EmitDefaultValue = false)]
            public string Lanes { get; set; }

            [DataMember(Name = "linePath", EmitDefaultValue = false)]
            public string LinePath { get; set; }

            [DataMember(Name = "reason", EmitDefaultValue = false)]
            public string Reason { get; set; }

            [DataMember(Name = "source")]
            public string Source { get; set; }

            [DataMember(Name = "createdBy", EmitDefaultValue = false)]
            public string CreatedBy { get; set; }

            [DataMember(Name = "createdAtUtc")]
            public string CreatedAtUtc { get; set; }

            [DataMember(Name = "status")]
            public string Status { get; set; }

            [DataMember(Name = "releasedBy", EmitDefaultValue = false)]
            public string ReleasedBy { get; set; }

            [DataMember(Name = "releasedAtUtc", EmitDefaultValue = false)]
            public string ReleasedAtUtc { get; set; }

            [DataMember(Name = "releaseReason", EmitDefaultValue = false)]
            public string ReleaseReason { get; set; }

            [DataMember(Name = "enforceState")]
            public string EnforceState { get; set; }

            [DataMember(Name = "enforceError", EmitDefaultValue = false)]
            public string EnforceError { get; set; }
        }
    }
}
