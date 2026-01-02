# SFTP Server

[![NuGet](https://img.shields.io/nuget/v/SFTPServerLib.svg)](https://www.nuget.org/packages/SFTPServerLib)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SFTPServerLib.svg)](https://www.nuget.org/packages/SFTPServerLib)

A lightweight, enterprise-ready SFTP server built on .NET 8 using the FxSsh library. Compatible with popular SFTP clients like FileZilla, WinSCP, and OpenSSH.

---

## Installation

```bash
dotnet add package SFTPServerLib
```

---

## Features

- **Full SFTP v3 Support** - Upload, download, rename, delete files and directories
- **User Management** - Built-in user authentication with granular permissions
- **Audit Logging** - Track all file operations for compliance and debugging
- **Connection Limits** - Configure maximum concurrent connections
- **Cross-Platform** - Runs on Windows and Linux
- **Secure** - RSA and ECDSA host key support with SHA-2 algorithms

---

## Quick Start

### Basic Usage

```csharp
using SFTPServer.Services;

// Create configuration
var config = new SftpServerConfiguration
{
    ListenAddress = "127.0.0.1",
    Port = 22,
    EnableLogging = true,
    MaxConnections = 100
};

// Create user manager and configure users
var userManager = new UserManager();

// Add a user with full permissions
userManager.AddOrUpdateUser(new UserAccount("admin", "admin123", config.RootDirectory)
{
    IsEnabled = true,
    CanUpload = true,
    CanDownload = true,
    CanDelete = true,
    CanCreateDirectory = true
});

// Create and start SFTP server
var server = new SftpServer(config, userManager);
server.Start();

Console.WriteLine("Press Ctrl+C to stop the server...");
await Task.Delay(-1);
```

---

## Configuration

### SftpServerConfiguration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ListenAddress` | `string?` | `null` | IP address to listen on. `null` = all interfaces. Example: `"127.0.0.1"` for localhost only |
| `Port` | `int` | `22` | SSH server port |
| `RootDirectory` | `string` | OS-specific* | SFTP root directory where files are served from |
| `MaxConnections` | `int` | `100` | Maximum concurrent connections allowed |
| `EnableLogging` | `bool` | `true` | Enable debug logging to console |
| `ConnectionTimeoutSeconds` | `int` | `300` | Connection timeout in seconds (0 = no timeout) |
| `MaxUploadSizeMB` | `int` | `0` | Maximum upload file size in MB (0 = unlimited) |
| `EnableAuditLog` | `bool` | `true` | Enable audit logging for file operations |
| `AuditLogPath` | `string` | OS-specific** | Path to the audit log file |
| `ServerBanner` | `string` | `"SSH-2.0-SFTP_1.0"` | Server banner message (must start with "SSH-2.0-") |
| `RsaHostKey` | `string` | Auto-generated | RSA host key in PEM format |
| `EcdsaP256HostKey` | `string` | Auto-generated | ECDSA P-256 host key in PEM format |

\* **RootDirectory defaults:**
- Windows: `%APPDATA%\SftpRoot`
- Linux: `/var/sftp`

\*\* **AuditLogPath defaults:**
- Windows: `%APPDATA%\SftpLogs\audit.log`
- Linux: `/var/log/sftp/audit.log`

---

## User Management

### UserAccount Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Username` | `string` | Required | User's login name |
| `Password` | `string` | Required | User's password |
| `HomeDirectory` | `string` | Required | User's home directory path |
| `IsEnabled` | `bool` | `true` | Whether the account is active |
| `CanUpload` | `bool` | `true` | Permission to upload files |
| `CanDownload` | `bool` | `true` | Permission to download files |
| `CanDelete` | `bool` | `true` | Permission to delete files and directories |
| `CanCreateDirectory` | `bool` | `true` | Permission to create directories |
| `MaxUploadBytes` | `long` | `0` | Maximum upload size per user (0 = unlimited) |

### UserManager Methods

```csharp
var userManager = new UserManager();

// Add or update a user
userManager.AddOrUpdateUser(new UserAccount("john", "password123", "/home/john"));

// Get a user
var user = userManager.GetUser("john");

// Remove a user
userManager.RemoveUser("john");

// Get all users
var allUsers = userManager.GetAllUsers();

// Check permissions
bool canUpload = userManager.HasPermission("john", SftpOperation.Write);
```

---

## Tested Clients

| Client | Version |
|--------|---------|
| OpenSSH | `OpenSSH_for_Windows_9.5p1, LibreSSL 3.8.2` |
| PuTTY | `Release 0.82` |
| WinSCP | `6.3.6` |
| FileZilla | Compatible |

---

## Underlying Technology: FxSsh

This SFTP server is built on top of the FxSsh library, a lightweight SSH server implementation.

### Supported Algorithms

| Category | Algorithms |
|----------|------------|
| **Public Key** | `rsa-sha2-256`, `rsa-sha2-512`, `ecdsa-sha2-nistp256`, `ecdsa-sha2-nistp384`, `ecdsa-sha2-nistp521` |
| **Key Exchange** | `diffie-hellman-group14-sha256`, `diffie-hellman-group16-sha512`, `diffie-hellman-group18-sha512`, `ecdh-sha2-nistp256`, `ecdh-sha2-nistp384`, `ecdh-sha2-nistp521` |
| **Encryption** | `aes128-ctr`, `aes192-ctr`, `aes256-ctr` |
| **MAC** | `hmac-sha2-256`, `hmac-sha2-512` |

### RFC Compliance

FxSsh adheres to the following RFC documents:
- [RFC4250](https://tools.ietf.org/html/rfc4250) - Protocol Assigned Numbers
- [RFC4251](https://tools.ietf.org/html/rfc4251) - Protocol Architecture
- [RFC4252](https://tools.ietf.org/html/rfc4252) - Authentication Protocol
- [RFC4253](https://tools.ietf.org/html/rfc4253) - Transport Layer Protocol
- [RFC4254](https://tools.ietf.org/html/rfc4254) - Connection Protocol
- [RFC4344](https://tools.ietf.org/html/rfc4344) - Transport Layer Encryption Modes
- [RFC5656](https://tools.ietf.org/html/rfc5656) - Elliptic Curve Algorithm Integration
- [RFC6668](https://tools.ietf.org/html/rfc6668) - SHA-2 Data Integrity Algorithms
- [RFC8332](https://tools.ietf.org/html/rfc8332) - Use of RSA Keys with SHA-2
- [draft-ietf-secsh-filexfer-02](https://tools.ietf.org/html/draft-ietf-secsh-filexfer-02) - SSH File Transfer Protocol (SFTP v3)

---

## License

The MIT license

---

Target: `.NET 8`
