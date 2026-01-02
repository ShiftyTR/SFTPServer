using SFTPServer.Services;

namespace TestApp
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                // Create configuration
                var config = new SftpServerConfiguration
                {
                    ListenAddress= "127.0.0.1",
                    Port = 22,
                    EnableLogging = true,
                    MaxConnections = 100
                };

                // Create user manager and configure users
                var userManager = new UserManager();

                // Update admin user - disable delete permission
                userManager.AddOrUpdateUser(new UserAccount("admin", "admin123", config.RootDirectory)
                {
                    IsEnabled = true,
                    CanUpload = true,
                    CanDownload = true,
                    CanDelete = true,
                    CanCreateDirectory = true
                });

                // Create SFTP server with user manager
                var server = new SftpServer(config, userManager);

                // Create cancellation token source for graceful shutdown
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                // Start server
                server.Start(cts.Token);

                if (config.EnableLogging)
                {
                    Console.WriteLine("Press Ctrl+C to stop the server...");
                    Console.WriteLine();
                }

                try
                {
                    await Task.Delay(-1, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                Environment.Exit(1);
            }
        }
    }
}
