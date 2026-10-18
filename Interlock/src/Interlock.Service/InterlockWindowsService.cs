using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace Interlock.Service
{
    /// <summary>
    /// The Windows service "Interlock". <see cref="OnStart"/> starts the composition on its own thread so the service control
    /// manager is answered at once. A start that fails sets a non-zero exit code, writes <c>startup-error.txt</c> and the event
    /// log (when the source exists), then stops the service, so the SCM shows the failure. <see cref="OnStop"/> and
    /// <see cref="OnShutdown"/> stop the composition; a stop that arrives during start-up cancels it.
    /// </summary>
    public sealed class InterlockWindowsService : ServiceBase
    {
        public const string Name = "Interlock";

        // The composition's stop budget is 25 s; the wait for the settings to load (before a composition exists) is part of the
        // same request. The SCM is asked for 35 s in all.
        private static readonly TimeSpan StarterWait = TimeSpan.FromSeconds(5);
        private const int StopWaitHintMs = 35000;

        private readonly object _gate = new object();
        private Composition _composition;
        private Thread _starter;
        private volatile bool _stopRequested;

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
            RequestTime();
            StopComposition();
        }

        protected override void OnShutdown()
        {
            RequestTime();
            StopComposition();
        }

        private void RequestTime()
        {
            try
            {
                RequestAdditionalTime(StopWaitHintMs);
            }
            catch (Exception)
            {
                // Not every state accepts a wait hint (for example during a system shutdown); stop anyway.
            }
        }

        private void StartComposition()
        {
            Composition composition = null;
            try
            {
                composition = new Composition(ServiceSettings.FromConfigFile());
                lock (_gate)
                {
                    if (_stopRequested)
                        return;
                    _composition = composition;
                }
                composition.Start();
            }
            catch (OperationCanceledException) when (_stopRequested)
            {
                // Stopped while starting: not a failure.
            }
            catch (Exception ex)
            {
                ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
                var file = StartupErrorWriter.Write(ex, StartupErrorWriter.DefaultFolders());
                WriteEventLog("Interlock could not start: " + ex.GetType().Name + ": " + ex.Message
                    + (file == null ? string.Empty : " Details: " + file), EventLogEntryType.Error);
                if (_stopRequested)
                    return;
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
            Composition composition;
            lock (_gate)
            {
                _stopRequested = true;
                composition = _composition;
                _composition = null;
            }

            if (composition == null)
            {
                // Still reading the settings: give it a moment; it sees the stop request and does not start.
                var starter = _starter;
                if (starter != null && starter != Thread.CurrentThread)
                    starter.Join(StarterWait);
                return;
            }

            // Safe while Start still runs on the starter thread: start-up notices the stop and closes what it opened.
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
                // No event source (New-EventLog -LogName Application -Source Interlock, once, as administrator).
            }
        }
    }
}
