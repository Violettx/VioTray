namespace VioTray.Core;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null)
                line += Environment.NewLine + ex;

            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                File.AppendAllText(
                    Path.Combine(Paths.LogDir, $"{now:yyyy-MM-dd}.log"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }

    public static void Cleanup(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(Paths.LogDir))
                return;

            var deadline = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.EnumerateFiles(Paths.LogDir, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < deadline)
                        File.Delete(file);
                }
                catch
                {
                    // 单个文件删不掉就跳过
                }
            }
        }
        catch (Exception ex)
        {
            Error("清理日志失败", ex);
        }
    }
}
