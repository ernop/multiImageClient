using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MultiImageClient;
using Xunit;

public sealed class UiLocalRequestGuardTests
{
    private static HttpContext Context(string method, string host, string? origin = null, string? fetchSite = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Host = new HostString(host);
        if (origin != null) ctx.Request.Headers.Origin = origin;
        if (fetchSite != null) ctx.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Theory]
    [InlineData("127.0.0.1:5960")]
    [InlineData("localhost:5960")]
    [InlineData("[::1]:5960")]
    [InlineData("multi-image-client.localhost")]
    [InlineData("LOCALHOST:5960")]
    public void LoopbackHostsAreServed(string host) =>
        Assert.Null(UiLocalRequestGuard.Problem(Context("GET", host).Request));

    [Theory]
    [InlineData("rebind.attacker.example:5960")]
    [InlineData("127.0.0.1.nip.io:5960")]
    [InlineData("localhost.attacker.example")]
    [InlineData("192.168.1.27:5960")]
    [InlineData("")]
    public void RebindingHostsAreRefused(string host) =>
        Assert.Equal("host", UiLocalRequestGuard.Problem(Context("GET", host).Request));

    [Theory]
    [InlineData("POST", "https://attacker.example", null)]
    [InlineData("POST", "null", null)]
    [InlineData("PUT", "http://127.0.0.1.attacker.example:5960", null)]
    [InlineData("DELETE", null, "cross-site")]
    public void CrossSiteWritesAreRefused(string method, string? origin, string? fetchSite) =>
        Assert.NotNull(UiLocalRequestGuard.Problem(Context(method, "127.0.0.1:5960", origin, fetchSite).Request));

    [Theory]
    [InlineData("127.0.0.1:5960", "http://127.0.0.1:5960", "same-origin")]
    [InlineData("multi-image-client.localhost", "http://multi-image-client.localhost", "same-origin")]
    [InlineData("127.0.0.1:5960", null, null)]
    public void OwnPagesAndScriptsMayWrite(string host, string? origin, string? fetchSite) =>
        Assert.Null(UiLocalRequestGuard.Problem(Context("POST", host, origin, fetchSite).Request));

    [Fact]
    public async Task RefusedRequestsNeverReachTheEndpoint()
    {
        var ctx = Context("POST", "rebind.attacker.example:5960", "http://rebind.attacker.example:5960");
        var reached = false;
        await UiLocalRequestGuard.Middleware(ctx, _ => { reached = true; return Task.CompletedTask; });
        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task KestrelRefusesRebindingAndCrossSiteRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use(UiLocalRequestGuard.Middleware);
        app.MapGet("/api/probe", () => "ok");
        app.MapPost("/api/probe", () => "ok");
        await app.StartAsync();
        var baseUri = new Uri(app.Urls.Single());
        using var http = new HttpClient { BaseAddress = baseUri };

        async Task<HttpStatusCode> Send(HttpMethod method, string? host = null, string? origin = null)
        {
            using var request = new HttpRequestMessage(method, "/api/probe");
            if (host != null) request.Headers.Host = host;
            if (origin != null) request.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Get));
        Assert.Equal(HttpStatusCode.OK, await Send(HttpMethod.Post, origin: $"http://127.0.0.1:{baseUri.Port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Get, host: $"rebind.attacker.example:{baseUri.Port}"));
        Assert.Equal(HttpStatusCode.Forbidden, await Send(HttpMethod.Post, origin: "https://attacker.example"));
        await app.StopAsync();
    }
}
