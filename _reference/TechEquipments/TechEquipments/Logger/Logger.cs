using System;
using System.IO;
using System.Reflection;

namespace TechLogger
{
    public static class Logger
    {
        public static readonly string logFilePath = $"{Assembly.GetExecutingAssembly().Location}.log.txt";
        //private static readonly string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

        public static void Info(string message) => WriteLog("INFO", message);
        public static void Debug(string message) => WriteLog("DEBUG", message);
        public static void Error(string message) => WriteLog("ERROR", message);
        public static void Trace(string message) => WriteLog("TRACE", message);
        public static void Warning(string message) => WriteLog("WARNING", message);

        private static void WriteLog(string level, string message)
        {
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss:ff} [{level}] {message}";
            try
            {
                File.AppendAllText(logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Если лог-файл занят/нет доступа — не падаем
            }
        }
    }
}
