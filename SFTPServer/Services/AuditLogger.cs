using System.Collections.Concurrent;

namespace SFTPServer.Services
{
    /// <summary>
    /// Audit Logger for SFTP operations
    /// Logs all file operations for compliance and security
    /// </summary>
    public class AuditLogger : IDisposable
    {
        private readonly string _logPath;
        private readonly bool _enabled;
        private readonly BlockingCollection<string> _logQueue = new(1000);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _writerTask;
        private bool _disposed;

        public AuditLogger(string logPath, bool enabled = true)
        {
            _logPath = logPath;
            _enabled = enabled;

            if (_enabled)
            {
                _writerTask = Task.Run(WriteLogEntries);
            }
            else
            {
                _writerTask = Task.CompletedTask;
            }
        }

        public void LogConnection(string sessionId, string username, string remoteAddress, bool success)
        {
            var action = success ? "CONNECTED" : "CONNECTION_FAILED";
            Log(sessionId, username, action, remoteAddress, null);
        }

        public void LogDisconnection(string sessionId, string username)
        {
            Log(sessionId, username, "DISCONNECTED", null, null);
        }

        public void LogAuthentication(string sessionId, string username, string method, bool success)
        {
            var action = success ? "AUTH_SUCCESS" : "AUTH_FAILED";
            Log(sessionId, username, action, method, null);
        }

        public void LogFileRead(string sessionId, string username, string filePath, long bytesRead)
        {
            Log(sessionId, username, "FILE_READ", filePath, $"bytes={bytesRead}");
        }

        public void LogFileWrite(string sessionId, string username, string filePath, long bytesWritten)
        {
            Log(sessionId, username, "FILE_WRITE", filePath, $"bytes={bytesWritten}");
        }

        public void LogFileDelete(string sessionId, string username, string filePath)
        {
            Log(sessionId, username, "FILE_DELETE", filePath, null);
        }

        public void LogDirectoryCreate(string sessionId, string username, string dirPath)
        {
            Log(sessionId, username, "DIR_CREATE", dirPath, null);
        }

        public void LogDirectoryDelete(string sessionId, string username, string dirPath)
        {
            Log(sessionId, username, "DIR_DELETE", dirPath, null);
        }

        public void LogDirectoryList(string sessionId, string username, string dirPath)
        {
            Log(sessionId, username, "DIR_LIST", dirPath, null);
        }

        public void LogRename(string sessionId, string username, string oldPath, string newPath)
        {
            Log(sessionId, username, "RENAME", oldPath, $"to={newPath}");
        }

        public void LogError(string sessionId, string username, string operation, string error)
        {
            Log(sessionId, username, "ERROR", operation, error);
        }

        private void Log(string sessionId, string username, string action, string? target, string? details)
        {
            if (!_enabled || _disposed) return;

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var entry = $"{timestamp}|{sessionId}|{username}|{action}|{target ?? "-"}|{details ?? "-"}";
            
            try
            {
                _logQueue.Add(entry);
            }
            catch (InvalidOperationException)
            {
                // Queue is completed, ignore
            }
        }

        private async Task WriteLogEntries()
        {
            try
            {
                await using var writer = new StreamWriter(_logPath, append: true);
                
                foreach (var entry in _logQueue.GetConsumingEnumerable(_cts.Token))
                {
                    await writer.WriteLineAsync(entry);
                    await writer.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIT] Error writing log: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logQueue.CompleteAdding();
            _cts.Cancel();
            
            try
            {
                _writerTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }

            _cts.Dispose();
            _logQueue.Dispose();
        }
    }
}
