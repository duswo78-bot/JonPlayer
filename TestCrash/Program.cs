using System;
using System.IO;

namespace TestCrash
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                var w = new JonPlayer.MainWindow();
            }
            catch (Exception ex)
            {
                string msg = ex.ToString();
                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    msg += "\n--- INNER ---\n" + ex.ToString();
                }
                File.WriteAllText("real_crash.txt", msg);
            }
        }
    }
}
