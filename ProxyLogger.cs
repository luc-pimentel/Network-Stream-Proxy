using System;
using System.IO;
using System.Threading.Tasks;

public class ProxyLogger
{
    private readonly string _logPath;
    private readonly string _metricsPath;
    
    public class RequestMetrics
    {
        public string Method { get; set; }
        public string Url { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public long ContentLength { get; set; }
        public int StatusCode { get; set; }
        public double Duration => (EndTime - StartTime).TotalMilliseconds;
    }

    public ProxyLogger(string logPath, string metricsPath)
    {
        _logPath = logPath;
        _metricsPath = metricsPath;
        InitializeLogFiles();
    }

    private void InitializeLogFiles()
    {
        // Create headers for metrics CSV if it doesn't exist
        if (!File.Exists(_metricsPath))
        {
            File.WriteAllText(_metricsPath, 
                "Timestamp,Method,URL,Duration,ContentLength,StatusCode\n");
        }
    }

    public async Task LogRequest(RequestMetrics metrics)
    {
        // Detailed log entry
        var logEntry = $"[{metrics.StartTime:yyyy-MM-dd HH:mm:ss.fff}] " +
                      $"Method: {metrics.Method}, URL: {metrics.Url}, " +
                      $"Duration: {metrics.Duration}ms, " +
                      $"Size: {metrics.ContentLength} bytes, " +
                      $"Status: {metrics.StatusCode}";

        await File.AppendAllTextAsync(_logPath, logEntry + Environment.NewLine);

        // CSV metrics entry
        var metricsEntry = $"{metrics.StartTime:yyyy-MM-dd HH:mm:ss.fff}," +
                          $"{metrics.Method},{metrics.Url},{metrics.Duration}," +
                          $"{metrics.ContentLength},{metrics.StatusCode}\n";

        await File.AppendAllTextAsync(_metricsPath, metricsEntry);
    }

    public async Task LogError(string message, Exception ex)
    {
        var logEntry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] " +
                      $"ERROR: {message}\n" +
                      $"Exception: {ex.Message}\n" +
                      $"StackTrace: {ex.StackTrace}";

        await File.AppendAllTextAsync(_logPath, logEntry + Environment.NewLine);
    }
}