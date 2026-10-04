using Aimmy2.Class;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Visuality;

namespace Other
{
    internal class LogManager
    {
        public enum LogLevel
        {
            Info,
            Warning,
            Error
        }

        public static void Log(LogLevel lvl, string message, bool notifyUser = false, int waitingTime = 4000)
        {
            if (notifyUser)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    new NoticeBar(message, waitingTime).Show();
                });
            }
#if DEBUG
            Debug.WriteLine(message);
#endif
            if (Dictionary.toggleState["Debug Mode"])
            {
                string logFilepath = "debug.txt";
                using StreamWriter w = new(logFilepath, true);
                string lvlPrefix = lvl.ToString().ToUpper();
                w.WriteLine($"[{DateTime.Now}] [{lvlPrefix}]: {message}");
            }
        }
    }
}
