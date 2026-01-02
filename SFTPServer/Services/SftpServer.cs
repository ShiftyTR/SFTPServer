using FxSsh;
using FxSsh.Services;

namespace SFTPServer.Services
{
    /// <summary>
    /// SFTP Server implementation using FxSsh library
    /// Supports SFTP file transfer compatible with FileZilla, WinSCP, and other SSH clients
    /// </summary>
    public class SftpServer
    {
        private readonly SftpServerConfiguration _config;
        private SshServer? _sshServer;
        private readonly UserManager _userManager;
        private int _activeConnections = 0;
        private AuditLogger? _audit;

        public SftpServer(SftpServerConfiguration? config = null, UserManager? userManager = null)
        {
            _config = config ?? new SftpServerConfiguration();
            _userManager = userManager ?? new UserManager();
            InitializeDirectories();
        }

        /// <summary>
        /// Get the UserManager for external user management
        /// </summary>
        public UserManager UserManager => _userManager;

        /// <summary>
        /// Initialize user home directories
        /// </summary>
        private void InitializeDirectories()
        {
            foreach (var user in _userManager.GetAllUsers())
            {
                if (!Directory.Exists(user.HomeDirectory))
                {
                    Directory.CreateDirectory(user.HomeDirectory);
                    if (_config.EnableLogging)
                        Console.WriteLine($"  Created home directory: {user.HomeDirectory}");
                }
            }
        }

        /// <summary>
        /// Start the SFTP server
        /// </summary>
        public void Start(CancellationToken cancellationToken)
        {
            if (_config.EnableLogging)
            {
                Console.WriteLine("╔══════════════════════════════════════════════╗");
                Console.WriteLine("║          SFTP Server Configuration           ║");
                Console.WriteLine("╚══════════════════════════════════════════════╝");
                Console.WriteLine();
                Console.WriteLine($"Listen Address:     {(string.IsNullOrWhiteSpace(_config.ListenAddress) ? "All interfaces" : _config.ListenAddress)}");
                Console.WriteLine($"Port:               {_config.Port}");
                Console.WriteLine($"Root Directory:     {_config.RootDirectory}");
                Console.WriteLine($"Max Connections:    {_config.MaxConnections}");
                Console.WriteLine($"Users:              {string.Join(", ", _userManager.GetAllUsers().Select(u => u.Username))}");
                Console.WriteLine();
            }


            try
            {
                // Setup audit logger if enabled
                _audit = _config.EnableAuditLog ? new AuditLogger(_config.AuditLogPath, true) : null;
                // Use provided ephemeral host keys from configuration if set; otherwise generate new ones per start
                var rsa2048BitPem = string.IsNullOrWhiteSpace(_config.RsaHostKey)
                    ? KeyGenerator.GenerateRsaKeyPem(2048)
                    : _config.RsaHostKey;
                var ecdsap256Pem = string.IsNullOrWhiteSpace(_config.EcdsaP256HostKey)
                    ? KeyGenerator.GenerateECDsaKeyPem("nistp256")
                    : _config.EcdsaP256HostKey;

                // Create SSH server with IP and port configuration
                var startingInfo = new StartingInfo(_config.GetListenIPAddress(), _config.Port, _config.ServerBanner);
                _sshServer = new SshServer(startingInfo);
                _sshServer.AddHostKey("rsa-sha2-256", rsa2048BitPem);
                _sshServer.AddHostKey("rsa-sha2-512", rsa2048BitPem);
                _sshServer.AddHostKey("ecdsa-sha2-nistp256", ecdsap256Pem);

                // Register events BEFORE starting
                _sshServer.ConnectionAccepted += Server_ConnectionAccepted;
                _sshServer.ConnectionEstablished += Server_ConnectionEstablished;

                // Start server
                _sshServer.Start();
                if (_config.EnableLogging)
                {
                    Console.WriteLine("╔════════════════════════════════════════════╗");
                    Console.WriteLine("║   ✓ SSH Server Started Successfully        ║");
                    Console.WriteLine("╚════════════════════════════════════════════╝");
                    Console.WriteLine();
                    var listenAddr = string.IsNullOrWhiteSpace(_config.ListenAddress) ? "All interfaces" : _config.ListenAddress;
                    Console.WriteLine($"Listening on {listenAddr}:{_config.Port}");
                    Console.WriteLine("Waiting for SFTP client connections...");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"Error starting server: {ex.Message}");
            }
            Task.Run(async () =>
            {
                while (true)
                {
                    if(cancellationToken.IsCancellationRequested)
                    {
                        Stop();
                        break;
                    }
                    await Task.Delay(10000);
                }
            });
        }

