using System;
using System.Configuration;
using System.Windows.Forms;

namespace FaceScan
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppSettings settings;
            try
            {
                settings = AppSettings.Load(args);
            }
            catch (Exception ex) when (ex is ConfigurationErrorsException || ex is ArgumentException)
            {
                MessageBox.Show("FaceScan.exe.config: " + ex.Message, "FaceScan",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(new MainForm(settings));
        }
    }
}
