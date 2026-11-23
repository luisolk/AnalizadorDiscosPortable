using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("Analizador de Discos e Instalaciones Portable (Administrador)")]
[assembly: AssemblyDescription("Analizador visual de discos, archivos, carpetas y todo lo instalado mediante mapa de cuadros (Treemap) con borrado de Administrador")]
[assembly: AssemblyConfiguration("Portable Single-File Executable")]
[assembly: AssemblyCompany("Antigravity Portable Tools")]
[assembly: AssemblyProduct("AnalizadorDiscosPortable")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace AnalizadorDiscosPortable
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                if (Environment.OSVersion.Version.Major >= 6)
                {
                    SetProcessDPIAware();
                }
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool skipElevate = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--no-elevate", StringComparison.OrdinalIgnoreCase))
                    {
                        skipElevate = true;
                    }
                }
            }

            if (!skipElevate && !MainForm.IsAdministrator())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.Arguments = "--elevated";
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // If user canceled UAC prompt, continue opening so the app still opens
                }
            }

            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Error inesperado en la interfaz:\n" + e.Exception.Message,
                    "Analizador de Discos Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
        }
    }
}
