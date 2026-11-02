using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SearchQuality.Chat;

/// <summary>
/// Keeps the unauthenticated Chat Studio loopback-only by default while allowing one
/// explicitly configured, secret-bearing reverse proxy in an isolated test deployment.
/// </summary>
public sealed class ChatNetworkAccess : IDisposable
{
    public const string ProxyHeaderName = "X-Search-Lab-Proxy";
    public const string ProxyKeyFileEnvironmentVariable = "SEARCHQUALITY_CHAT_PROXY_KEY_FILE";

    private readonly byte[]? _proxyKeyUtf8;

    public ChatNetworkAccess(string? proxyKey)
    {
        if (proxyKey is null)
        {
            return;
        }

        string normalized = proxyKey.Trim();
        if (normalized.Length is < 32 or > 512)
        {
            throw new InvalidOperationException(
                "The configured Chat Studio proxy key must contain between 32 and 512 characters.");
        }

        _proxyKeyUtf8 = Encoding.UTF8.GetBytes(normalized);
    }

    public static ChatNetworkAccess FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable(ProxyKeyFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ChatNetworkAccess(proxyKey: null);
        }

        try
        {
            return new ChatNetworkAccess(File.ReadAllText(Path.GetFullPath(path)));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The configured Chat Studio proxy key file could not be read.",
                exception);
        }
    }

    public bool IsAllowed(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAllowedHost(request.Host.Host))
        {
            return false;
        }

        IPAddress? remoteAddress = request.HttpContext.Connection.RemoteIpAddress;
        if (remoteAddress is null || IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        if (_proxyKeyUtf8 is null)
        {
            return false;
        }

        string supplied = request.Headers[ProxyHeaderName].ToString();
        byte[] suppliedUtf8 = Encoding.UTF8.GetBytes(supplied);
        try
        {
            return suppliedUtf8.Length == _proxyKeyUtf8.Length
                && CryptographicOperations.FixedTimeEquals(suppliedUtf8, _proxyKeyUtf8);
        }
        finally
        {
            Array.Clear(suppliedUtf8);
        }
    }

    public void Dispose()
    {
        if (_proxyKeyUtf8 is not null)
        {
            Array.Clear(_proxyKeyUtf8);
        }
    }

    private static bool IsAllowedHost(string host) =>
        string.IsNullOrEmpty(host)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(host, "::1", StringComparison.Ordinal);
}
