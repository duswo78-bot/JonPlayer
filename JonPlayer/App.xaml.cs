using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace JonPlayer
{
    /// <summary>
    /// Application entry point for JonPlayer.
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private const string PipeName = "JonPlayer_SingleInstancePipe";

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "JonPlayerApp_Mutex";
            _mutex = new Mutex(true, appName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running. Send args via named pipe.
                if (e.Args.Length > 0)
                {
                    try
                    {
                        using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                        {
                            client.Connect(2000);
                            using (var writer = new StreamWriter(client))
                            {
                                writer.WriteLine(string.Join("|", e.Args));
                                writer.Flush();
                            }
                        }
                    }
                    catch { }
                }
                Current.Shutdown();
                return;
            }

            // This is the first instance. Start named pipe server to listen for new files.
            Task.Run(ListenForArgs);

            base.OnStartup(e);
        }

        private void ListenForArgs()
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server))
                        {
                            string? argsStr = reader.ReadLine();
                            if (!string.IsNullOrEmpty(argsStr))
                            {
                                string[] args = argsStr.Split('|');
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    if (System.Windows.Application.Current.MainWindow is MainWindow win)
                                    {
                                        if (win.WindowState == WindowState.Minimized)
                                            win.WindowState = WindowState.Normal;
                                        win.Activate();
                                        win.LoadExternalFiles(args);
                                    }
                                });
                            }
                        }
                    }
                }
                catch
                {
                    Thread.Sleep(500); // Backoff on error
                }
            }
        }

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
