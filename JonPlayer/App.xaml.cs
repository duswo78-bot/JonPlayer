using System.Windows;

namespace JonPlayer
{
    /// <summary>
    /// Application entry point for JonPlayer.
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public App()
        {
            // 전역 예외 처리기 (UI 스레드)
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            // 전역 예외 처리기 (백그라운드 스레드 등 전체)
            System.AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            
            // Task에서 발생한 관찰되지 않은 예외 처리기
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            System.Windows.MessageBox.Show($"예기치 않은 오류가 발생했습니다.\n{e.Exception.Message}\n\n자세한 내용은 crash_log.txt를 확인하세요.", "JonPlayer Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            e.Handled = true; // 강제 종료 방지 시도
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is System.Exception ex)
            {
                LogCrash(ex);
                System.Windows.MessageBox.Show($"치명적인 오류가 발생하여 프로그램이 종료될 수 있습니다.\n{ex.Message}\n\n자세한 내용은 crash_log.txt를 확인하세요.", "JonPlayer Fatal Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            e.SetObserved(); // 강제 종료 방지
        }

        private void LogCrash(System.Exception ex)
        {
            try
            {
                string logPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                string logMessage = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
                System.IO.File.AppendAllText(logPath, logMessage);
            }
            catch { }
        }
    }
}
