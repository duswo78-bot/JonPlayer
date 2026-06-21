using System;
using System.IO;
namespace JonPlayer {
    public partial class App : System.Windows.Application {
        protected override void OnStartup(System.Windows.StartupEventArgs e) {
            try { base.OnStartup(e); } 
            catch (Exception ex) { 
                string msg = ex.ToString(); 
                while(ex.InnerException != null) { ex = ex.InnerException; msg += "--- INNER ---" + ex.ToString(); }
                File.WriteAllText("crash.txt", msg);
                Environment.Exit(1);
            }
        }
    }
}
