#region Copyright

// // ASM OIB - Copyright (C) ASM AS GmbH & Co. KG
// // All rights reserved.
// //
// // The software and associated documentation supplied hereunder are
// the proprietary information of ASMand are supplied subject to license terms.

#endregion

#region using

using System;
using System.Threading;
using System.Windows.Forms;

#endregion

namespace OIB.Tutorial
{
    internal static class Program
    {
        private static Mutex _mutex;

        /// <summary>
        ///     The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            // Prevent duplicate instances (e.g. Task Scheduler re-launching while already running)
            string appPath = Application.ExecutablePath.Replace("\\", "_");
            string mutexName = string.Format("Global\\MonitoringPartUsage_SingleInstance_{0}", appPath.GetHashCode().ToString("X"));
            bool createdNew;
            _mutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance is already running – exit silently
                MessageBox.Show("Monitoring Part Usage (R23) is already running.", 
                    "Monitoring Part Usage R23", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());

            // Release mutex on exit
            _mutex.ReleaseMutex();
        }
    }
}