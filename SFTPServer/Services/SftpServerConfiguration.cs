using System.Net;

namespace SFTPServer.Services
{
    /// <summary>
    /// Enterprise SFTP Server configuration settings
    /// </summary>
    public class SftpServerConfiguration
    {
        /// <summary>
        /// IP address to listen on (default: null = all interfaces)
        /// Example: "127.0.0.1" for localhost only, "192.168.1.100" for specific IP
        /// </summary>
        public string? ListenAddress { get; set; } = null;

        /// <summary>
        /// Gets the IPAddress to bind to. Returns IPAddress.Any (all interfaces) if ListenAddress is null or empty.
        /// </summary>
        public IPAddress GetListenIPAddress()
        {
            if (string.IsNullOrWhiteSpace(ListenAddress))
                return IPAddress.Any;

            return IPAddress.Parse(ListenAddress);
        }

        /// <summary>
        /// SSH server port (default: 22)
        /// </summary>
        public int Port { get; set; } = 22;

        /// <summary>
        /// SFTP root directory where files are served from
        /// </summary>
        public string RootDirectory { get; set; } = null!;

        /// <summary>
        /// Maximum concurrent connections
        /// </summary>
        public int MaxConnections { get; set; } = 100;

        /// <summary>
        /// Enable debug logging
        /// </summary>
        public bool EnableLogging { get; set; } = true;

        /// <summary>
        /// Connection timeout in seconds (0 = no timeout)
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Maximum upload file size in MB (0 = unlimited)
        /// </summary>
        public int MaxUploadSizeMB { get; set; } = 0;

        /// <summary>
        /// Enable audit logging for file operations
        /// </summary>
        public bool EnableAuditLog { get; set; } = true;

        /// <summary>
        /// Audit log file path
        /// </summary>
        public string AuditLogPath { get; set; } = null!;

        /// <summary>
        /// Server banner message (must start with "SSH-2.0-")
        /// </summary>
        public string ServerBanner { get; set; } = "SSH-2.0-SFTP_1.0";

        /// <summary>
        /// Ephemeral RSA host key
        /// </summary>
        public string RsaHostKey { get; set; } = null!;

        /// <summary>
        /// Ephemeral ECDSA (nistp256) host key
        /// </summary>
        public string EcdsaP256HostKey { get; set; } = null!;

        public SftpServerConfiguration()
        {
            // Set defaults only if not already set
            RootDirectory ??= OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SftpRoot")
                : "/var/sftp";

            AuditLogPath ??= OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SftpLogs", "audit.log")
                : "/var/log/sftp/audit.log";

            EnsureDirectoriesExist();
        }

        /// <summary>
        /// Ensures required directories exist
        /// </summary>
        public void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(RootDirectory))
            {
                Directory.CreateDirectory(RootDirectory);
            }

            var logDir = Path.GetDirectoryName(AuditLogPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
    }

    /// <summary>
    /// User account for authentication
    /// </summary>
    public class UserAccount
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string HomeDirectory { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool CanUpload { get; set; } = true;
        public bool CanDownload { get; set; } = true;
        public bool CanDelete { get; set; } = true;
        public bool CanCreateDirectory { get; set; } = true;
        public long MaxUploadBytes { get; set; } = 0; // 0 = unlimited

        public UserAccount(string username, string password, string homeDirectory)
        {
            Username = username;
            Password = password;
            HomeDirectory = homeDirectory;
        }
    }
}
