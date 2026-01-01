namespace SFTPServer.Services
{
    /// <summary>
    /// Simple User Manager for SFTP authentication and authorization
    /// Stores users in memory dictionary
    /// </summary>
    public class UserManager
    {
        private readonly Dictionary<string, UserAccount> _users = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        /// <summary>
        /// Authenticate a user with password
        /// </summary>
        public bool AuthenticatePassword(string username, string password, out UserAccount? user)
        {
            lock (_lock)
            {
                if (_users.TryGetValue(username, out user))
                {
                    if (!user.IsEnabled)
                    {
                        user = null;
                        return false;
                    }

                    return user.Password == password;
                }
            }

            user = null;
            return false;
        }

        /// <summary>
        /// Get user by username
        /// </summary>
        public UserAccount? GetUser(string username)
        {
            lock (_lock)
            {
                return _users.TryGetValue(username, out var user) ? user : null;
            }
        }

        /// <summary>
        /// Add or update a user
        /// </summary>
        public void AddOrUpdateUser(UserAccount user)
        {
            lock (_lock)
            {
                _users[user.Username] = user;
            }
        }

        /// <summary>
        /// Remove a user
        /// </summary>
        public bool RemoveUser(string username)
        {
            lock (_lock)
            {
                return _users.Remove(username);
            }
        }

        /// <summary>
        /// Get all users
        /// </summary>
        public IReadOnlyList<UserAccount> GetAllUsers()
        {
            lock (_lock)
            {
                return _users.Values.ToList();
            }
        }

                /// <summary>
                /// Check if user has permission for an operation
                /// </summary>
                public bool HasPermission(string username, SftpOperation operation)
                {
                    var user = GetUser(username);
                    if (user == null || !user.IsEnabled) return false;

                    return operation switch
                    {
                        SftpOperation.Read => user.CanDownload,
                        SftpOperation.Write => user.CanUpload,
                        SftpOperation.Delete => user.CanDelete,
                        SftpOperation.CreateDirectory => user.CanCreateDirectory,
                        SftpOperation.List => true,
                        _ => false
                    };
                }                
            }

            /// <summary>
            /// SFTP operations for permission checking
            /// </summary>
            public enum SftpOperation
            {
                Read,
                Write,
                Delete,
                CreateDirectory,
                List
            }
        }
