using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WpadManager.App
{
    // Single portable entry point. With no arguments it opens the WinForms GUI;
    // with arguments it runs in CLI mode (for automation / CI). Because the exe is
    // built as /target:winexe it has no console of its own, so in CLI mode we attach
    // to the parent console to make Console.WriteLine visible.
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                return Cli.Run(args);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
