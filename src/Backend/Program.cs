using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualRemote
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        private const uint AttachParentProcess = 0xFFFFFFFFu;

        [STAThread]
        internal static int Main(string[] args)
        {
            bool selfTest = false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                }
            }
            if (selfTest)
            {
                // 无 UI：让 self-test 输出尽量出现在调用方控制台，同时写临时报告文件。
                AttachConsole(AttachParentProcess);
                try
                {
                    return SelfTest.Run();
                }
                catch (Exception ex)
                {
                    string path = Path.Combine(Path.GetTempPath(), "双端远程-self-test.log");
                    try
                    {
                        File.WriteAllText(path, "self-test 崩溃：" + ex + "\r\n", new System.Text.UTF8Encoding(false));
                    }
                    catch
                    {
                    }
                    try
                    {
                        Console.WriteLine("self-test 崩溃：" + ex);
                    }
                    catch
                    {
                    }
                    return 2;
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序异常：" + ex.Message, "双端远程", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
