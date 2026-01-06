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
            RootDirectory ??= GetDefaultRootDirectory();
            AuditLogPath ??= GetDefaultAuditLogPath();

            EnsureDirectoriesExist();
        }

        /// <summary>
        /// Gets the default root directory based on the current platform and available permissions
        /// </summary>
        private static string GetDefaultRootDirectory()
        {
            // Try user home directory first (works on all platforms without elevated permissions)
            var homeDir = GetUserHomeDirectory();
            if (!string.IsNullOrEmpty(homeDir))
            {
                return Path.Combine(homeDir, ".sftp", "root");
            }

            // Fallback to temp directory if home is not available
            return Path.Combine(Path.GetTempPath(), "sftp", "root");
        }

        /// <summary>
        /// Gets the default audit log path based on the current platform and available permissions
        /// </summary>
        private static string GetDefaultAuditLogPath()
        {
            var homeDir = GetUserHomeDirectory();
            if (!string.IsNullOrEmpty(homeDir))
            {
                return Path.Combine(homeDir, ".sftp", "logs", "audit.log");
            }

            return Path.Combine(Path.GetTempPath(), "sftp", "logs", "audit.log");
        }

        /// <summary>
        /// Gets the user's home directory in a cross-platform way
        /// </summary>
        private static string GetUserHomeDirectory()
        {
            // Try HOME environment variable first (Linux/macOS)
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            {
                return home;
            }

            // Try USERPROFILE for Windows
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(userProfile) && Directory.Exists(userProfile))
            {
                return userProfile;
            }

            // Try .NET's built-in method
            var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(specialFolder) && Directory.Exists(specialFolder))
            {
                return specialFolder;
            }

            // Last resort: try ApplicationData
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData) && Directory.Exists(appData))
            {
                return appData;
            }

            return string.Empty;
        }

        /// <summary>
        /// Ensures required directories exist
        /// </summary>
        public void EnsureDirectoriesExist()
        {
            TryCreateDirectory(RootDirectory);

            var logDir = Path.GetDirectoryName(AuditLogPath);
            if (!string.IsNullOrEmpty(logDir))
            {
                TryCreateDirectory(logDir);
            }
        }

        /// <summary>
        /// Tries to create a directory, handling permission errors gracefully
        /// </summary>
        private static bool TryCreateDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Permission denied - directory cannot be created
                return false;
            }
            catch (IOException)
            {
                // IO error - path may be invalid or inaccessible
                return false;
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
