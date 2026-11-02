using System.Net;
using Microsoft.AspNetCore.Http;
using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class ChatNetworkAccessTests
{
    private const string ProxyKey = "test-proxy-key-0123456789abcdef0123456789abcdef";

    [Fact]
    public void IsAllowed_AcceptsLoopbackWithoutProxyConfiguration()
    {
        using var access = new ChatNetworkAccess(proxyKey: null);
        HttpRequest request = CreateRequest(IPAddress.Loopback, "localhost", proxyKey: null);

        Assert.True(access.IsAllowed(request));
    }

    [Fact]
    public void IsAllowed_RejectsRemoteRequestWithoutProxyConfiguration()
    {
        using var access = new ChatNetworkAccess(proxyKey: null);
        HttpRequest request = CreateRequest(IPAddress.Parse("172.20.0.4"), "localhost", ProxyKey);

        Assert.False(access.IsAllowed(request));
    }

    [Fact]
    public void IsAllowed_AcceptsRemoteRequestWithMatchingProxyKey()
    {
        using var access = new ChatNetworkAccess(ProxyKey);
        HttpRequest request = CreateRequest(IPAddress.Parse("172.20.0.4"), "localhost", ProxyKey);

        Assert.True(access.IsAllowed(request));
    }

    [Fact]
    public void IsAllowed_RejectsRemoteRequestWithWrongProxyKey()
    {
        using var access = new ChatNetworkAccess(ProxyKey);
        HttpRequest request = CreateRequest(
            IPAddress.Parse("172.20.0.4"),
            "localhost",
            "wrong-proxy-key-0123456789abcdef0123456789abcdef");

        Assert.False(access.IsAllowed(request));
    }

    [Fact]
    public void IsAllowed_RejectsUntrustedHostEvenWithMatchingProxyKey()
    {
        using var access = new ChatNetworkAccess(ProxyKey);
        HttpRequest request = CreateRequest(IPAddress.Parse("172.20.0.4"), "attacker.test", ProxyKey);

        Assert.False(access.IsAllowed(request));
    }

    [Fact]
    public void Constructor_RejectsWeakProxyKey()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new ChatNetworkAccess("too-short"));

        Assert.Contains("between 32 and 512", exception.Message, StringComparison.Ordinal);
    }

    private static HttpRequest CreateRequest(
        IPAddress remoteAddress,
        string host,
        string? proxyKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Host = new HostString(host);
        if (proxyKey is not null)
        {
            context.Request.Headers[ChatNetworkAccess.ProxyHeaderName] = proxyKey;
        }

        return context.Request;
    }
}
