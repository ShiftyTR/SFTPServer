using FxSsh.Services;
using System.Text;

namespace SFTPServer.Services
{
    /// <summary>
    /// SFTP Protocol Handler - Implements SFTP v3 protocol
    /// </summary>
    public class SftpSubsystem
    {
        private readonly SessionChannel _channel;
        private readonly string _rootDirectory;
        private readonly bool _enableLogging;
        private readonly UserAccount? _user;
        private readonly AuditLogger? _audit;
        private readonly string _sessionId = "-";
        private readonly string _username = "-";
        private readonly long _maxUploadBytes; // 0 = unlimited
        private readonly int _idleTimeoutSeconds; // 0 = no timeout
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private Timer? _idleTimer;
        private readonly Dictionary<uint, FileStream> _openFiles = new();
        private readonly Dictionary<uint, DirectoryInfo> _openDirectories = new();
        private readonly Dictionary<uint, bool> _directoryRead = new(); // Track if directory was already read
        private uint _handleCounter = 1;
        private string _currentDirectory;

        // SFTP packet types
        private const byte SSH_FXP_INIT = 1;
        private const byte SSH_FXP_VERSION = 2;
        private const byte SSH_FXP_OPEN = 3;
        private const byte SSH_FXP_CLOSE = 4;
        private const byte SSH_FXP_READ = 5;
        private const byte SSH_FXP_WRITE = 6;
        private const byte SSH_FXP_LSTAT = 7;
        private const byte SSH_FXP_FSTAT = 8;
        private const byte SSH_FXP_SETSTAT = 9;
        private const byte SSH_FXP_FSETSTAT = 10;
        private const byte SSH_FXP_OPENDIR = 11;
        private const byte SSH_FXP_READDIR = 12;
        private const byte SSH_FXP_REMOVE = 13;
        private const byte SSH_FXP_MKDIR = 14;
        private const byte SSH_FXP_RMDIR = 15;
        private const byte SSH_FXP_REALPATH = 16;
        private const byte SSH_FXP_STAT = 17;
        private const byte SSH_FXP_RENAME = 18;
        private const byte SSH_FXP_READLINK = 19;
        private const byte SSH_FXP_SYMLINK = 20;

        private const byte SSH_FXP_STATUS = 101;
        private const byte SSH_FXP_HANDLE = 102;
        private const byte SSH_FXP_DATA = 103;
        private const byte SSH_FXP_NAME = 104;
        private const byte SSH_FXP_ATTRS = 105;

        // Status codes
        private const uint SSH_FX_OK = 0;
        private const uint SSH_FX_EOF = 1;
        private const uint SSH_FX_NO_SUCH_FILE = 2;
        private const uint SSH_FX_PERMISSION_DENIED = 3;
        private const uint SSH_FX_FAILURE = 4;
        private const uint SSH_FX_BAD_MESSAGE = 5;
        private const uint SSH_FX_NO_CONNECTION = 6;
        private const uint SSH_FX_CONNECTION_LOST = 7;
        private const uint SSH_FX_OP_UNSUPPORTED = 8;

        private byte[] _buffer = Array.Empty<byte>();

        public SftpSubsystem(SessionChannel channel, string rootDirectory, UserAccount? user = null, bool enableLogging = true, AuditLogger? audit = null, string sessionId = "-", string username = "-", long maxUploadBytes = 0, int idleTimeoutSeconds = 0)
        {
            _channel = channel;
            _rootDirectory = rootDirectory;
            _currentDirectory = rootDirectory;
            _user = user;
            _enableLogging = enableLogging;
            _audit = audit;
            _sessionId = sessionId;
            _username = username;
            _maxUploadBytes = maxUploadBytes;
            _idleTimeoutSeconds = idleTimeoutSeconds;

            // Ensure root directory exists
            if (!Directory.Exists(_rootDirectory))
            {
                Directory.CreateDirectory(_rootDirectory);
            }

            // Subscribe to channel data
            _channel.DataReceived += OnDataReceived;
            _channel.CloseReceived += OnCloseReceived;

            // Setup idle timeout if configured
            if (_idleTimeoutSeconds > 0)
            {
                _idleTimer = new Timer(_ => CheckIdleTimeout(), null, TimeSpan.FromSeconds(_idleTimeoutSeconds), TimeSpan.FromSeconds(_idleTimeoutSeconds));
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] Subsystem initialized for user '{_user?.Username ?? "unknown"}', root: {_rootDirectory}");
        }