        /// <summary>
        /// Stop the SFTP server
        /// </summary>
        public void Stop()
        {
            _sshServer?.Stop();
            if (_config.EnableLogging)
                Console.WriteLine("SFTP Server stopped");
            _audit?.Dispose();
        }

        private void Server_ConnectionAccepted(object sender, Session e)
        {
            var current = Interlocked.Increment(ref _activeConnections);
            var timestamp = DateTime.Now.ToString("HH:mm:ss");

            // Enforce max connections
            if (current > _config.MaxConnections)
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"[{timestamp}] [LIMIT] ✗ Connection rejected: max {_config.MaxConnections} reached");
                try
                {
                    e.Disconnect();
                }
                catch { /* swallow */ }
                Interlocked.Decrement(ref _activeConnections);
                return;
            }

            // Generate session ID...
            var sessionId = "SESSION";
            if (e.SessionId != null && e.SessionId.Length > 0)
            {
                var hexId = BitConverter.ToString(e.SessionId).Replace("-", "");
                sessionId = hexId.Length > 12 ? hexId.Substring(0, 12) : hexId;
            }
            else
            {
                sessionId = Guid.NewGuid().ToString().Substring(0, 12);
            }

            if (_config.EnableLogging)
            {
                Console.WriteLine($"[{timestamp}] [{sessionId}] ✓ Client connected (Active: {current})");
                Console.WriteLine($"[{timestamp}] [{sessionId}]   Establishing connection...");
            }

            // Audit connection (remote address not available here, pass '-')
            _audit?.LogConnection(sessionId, "-", "-", true);

            // Subscribe to ServiceRegistered IMMEDIATELY
            e.ServiceRegistered += (s, service) =>
            {
                Session_ServiceRegistered(s, service, sessionId);
            };

