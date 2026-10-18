using System;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Interlock.Core.Errors;
using Interlock.Core.Model;
using Interlock.Core.Ports;
using Interlock.Core.Security;
using Interlock.Service.Data;

namespace Interlock.Service
{
    internal static class Program
    {
        private const int Ok = 0;
        private const int Failed = 1;
        private const int Usage = 2;

        private const string UsageText =
@"Interlock.Service.exe                       run as the Windows service ""Interlock"" (started by the SCM)
Interlock.Service.exe --console             run in this console until Enter or Ctrl+C
Interlock.Service.exe --add-user <username> <OPERATOR|QA|ENGINEER|ADMIN>
                                            create a user; asks for the PIN twice (not shown)
Interlock.Service.exe --help                this text";

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                ServiceBase.Run(new InterlockWindowsService());
                return Ok;
            }

            var command = args[0].ToLowerInvariant();
            if ((command == "--help" || command == "-h" || command == "/?") && args.Length == 1)
            {
                Console.WriteLine(UsageText);
                return Ok;
            }
            if (command == "--console" && args.Length == 1)
                return RunConsole();
            if (command == "--add-user" && args.Length == 3)
                return AddUser(args[1], args[2], Console.In, Console.IsInputRedirected);

            Console.Error.WriteLine("Unknown or incomplete arguments.");
            Console.Error.WriteLine(UsageText);
            return Usage;
        }

        private static int RunConsole()
        {
            ServiceSettings settings;
            try
            {
                settings = ServiceSettings.FromConfigFile();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Configuration error: " + ex.Message);
                return Failed;
            }

            using (var stop = new ManualResetEvent(false))
            using (var composition = new Composition(new ProductionDeps(settings, true)))
            {
                ConsoleCancelEventHandler onCancel = (s, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };
                Console.CancelKeyPress += onCancel;
                try
                {
                    try
                    {
                        composition.Start();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Start-up failed: " + ex.GetType().Name + ": " + ex.Message);
                        return Failed;
                    }

                    Console.WriteLine("Interlock is running. Press Enter or Ctrl+C to stop.");
                    var reader = new Thread(() =>
                    {
                        try
                        {
                            Console.ReadLine(); // a line or the end of the input
                        }
                        catch (Exception)
                        {
                            // No console input: wait for Ctrl+C.
                            return;
                        }
                        stop.Set();
                    }) { IsBackground = true };
                    reader.Start();
                    stop.WaitOne();

                    Console.WriteLine("Stopping...");
                    composition.Stop();
                    return Ok;
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }
            }
        }

        /// <summary>Creates a user straight in the database (no login needed: the console is the bootstrap path). Never prints the PIN.</summary>
        internal static int AddUser(string username, string roleText, TextReader input, bool inputRedirected)
        {
            Role role;
            try
            {
                role = DbEnum.ParseRole((roleText ?? string.Empty).Trim().ToUpperInvariant());
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Unknown role '" + roleText + "'. Use OPERATOR, QA, ENGINEER or ADMIN.");
                return Usage;
            }

            try
            {
                var settings = ServiceSettings.FromConfigFile();
                var pin = ReadPin("PIN: ", input, inputRedirected);
                var again = ReadPin("PIN again: ", input, inputRedirected);
                if (pin == null || again == null || !string.Equals(pin, again, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("The two PINs differ (or no PIN was given). No user created.");
                    return Failed;
                }

                var db = new NpgsqlDb(settings.ConnectionString, settings.DbSchema);
                var clock = new SystemClock();
                var auth = new AuthService(new NpgsqlUserRepository(db), new SessionStore(clock, TimeSpan.FromHours(8)), clock);
                auth.CreateInitialUser(username, role, pin);
                Console.WriteLine("User '" + username.Trim() + "' created with role " + DbEnum.ToDb(role) + ".");
                return Ok;
            }
            catch (InterlockException ex)
            {
                Console.Error.WriteLine("No user created: " + ex.Message);
                return Failed;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("No user created: " + ex.GetType().Name + ": " + ex.Message);
                return Failed;
            }
        }

        private static string ReadPin(string prompt, TextReader input, bool inputRedirected)
        {
            Console.Write(prompt);
            if (inputRedirected)
            {
                var line = input.ReadLine();
                Console.WriteLine();
                return line;
            }

            var pin = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (pin.Length > 0)
                        pin.Length--;
                    continue;
                }
                if (key.KeyChar != '\0')
                    pin.Append(key.KeyChar);
            }
            Console.WriteLine();
            return pin.ToString();
        }
    }
}