        private void OnCloseReceived(object? sender, EventArgs e)
        {
            // Clean up open files and directories
            foreach (var file in _openFiles.Values)
            {
                try { file.Close(); } catch { }
            }
            _openFiles.Clear();
            _openDirectories.Clear();
            _idleTimer?.Dispose();

            if (_enableLogging)
                Console.WriteLine("[SFTP] Channel closed");
            _audit?.LogDisconnection(_sessionId, _username);
        }

        private void OnDataReceived(object? sender, byte[] data)
        {
            try
            {
                _lastActivityUtc = DateTime.UtcNow;
                // Append new data to buffer
                var newBuffer = new byte[_buffer.Length + data.Length];
                Array.Copy(_buffer, 0, newBuffer, 0, _buffer.Length);
                Array.Copy(data, 0, newBuffer, _buffer.Length, data.Length);
                _buffer = newBuffer;

                // Process complete packets
                while (_buffer.Length >= 4)
                {
                    uint packetLength = ReadUInt32(_buffer, 0);
                    if (_buffer.Length < packetLength + 4)
                        break; // Wait for more data

                    // Extract packet
                    byte[] packet = new byte[packetLength];
                    Array.Copy(_buffer, 4, packet, 0, packetLength);

                    // Remove processed data from buffer
                    byte[] remaining = new byte[_buffer.Length - packetLength - 4];
                    Array.Copy(_buffer, packetLength + 4, remaining, 0, remaining.Length);
                    _buffer = remaining;

                    // Process packet
                    ProcessPacket(packet);
                }
            }
            catch (Exception ex)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] Error processing data: {ex.Message}");
                _audit?.LogError(_sessionId, _username, "DATA_RECEIVED", ex.Message);
            }
        }

        private void CheckIdleTimeout()
        {
            if (_idleTimeoutSeconds <= 0) return;
            var idleFor = DateTime.UtcNow - _lastActivityUtc;
            if (idleFor.TotalSeconds >= _idleTimeoutSeconds)
            {
                try
                {
                    if (_enableLogging)
                        Console.WriteLine($"[SFTP] Idle timeout reached ({_idleTimeoutSeconds}s)");
                    _channel.SendClose();
                }
                catch { }
                finally
                {
                    _idleTimer?.Dispose();
                    _idleTimer = null;
                }
            }
        }

        private void ProcessPacket(byte[] packet)
        {
            if (packet.Length < 1) return;

            byte type = packet[0];

            if (_enableLogging)
                Console.WriteLine($"[SFTP] Received packet type: {type} ({GetPacketTypeName(type)})");

            switch (type)
            {
                case SSH_FXP_INIT:
                    HandleInit(packet);
                    break;
                case SSH_FXP_REALPATH:
                    HandleRealPath(packet);
                    break;
                case SSH_FXP_STAT:
                case SSH_FXP_LSTAT:
                    HandleStat(packet);
                    break;
                case SSH_FXP_OPENDIR:
                    HandleOpenDir(packet);
                    break;
                case SSH_FXP_READDIR:
                    HandleReadDir(packet);
                    break;
                case SSH_FXP_CLOSE:
                    HandleClose(packet);
                    break;
                case SSH_FXP_OPEN:
                    HandleOpen(packet);
                    break;
                case SSH_FXP_READ:
                    HandleRead(packet);
                    break;
                case SSH_FXP_WRITE:
                    HandleWrite(packet);
                    break;
                case SSH_FXP_REMOVE:
                    HandleRemove(packet);
                    break;
                case SSH_FXP_MKDIR:
                    HandleMkdir(packet);
                    break;
                case SSH_FXP_RMDIR:
                    HandleRmdir(packet);
                    break;
                case SSH_FXP_RENAME:
                    HandleRename(packet);
                    break;
                case SSH_FXP_FSTAT:
                    HandleFStat(packet);
                    break;
                case SSH_FXP_SETSTAT:
                    HandleSetStat(packet);
                    break;
                case SSH_FXP_FSETSTAT:
                    HandleFSetStat(packet);
                    break;
                case SSH_FXP_READLINK:
                    HandleReadLink(packet);
                    break;
                case SSH_FXP_SYMLINK:
                    HandleSymlink(packet);
                    break;
                default:
                    if (_enableLogging)
                        Console.WriteLine($"[SFTP] Unsupported packet type: {type}");
                    // Send unsupported response
                    if (packet.Length >= 5)
                    {
                        uint requestId = ReadUInt32(packet, 1);
                        SendStatus(requestId, SSH_FX_OP_UNSUPPORTED, "Operation not supported");
                    }
                    break;
            }
        }

        private void HandleInit(byte[] packet)
        {
            // Client sends version
            uint clientVersion = packet.Length >= 5 ? ReadUInt32(packet, 1) : 3;
            if (_enableLogging)
                Console.WriteLine($"[SFTP] Client version: {clientVersion}");

            // Send version response (we support version 3)
            var response = new List<byte>();
            response.Add(SSH_FXP_VERSION);
            response.AddRange(ToBytes((uint)3)); // Version 3
            SendPacket(response.ToArray());

            if (_enableLogging)
                Console.WriteLine("[SFTP] Sent version 3 response");
        }

        private void HandleRealPath(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] REALPATH: {path}");

            string realPath = ResolvePath(path);
            
            // Send NAME response with single entry
            var response = new List<byte>();
            response.Add(SSH_FXP_NAME);
            response.AddRange(ToBytes(requestId));
            response.AddRange(ToBytes((uint)1)); // count
            response.AddRange(ToBytes(realPath)); // filename
            response.AddRange(ToBytes(realPath)); // longname
            response.AddRange(GetDummyAttrs()); // attrs

            SendPacket(response.ToArray());
        }

        private void HandleStat(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] STAT: {path}");

            string fullPath = GetFullPath(path);

            try
            {
                if (Directory.Exists(fullPath))
                {
                    var info = new DirectoryInfo(fullPath);
                    SendAttrs(requestId, info);
                }
                else if (File.Exists(fullPath))
                {
                    var info = new FileInfo(fullPath);
                    SendAttrs(requestId, info);
                }
                else
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "No such file");
                }
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleOpenDir(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] OPENDIR: {path}");

            string fullPath = GetFullPath(path);

            try
            {
                if (!Directory.Exists(fullPath))
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "Directory not found");
                    return;
                }

                var dir = new DirectoryInfo(fullPath);
                uint handle = _handleCounter++;
                _openDirectories[handle] = dir;

                SendHandle(requestId, handle);
                _audit?.LogDirectoryList(_sessionId, _username, fullPath);
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "OPENDIR", ex.Message);
            }
        }

        private void HandleReadDir(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] READDIR: handle={handle}");

            if (!_openDirectories.TryGetValue(handle, out var dir))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle");
                return;
            }

            try
            {
                // Check if we already sent the directory listing
                if (_directoryRead.TryGetValue(handle, out bool alreadyRead) && alreadyRead)
                {
                    // Already read, send EOF
                    SendStatus(requestId, SSH_FX_EOF, "End of directory");
                    return;
                }

                var entries = dir.GetFileSystemInfos();

                // Send directory entries
                var response = new List<byte>();
                response.Add(SSH_FXP_NAME);
                response.AddRange(ToBytes(requestId));
                response.AddRange(ToBytes((uint)entries.Length));

                foreach (var entry in entries)
                {
                    string name = entry.Name;
                    string longName = FormatLongName(entry);

                    response.AddRange(ToBytes(name));
                    response.AddRange(ToBytes(longName));
                    response.AddRange(GetAttrs(entry));
                }

                SendPacket(response.ToArray());

                // Mark as read (next read will return EOF)
                _directoryRead[handle] = true;
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleClose(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] CLOSE: handle={handle}");

            if (_openFiles.TryGetValue(handle, out var file))
            {
                file.Close();
                _openFiles.Remove(handle);
            }
            _openDirectories.Remove(handle);
            _directoryRead.Remove(handle);

            SendStatus(requestId, SSH_FX_OK, "OK");
        }

        private void HandleOpen(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            int offset = 5;
            string path = ReadString(packet, offset, out int bytesRead);
            offset += bytesRead;
            uint pflags = ReadUInt32(packet, offset);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] OPEN: {path}, flags={pflags}");

            // Check permissions based on flags
            bool isWrite = (pflags & 0x02) != 0 || (pflags & 0x08) != 0 || (pflags & 0x10) != 0 || (pflags & 0x20) != 0;
            bool isRead = (pflags & 0x01) != 0;

            if (_user != null)
            {
                if (isWrite && !_user.CanUpload)
                {
                    if (_enableLogging)
                        Console.WriteLine($"[SFTP] OPEN DENIED: User '{_user.Username}' does not have upload permission");
                    SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied: Upload not allowed");
                    return;
                }

                if (isRead && !_user.CanDownload)
                {
                    if (_enableLogging)
                        Console.WriteLine($"[SFTP] OPEN DENIED: User '{_user.Username}' does not have download permission");
                    SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied: Download not allowed");
                    return;
                }
            }

            string fullPath = GetFullPath(path);

            try
            {
                FileMode mode = FileMode.Open;
                FileAccess access = FileAccess.Read;

                if ((pflags & 0x01) != 0) access |= FileAccess.Read;
                if ((pflags & 0x02) != 0) access |= FileAccess.Write;
                if ((pflags & 0x08) != 0) mode = FileMode.Append;
                if ((pflags & 0x10) != 0) mode = FileMode.Create;
                if ((pflags & 0x20) != 0) mode = FileMode.Truncate;

                var fs = new FileStream(fullPath, mode, access, FileShare.ReadWrite);
                uint handle = _handleCounter++;
                _openFiles[handle] = fs;

                SendHandle(requestId, handle);
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleRead(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5, out int bytesRead);
            int offset = 5 + bytesRead;
            ulong fileOffset = ReadUInt64(packet, offset);
            uint length = ReadUInt32(packet, offset + 8);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] READ: handle={handle}, offset={fileOffset}, length={length}");

            if (!_openFiles.TryGetValue(handle, out var file))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle");
                return;
            }

            try
            {
                file.Seek((long)fileOffset, SeekOrigin.Begin);
                byte[] buffer = new byte[length];
                int read = file.Read(buffer, 0, (int)length);

                if (read == 0)
                {
                    SendStatus(requestId, SSH_FX_EOF, "End of file");
                    return;
                }

                var response = new List<byte>();
                response.Add(SSH_FXP_DATA);
                response.AddRange(ToBytes(requestId));
                response.AddRange(ToBytes((uint)read));
                response.AddRange(buffer.Take(read));

                SendPacket(response.ToArray());
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleWrite(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5, out int bytesRead);
            int offset = 5 + bytesRead;
            ulong fileOffset = ReadUInt64(packet, offset);
            uint dataLength = ReadUInt32(packet, offset + 8);
            byte[] data = new byte[dataLength];
            Array.Copy(packet, offset + 12, data, 0, dataLength);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] WRITE: handle={handle}, offset={fileOffset}, length={dataLength}");

            if (!_openFiles.TryGetValue(handle, out var file))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle");
                return;
            }

            try
            {
                // Enforce max upload size if configured
                if (_maxUploadBytes > 0)
                {
                    long targetSize = Math.Max((long)fileOffset + data.Length, new FileInfo(file.Name).Length);
                    if (targetSize > _maxUploadBytes)
                    {
                        SendStatus(requestId, SSH_FX_FAILURE, "Upload size limit exceeded");
                        _audit?.LogError(_sessionId, _username, "WRITE", "Upload size limit exceeded");
                        return;
                    }
                }

                file.Seek((long)fileOffset, SeekOrigin.Begin);
                file.Write(data, 0, data.Length);
                SendStatus(requestId, SSH_FX_OK, "OK");
                _audit?.LogFileWrite(_sessionId, _username, file.Name, data.Length);
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "WRITE", ex.Message);
            }
        }

        private void HandleRemove(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] REMOVE: {path}");

            // Check permission
            if (_user != null && !_user.CanDelete)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] REMOVE DENIED: User '{_user.Username}' does not have delete permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullPath = GetFullPath(path);

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    SendStatus(requestId, SSH_FX_OK, "OK");
                    _audit?.LogFileDelete(_sessionId, _username, fullPath);
                }
                else
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "File not found");
                }
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "REMOVE", ex.Message);
            }
        }

        private void HandleMkdir(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] MKDIR: {path}");

            // Check permission
            if (_user != null && !_user.CanCreateDirectory)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] MKDIR DENIED: User '{_user.Username}' does not have create directory permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullPath = GetFullPath(path);

            try
            {
                Directory.CreateDirectory(fullPath);
                SendStatus(requestId, SSH_FX_OK, "OK");
                _audit?.LogDirectoryCreate(_sessionId, _username, fullPath);
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "MKDIR", ex.Message);
            }
        }

        private void HandleRmdir(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] RMDIR: {path}");

            // Check permission
            if (_user != null && !_user.CanDelete)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] RMDIR DENIED: User '{_user.Username}' does not have delete permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullPath = GetFullPath(path);

            try
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, false);
                    SendStatus(requestId, SSH_FX_OK, "OK");
                    _audit?.LogDirectoryDelete(_sessionId, _username, fullPath);
                }
                else
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "Directory not found");
                }
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "RMDIR", ex.Message);
            }
        }

        private void HandleRename(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string oldPath = ReadString(packet, 5, out int bytesRead);
            string newPath = ReadString(packet, 5 + bytesRead);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] RENAME: {oldPath} -> {newPath}");

            // Check permission - rename requires both upload (write to new) and delete (remove old) permissions
            if (_user != null && (!_user.CanUpload || !_user.CanDelete))
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] RENAME DENIED: User '{_user.Username}' does not have rename permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullOldPath = GetFullPath(oldPath);
            string fullNewPath = GetFullPath(newPath);

            try
            {
                if (File.Exists(fullOldPath))
                {
                    File.Move(fullOldPath, fullNewPath);
                    SendStatus(requestId, SSH_FX_OK, "OK");
                    _audit?.LogRename(_sessionId, _username, fullOldPath, fullNewPath);
                }
                else if (Directory.Exists(fullOldPath))
                {
                    Directory.Move(fullOldPath, fullNewPath);
                    SendStatus(requestId, SSH_FX_OK, "OK");
                    _audit?.LogRename(_sessionId, _username, fullOldPath, fullNewPath);
                }
                else
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "File not found");
                }
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
                _audit?.LogError(_sessionId, _username, "RENAME", ex.Message);
            }
        }

        private void HandleFStat(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] FSTAT: handle={handle}");

            if (_openFiles.TryGetValue(handle, out var file))
            {
                var info = new FileInfo(file.Name);
                SendAttrs(requestId, info);
            }
            else
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle");
            }
        }

        private void HandleSetStat(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5, out int bytesRead);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] SETSTAT: {path}");

            // Check permission - setstat requires upload permission
            if (_user != null && !_user.CanUpload)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] SETSTAT DENIED: User '{_user.Username}' does not have permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullPath = GetFullPath(path);

            try
            {
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "No such file");
                    return;
                }

                // Parse attributes from packet
                int attrOffset = 5 + bytesRead;
                if (packet.Length > attrOffset + 4)
                {
                    uint flags = ReadUInt32(packet, attrOffset);
                    int offset = attrOffset + 4;

                    // Skip size if present (8 bytes)
                    if ((flags & 0x01) != 0) offset += 8;

                    // Skip uid/gid if present (8 bytes)
                    if ((flags & 0x02) != 0) offset += 8;

                    // Skip permissions if present (4 bytes)
                    if ((flags & 0x04) != 0) offset += 4;

                    // Handle atime/mtime if present
                    if ((flags & 0x08) != 0 && packet.Length >= offset + 8)
                    {
                        uint atime = ReadUInt32(packet, offset);
                        uint mtime = ReadUInt32(packet, offset + 4);

                        var accessTime = DateTimeOffset.FromUnixTimeSeconds(atime).LocalDateTime;
                        var modifyTime = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime;

                        if (File.Exists(fullPath))
                        {
                            File.SetLastAccessTime(fullPath, accessTime);
                            File.SetLastWriteTime(fullPath, modifyTime);
                        }
                        else if (Directory.Exists(fullPath))
                        {
                            Directory.SetLastAccessTime(fullPath, accessTime);
                            Directory.SetLastWriteTime(fullPath, modifyTime);
                        }
                    }
                }

                SendStatus(requestId, SSH_FX_OK, "OK");
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleFSetStat(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string handleStr = ReadString(packet, 5, out int bytesRead);

            if (!uint.TryParse(handleStr, out uint handle))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle format");
                return;
            }

            if (_enableLogging)
                Console.WriteLine($"[SFTP] FSETSTAT: handle={handle}");

            // Check permission
            if (_user != null && !_user.CanUpload)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] FSETSTAT DENIED: User '{_user.Username}' does not have permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            if (!_openFiles.TryGetValue(handle, out var file))
            {
                SendStatus(requestId, SSH_FX_FAILURE, "Invalid handle");
                return;
            }

            try
            {
                string fullPath = file.Name;

                // Parse attributes from packet
                int attrOffset = 5 + bytesRead;
                if (packet.Length > attrOffset + 4)
                {
                    uint flags = ReadUInt32(packet, attrOffset);
                    int offset = attrOffset + 4;

                    // Skip size if present (8 bytes)
                    if ((flags & 0x01) != 0) offset += 8;

                    // Skip uid/gid if present (8 bytes)
                    if ((flags & 0x02) != 0) offset += 8;

                    // Skip permissions if present (4 bytes)
                    if ((flags & 0x04) != 0) offset += 4;

                    // Handle atime/mtime if present
                    if ((flags & 0x08) != 0 && packet.Length >= offset + 8)
                    {
                        uint atime = ReadUInt32(packet, offset);
                        uint mtime = ReadUInt32(packet, offset + 4);

                        var accessTime = DateTimeOffset.FromUnixTimeSeconds(atime).LocalDateTime;
                        var modifyTime = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime;

                        // Close file temporarily to set times
                        file.Flush();
                        File.SetLastAccessTime(fullPath, accessTime);
                        File.SetLastWriteTime(fullPath, modifyTime);
                    }
                }

                SendStatus(requestId, SSH_FX_OK, "OK");
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleReadLink(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string path = ReadString(packet, 5);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] READLINK: {path}");

            string fullPath = GetFullPath(path);

            try
            {
                // Check if it's a symbolic link
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // On Windows, resolve the link target
                    string? target = null;

                    try
                    {
                        target = File.ResolveLinkTarget(fullPath, false)?.FullName;
                    }
                    catch
                    {
                        // Fall back - not a supported link type
                    }

                    if (!string.IsNullOrEmpty(target))
                    {
                        // Convert to relative path if within root
                        string relativePath = target;
                        if (target.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            relativePath = target.Substring(_rootDirectory.Length).Replace(Path.DirectorySeparatorChar, '/');
                            if (!relativePath.StartsWith("/"))
                                relativePath = "/" + relativePath;
                        }

                        // Send NAME response
                        var response = new List<byte>();
                        response.Add(SSH_FXP_NAME);
                        response.AddRange(ToBytes(requestId));
                        response.AddRange(ToBytes((uint)1)); // count
                        response.AddRange(ToBytes(relativePath)); // filename
                        response.AddRange(ToBytes(relativePath)); // longname
                        response.AddRange(GetDummyAttrs()); // attrs

                        SendPacket(response.ToArray());
                        return;
                    }
                }

                SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "Not a symbolic link");
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        private void HandleSymlink(byte[] packet)
        {
            uint requestId = ReadUInt32(packet, 1);
            string linkPath = ReadString(packet, 5, out int bytesRead);
            string targetPath = ReadString(packet, 5 + bytesRead);

            if (_enableLogging)
                Console.WriteLine($"[SFTP] SYMLINK: {linkPath} -> {targetPath}");

            // Check permission - symlink requires upload permission
            if (_user != null && !_user.CanUpload)
            {
                if (_enableLogging)
                    Console.WriteLine($"[SFTP] SYMLINK DENIED: User '{_user.Username}' does not have permission");
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Permission denied");
                return;
            }

            string fullLinkPath = GetFullPath(linkPath);
            string fullTargetPath = GetFullPath(targetPath);

            try
            {
                // Check if target exists
                bool isDirectory = Directory.Exists(fullTargetPath);
                bool isFile = File.Exists(fullTargetPath);

                if (!isDirectory && !isFile)
                {
                    SendStatus(requestId, SSH_FX_NO_SUCH_FILE, "Target not found");
                    return;
                }

                // Create symbolic link (requires elevated permissions on Windows)
                if (isDirectory)
                {
                    Directory.CreateSymbolicLink(fullLinkPath, fullTargetPath);
                }
                else
                {
                    File.CreateSymbolicLink(fullLinkPath, fullTargetPath);
                }

                SendStatus(requestId, SSH_FX_OK, "OK");
            }
            catch (UnauthorizedAccessException)
            {
                SendStatus(requestId, SSH_FX_PERMISSION_DENIED, "Symbolic links require administrator privileges on Windows");
            }
            catch (Exception ex)
            {
                SendStatus(requestId, SSH_FX_FAILURE, ex.Message);
            }
        }

        #region Helper Methods

        private string GetFullPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "." || path == "/")
                return _rootDirectory;

            // Remove leading slash
            if (path.StartsWith("/"))
                path = path.Substring(1);

            // Combine with root
            string fullPath = Path.Combine(_rootDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            
            // Security: Ensure path is within root directory
            string normalizedPath = Path.GetFullPath(fullPath);
            if (!normalizedPath.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return _rootDirectory;
            }

            return normalizedPath;
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "." || path == "/")
                return "/";

            // Normalize path
            string fullPath = GetFullPath(path);
            string relativePath = fullPath.Substring(_rootDirectory.Length).Replace(Path.DirectorySeparatorChar, '/');
            
            if (string.IsNullOrEmpty(relativePath))
                return "/";
                
            if (!relativePath.StartsWith("/"))
                relativePath = "/" + relativePath;

            return relativePath;
        }

        private void SendPacket(byte[] data)
        {
            var packet = new List<byte>();
            packet.AddRange(ToBytes((uint)data.Length));
            packet.AddRange(data);
            _channel.SendData(packet.ToArray());
        }

        private void SendStatus(uint requestId, uint statusCode, string message)
        {
            var response = new List<byte>();
            response.Add(SSH_FXP_STATUS);
            response.AddRange(ToBytes(requestId));
            response.AddRange(ToBytes(statusCode));
            response.AddRange(ToBytes(message));
            response.AddRange(ToBytes("")); // language tag

            SendPacket(response.ToArray());
        }

        private void SendHandle(uint requestId, uint handle)
        {
            var response = new List<byte>();
            response.Add(SSH_FXP_HANDLE);
            response.AddRange(ToBytes(requestId));
            response.AddRange(ToBytes(handle.ToString()));

            SendPacket(response.ToArray());
        }

        private void SendAttrs(uint requestId, FileSystemInfo info)
        {
            var response = new List<byte>();
            response.Add(SSH_FXP_ATTRS);
            response.AddRange(ToBytes(requestId));
            response.AddRange(GetAttrs(info));

            SendPacket(response.ToArray());
        }

        private byte[] GetAttrs(FileSystemInfo info)
        {
            var attrs = new List<byte>();
            
            uint flags = 0x0F; // SIZE | UIDGID | PERMISSIONS | ACMODTIME
            attrs.AddRange(ToBytes(flags));

            // Size
            long size = 0;
            if (info is FileInfo fi)
                size = fi.Length;
            attrs.AddRange(ToBytes((ulong)size));

            // UID/GID
            attrs.AddRange(ToBytes((uint)0)); // uid
            attrs.AddRange(ToBytes((uint)0)); // gid

            // Permissions
            uint permissions = info is DirectoryInfo ? 0x41FDu : 0x81A4u; // drwxrwxr-x or -rw-r--r--
            attrs.AddRange(ToBytes(permissions));

            // Access/Modify time
            uint atime = (uint)((DateTimeOffset)info.LastAccessTime).ToUnixTimeSeconds();
            uint mtime = (uint)((DateTimeOffset)info.LastWriteTime).ToUnixTimeSeconds();
            attrs.AddRange(ToBytes(atime));
            attrs.AddRange(ToBytes(mtime));

            return attrs.ToArray();
        }

        private byte[] GetDummyAttrs()
        {
            var attrs = new List<byte>();
            attrs.AddRange(ToBytes((uint)0x0F)); // flags
            attrs.AddRange(ToBytes((ulong)0)); // size
            attrs.AddRange(ToBytes((uint)0)); // uid
            attrs.AddRange(ToBytes((uint)0)); // gid
            attrs.AddRange(ToBytes((uint)0x41FD)); // permissions (directory)
            attrs.AddRange(ToBytes((uint)DateTimeOffset.Now.ToUnixTimeSeconds())); // atime
            attrs.AddRange(ToBytes((uint)DateTimeOffset.Now.ToUnixTimeSeconds())); // mtime
            return attrs.ToArray();
        }

        private string FormatLongName(FileSystemInfo info)
        {
            string perms = info is DirectoryInfo ? "drwxrwxr-x" : "-rw-r--r--";
            string size = info is FileInfo fi ? fi.Length.ToString() : "4096";
            string date = info.LastWriteTime.ToString("MMM dd HH:mm");
            return $"{perms}   1 owner    group    {size,10} {date} {info.Name}";
        }

        private string GetPacketTypeName(byte type) => type switch
        {
            SSH_FXP_INIT => "INIT",
            SSH_FXP_VERSION => "VERSION",
            SSH_FXP_OPEN => "OPEN",
            SSH_FXP_CLOSE => "CLOSE",
            SSH_FXP_READ => "READ",
            SSH_FXP_WRITE => "WRITE",
            SSH_FXP_LSTAT => "LSTAT",
            SSH_FXP_FSTAT => "FSTAT",
            SSH_FXP_SETSTAT => "SETSTAT",
            SSH_FXP_FSETSTAT => "FSETSTAT",
            SSH_FXP_OPENDIR => "OPENDIR",
            SSH_FXP_READDIR => "READDIR",
            SSH_FXP_REMOVE => "REMOVE",
            SSH_FXP_MKDIR => "MKDIR",
            SSH_FXP_RMDIR => "RMDIR",
            SSH_FXP_REALPATH => "REALPATH",
            SSH_FXP_STAT => "STAT",
            SSH_FXP_RENAME => "RENAME",
            SSH_FXP_READLINK => "READLINK",
            SSH_FXP_SYMLINK => "SYMLINK",
            _ => $"UNKNOWN({type})"
        };

        // Binary helpers
        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        private static ulong ReadUInt64(byte[] data, int offset) =>
            ((ulong)ReadUInt32(data, offset) << 32) | ReadUInt32(data, offset + 4);

        private static string ReadString(byte[] data, int offset) => ReadString(data, offset, out _);

        private static string ReadString(byte[] data, int offset, out int bytesRead)
        {
            uint length = ReadUInt32(data, offset);
            bytesRead = 4 + (int)length;
            return Encoding.UTF8.GetString(data, offset + 4, (int)length);
        }

        private static byte[] ToBytes(uint value) => new byte[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };

        private static byte[] ToBytes(ulong value) => new byte[]
        {
            (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };

        private static byte[] ToBytes(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var result = new List<byte>();
            result.AddRange(ToBytes((uint)bytes.Length));
            result.AddRange(bytes);
            return result.ToArray();
        }

        #endregion
    }
}