            // Decrement active count on close/error
            e.Disconnected += (s, args) =>
            {
                var left = Interlocked.Decrement(ref _activeConnections);
                if (_config.EnableLogging)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Connection closed (Active: {left})");
                // Audit disconnection
                _audit?.LogDisconnection(sessionId, "-");
            };
        }

        private void Server_ConnectionEstablished(object sender, Session e)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");

            // Generate session ID - handle null case
            var sessionId = "SESSION";
            if (e.SessionId != null && e.SessionId.Length > 0)
            {
                var hexId = BitConverter.ToString(e.SessionId).Replace("-", "");
                sessionId = hexId.Length > 12 ? hexId.Substring(0, 12) : hexId;
            }
            else
            {
                sessionId = Guid.NewGuid().ToString().Substring(0, 12);
            }

            if (_config.EnableLogging)
            {
                Console.WriteLine($"[{timestamp}] [{sessionId}] ✓ Connection established");
                Console.WriteLine($"[{timestamp}] [{sessionId}]   Waiting for services...");
            }

            // Register service handlers AFTER connection established
            e.ServiceRegistered += (s, service) => Session_ServiceRegistered(s, service, sessionId);
        }

        private void Session_ServiceRegistered(object sender, SshService service, string sessionId)
        {
            var serviceName = service.GetType().Name;

            if (_config.EnableLogging)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Service: {serviceName}");
            }

            // Handle UserAuthService (NOT UserauthService - capital A!)
            if (serviceName == "UserAuthService")
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   → UserAuthService detected");

                try
                {
                    // Cast to UserAuthService and subscribe directly
                    var userAuthService = (UserAuthService)service;
                    userAuthService.UserAuth += (s, e) =>
                    {
                        HandleUserAuth(s, e, sessionId);
                    };

                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   ✓ UserAuth handler registered");
                }
                catch (Exception ex)
                {
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   ✗ Error: {ex.Message}");
                }
            }
            // Handle ConnectionService
            else if (serviceName == "ConnectionService")
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   → ConnectionService detected");

                try
                {
                    var connectionService = (ConnectionService)service;
                    connectionService.CommandOpened += (s, e) =>
                    {
                        HandleCommandOpened(s, e, sessionId);
                    };

                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   ✓ CommandOpened handler registered");
                }
                catch (Exception ex)
                {
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   ✗ Error: {ex.Message}");
                }
            }
        }

        // Handler for UserAuth event (typed version)
        private void HandleUserAuth(object sender, UserAuthArgs e, string sessionId)
        {
            try
            {
                string username = e.Username ?? string.Empty;
                string authMethod = e.AuthMethod ?? "unknown";
                string password = e.Password ?? string.Empty;

                if (_config.EnableLogging)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Auth: user='{username}', method='{authMethod}'");
                }

                e.Result = false; // Default to reject

                // Handle password authentication
                if (authMethod == "password")
                {
                    if (_userManager.AuthenticatePassword(username, password, out var user))
                    {
                        e.Result = true;
                        if (_config.EnableLogging)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✓ Authentication successful for user '{username}'");
                        _audit?.LogAuthentication(sessionId, username, "password", true);
                    }
                    else
                    {
                        if (_config.EnableLogging)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ Invalid credentials for user '{username}'");
                        _audit?.LogAuthentication(sessionId, username, "password", false);
                    }
                }
                // Handle publickey authentication
                else if (authMethod == "publickey")
                {
                    var user = _userManager.GetUser(username);
                    if (user != null && user.IsEnabled)
                    {
                        e.Result = true;
                        if (_config.EnableLogging)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✓ Public key auth successful for user '{username}'");
                        _audit?.LogAuthentication(sessionId, username, "publickey", true);
                    }
                    else
                    {
                        if (_config.EnableLogging)
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ User '{username}' not found or disabled");
                        _audit?.LogAuthentication(sessionId, username, "publickey", false);
                    }
                }
                else
                {
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ Auth method '{authMethod}' not supported");
                    _audit?.LogError(sessionId, username, "auth", $"method={authMethod} not supported");
                }
            }
            catch (Exception ex)
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Error in HandleUserAuth: {ex.Message}");
            }
        }

        // Handler for CommandOpened event (typed version)
        private void HandleCommandOpened(object sender, CommandRequestedArgs e, string sessionId)
        {
            try
            {
                string? shellType = e.ShellType;
                string? commandText = e.CommandText;

                if (_config.EnableLogging)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Command: type='{shellType}', cmd='{commandText}'");
                }

                // Handle SFTP subsystem - MUST set Agreed = true!
                if (shellType == "subsystem" && commandText == "sftp")
                {
                    e.Agreed = true;  // Accept the SFTP subsystem request

                    // Get authenticated user from AttachedUserAuthArgs
                    string? username = e.AttachedUserAuthArgs?.Username;
                    UserAccount? user = null;
                    string userRoot = _config.RootDirectory;

                    if (!string.IsNullOrEmpty(username))
                    {
                        user = _userManager.GetUser(username);
                        if (user != null)
                        {
                            userRoot = user.HomeDirectory;
                        }
                    }

                    if (_config.EnableLogging)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✓ SFTP subsystem AGREED for user '{username}'");
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   Root: {userRoot}");
                        if (user != null)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}]   Permissions: Upload={user.CanUpload}, Download={user.CanDownload}, Delete={user.CanDelete}");
                        }
                    }

                    // Start SFTP subsystem handler with UserManager for dynamic permission checking
                    var sftpHandler = new SftpSubsystem(
                        e.Channel,
                        userRoot,
                        _userManager,
                        _config.EnableLogging,
                        _audit,
                        sessionId,
                        username ?? "-",
                        _config.MaxUploadSizeMB > 0 ? (long)_config.MaxUploadSizeMB * 1024L * 1024L : 0,
                        _config.ConnectionTimeoutSeconds
                    );

                    if (_config.EnableLogging)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✓ SFTP handler started");
                    }
                }
                else if (shellType == "shell")
                {
                    e.Agreed = false;
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ Shell REJECTED (SFTP-only mode)");
                }
                else if (shellType == "exec")
                {
                    e.Agreed = false;
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ Exec REJECTED (SFTP-only mode)");
                }
                else
                {
                    e.Agreed = false;
                    if (_config.EnableLogging)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] ✗ Unknown command type: {shellType}");
                }
            }
            catch (Exception ex)
            {
                if (_config.EnableLogging)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{sessionId}] Error in HandleCommandOpened: {ex.Message}");
            }
        }

        public int ActiveConnections => _activeConnections;
    }
}
