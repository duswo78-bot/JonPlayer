using System;
using System.Diagnostics;
using System.IO;

namespace JonPlayer
{
    public static class Logger
    {
        private static readonly string LogFilePath;
        private static readonly object _lock = new object();

        static Logger()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                LogFilePath = Path.Combine(baseDir, "JonPlayer_Log.txt");
            }
            catch
            {
                LogFilePath = "JonPlayer_Log.txt";
            }
        }

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        
        public static void Error(string message, Exception? ex = null)
        {
            string fullMessage = ex == null ? message : $"{message}\nException: {ex}";
            Log("ERROR", fullMessage);
        }

        private static void Log(string level, string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logLine = $"[{timestamp}] [{level}] {message}";

            Debug.WriteLine(logLine);

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
                }
                catch
                {
                    // Fallback to purely Debug if file writing fails
                }
            }
        }
    }
}
