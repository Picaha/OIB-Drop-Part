using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace Interlock.Service
{
    /// <summary>
    /// The Windows service "Interlock". <see cref="OnStart"/> starts the composition on its own thread so the service control
    /// manager is answered at once; a start that fails sets a non-zero exit code, writes the event log and stops the service, so
    /// the SCM shows the failure. <see cref="OnStop"/> and <see cref="OnShutdown"/> stop the composition.
    /// </summary>
    public sealed class InterlockWindowsService : ServiceBase
    {
        public const string Name = "Interlock";

        private readonly object _gate = new object();
        private Composition _composition;
        private Thread _starter;

        public InterlockWindowsService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            _starter = new Thread(StartComposition) { IsBackground = true, Name = "Interlock start-up" };
            _starter.Start();
        }

        protected override void OnStop()
        {
            RequestAdditionalTime(30000);
            StopComposition();
        }

        protected override void OnShutdown()
        {
            StopComposition();
        }

        private void StartComposition()
        {
            try
            {
                var composition = new Composition(ServiceSettings.FromConfigFile());
                lock (_gate)
                    _composition = composition;
                composition.Start();
            }
            catch (Exception ex)
            {
                ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
                WriteEventLog("Interlock could not start: " + ex.GetType().Name + ": " + ex.Message, EventLogEntryType.Error);
                try
                {
                    Stop();
                }
                catch (Exception)
                {
                    // The SCM may already be stopping the service.
                }
            }
        }

        private void StopComposition()
        {
            var starter = _starter;
            if (starter != null && starter != Thread.CurrentThread)
                starter.Join(TimeSpan.FromSeconds(60));

            Composition composition;
            lock (_gate)
            {
                composition = _composition;
                _composition = null;
            }
            if (composition != null)
                composition.Stop();
        }

        private void WriteEventLog(string message, EventLogEntryType type)
        {
            try
            {
                EventLog.WriteEntry(message, type);
            }
            catch (Exception)
            {
                // No event source (it needs administrator rights to create); the log file has the details.
            }
        }
    }
}
