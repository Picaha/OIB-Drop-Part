using System;
using System.Configuration;
using System.Windows.Forms;

namespace Interlock.Ui
{
    internal static class Program
    {
        private const string DefaultBaseUrl = "http://localhost:8090/interlock/v1";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var url = ConfigurationManager.AppSettings["Interlock.ApiBaseUrl"];
            if (string.IsNullOrWhiteSpace(url)) url = DefaultBaseUrl;

            ApiClient client;
            try
            {
                client = new ApiClient(url);
            }
            catch (ArgumentException)
            {
                MessageBox.Show("The setting Interlock.ApiBaseUrl in Interlock.Ui.exe.config is not a valid http or https address:\n" + url,
                    "Interlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (client)
            {
                string notice = null;
                while (true)
                {
                    Session session;
                    using (var login = new LoginForm(client) { Notice = notice })
                    {
                        if (login.ShowDialog() != DialogResult.OK) return;
                        session = login.Session;
                    }

                    using (var main = new MainForm(client, session))
                    {
                        Application.Run(main);
                        if (!main.ReturnToLogin) return;
                        notice = main.LoginNotice;
                    }
                }
            }
        }
    }
}
