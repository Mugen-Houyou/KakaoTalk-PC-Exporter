using System.Threading;
using System.Windows;

namespace KakaoPcLogger.Services
{
    public sealed class ClipboardService
    {
        public string? TryReadText(int retries = 6, int delayMs = 30)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                        return Clipboard.GetText();
                    return string.Empty;
                }
                catch
                {
                    Thread.Sleep(delayMs);
                }
            }

            return null;
        }
    }
}
