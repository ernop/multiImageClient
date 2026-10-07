using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Primitives;
using MultiImageClient;

namespace MultiImageClient.Tests;

public sealed class ShareLinkTests
{
    private static Settings LinkSettings(string folder) => new()
    {
        ImageDownloadBaseFolder = folder, LogFilePath = Path.Combine(folder, "test.log"), EnableGenerationArchive = false,
        UiAuthFilePath = Path.Combine(folder, "auth.json"), UiEnvironmentId = "original",
        UiEnvironmentRegistryPath = Path.Combine(folder, "environments.json"),
        UiPublicBaseUrl = "https://example.test/private-path", UiPublicShareBaseUrl = "https://example.test/shared/original",
    };

    private static object Account(string username) => new
    {
        username,
        passwordHash = "pbkdf2-sha256$600000$" + Convert.ToBase64String(new byte[16]) + "$"
            + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2("test-password", new byte[16], 600000, HashAlgorithmName.SHA256, 32)),
    };

    [Fact]
    public void AllowlistAcceptsOnlyExactPromptLinkPaths()
    {
        Assert.True(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab", "GET"));
        Assert.True(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab", "POST"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab", "PUT"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab", "HEAD"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789AB", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789a", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789abc", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab/", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/0123456789ab/reuse", "POST"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/prompt/", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/api/share-link", "GET"));
    }

    [Fact]
    public void TargetNamesAWholePromptOrOneExactResult()
    {
        static bool Parse(string[] generators, string[] indexes, out string? generator, out int? index) =>
            UiWorkflow.TryParseShareTarget(new StringValues(generators), new StringValues(indexes), out generator, out index);
        Assert.True(Parse([], [], out var generator, out var index));
        Assert.Null(generator);
        Assert.Null(index);
        Assert.True(Parse(["gpt25-sunburst"], ["3"], out generator, out index));
        Assert.Equal("gpt25-sunburst", generator);
        Assert.Equal(3, index);
        Assert.False(Parse(["gpt2"], [], out _, out _));
        Assert.False(Parse([], ["0"], out _, out _));
        Assert.False(Parse(["gpt2", "gpt2"], ["0", "0"], out _, out _));
        Assert.False(Parse(["GPT2"], ["0"], out _, out _));
        Assert.False(Parse(["gpt2/../x"], ["0"], out _, out _));
        Assert.False(Parse(["-gpt2"], ["0"], out _, out _));
        Assert.False(Parse(["gpt2"], ["01"], out _, out _));
        Assert.False(Parse(["gpt2"], ["-1"], out _, out _));
        Assert.False(Parse(["gpt2"], ["10000"], out _, out _));
        Assert.False(Parse(["gpt2"], [""], out _, out _));
    }

    [Fact]
    public void LinksUseThePublicRouteAndDestinationsUseThePrivateSite()
    {
        var settings = LinkSettings("/unused");
        Assert.Equal("https://example.test/shared/original/prompt/0123456789ab", UiWorkflow.ShareLinkUrl(settings, "0123456789ab", null, null));
        Assert.Equal("https://example.test/shared/original/prompt/0123456789ab?gen=gpt2&n=1", UiWorkflow.ShareLinkUrl(settings, "0123456789ab", "gpt2", 1));
        Assert.Equal("https://example.test/private-path/?job=0123456789ab", UiWorkflow.ShareLinkDestination(settings, "0123456789ab", null, null));
        Assert.Equal("https://example.test/private-path/?job=0123456789ab&gen=gpt2&n=1", UiWorkflow.ShareLinkDestination(settings, "0123456789ab", "gpt2", 1));
    }

    [Fact]
    public void LinksNeedLoginAndBothHttpsAddresses()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-link-config-").FullName;
        try
        {
            var settings = LinkSettings(folder);
            File.WriteAllText(settings.UiAuthFilePath, JsonSerializer.Serialize(new { version = 2, enabled = true, secret = new string('s', 40), accounts = new[] { Account("alice") } }));
            var auth = UiAuth.CreateFromSettings(settings)!;
            Assert.Equal("Share links need site login.", UiWorkflow.ShareLinksProblem(settings, null));
            Assert.Null(UiWorkflow.ShareLinksProblem(settings, auth));
            settings.UiPublicShareBaseUrl = "";
            Assert.NotNull(UiWorkflow.ShareLinksProblem(settings, auth));
            settings.UiPublicShareBaseUrl = "https://example.test/shared/original";
            settings.UiPublicBaseUrl = "http://example.test/private-path";
            Assert.Equal("Configure the private HTTPS site address.", UiWorkflow.ShareLinksProblem(settings, auth));
            settings.UiPublicBaseUrl = "";
            Assert.Equal("Configure the private HTTPS site address.", UiWorkflow.ShareLinksProblem(settings, auth));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void LoginPageOffersAnAccountRequestOnlyWhenGiven()
    {
        var plain = UiWorkflow.ShareLinkLoginPage("AVE—images", false, null);
        Assert.Contains("<title>Log in to AVE—images</title>", plain);
        Assert.Contains("Log in to open the shared prompt.", plain);
        Assert.Contains("Need an account? Ask Ernie.", plain);
        Assert.Contains("<script>" + UiWorkflow.ShareLinkLoginScript + "</script>", plain);
        Assert.DoesNotContain('\r', UiWorkflow.ShareLinkLoginScript);
        Assert.DoesNotContain("action=", plain);
        var request = UiWorkflow.ShareLinkLoginPage("Vibecoders <AI>", true, "https://example.test/shared/vibecoders-ai-generation/request-account");
        Assert.Contains("Your current account cannot open Vibecoders &lt;AI&gt;.", request);
        Assert.Contains("<a href=\"https://example.test/shared/vibecoders-ai-generation/request-account\">Request an account</a>", request);
        Assert.DoesNotContain("Vibecoders <AI>", request);
    }

    [Fact]
    public async Task OnlySignedInMembersReceiveThePrivateDestination()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-link-").FullName;
        var settings = LinkSettings(folder);
        File.WriteAllText(settings.UiAuthFilePath, JsonSerializer.Serialize(new { version = 2, enabled = true,
            secret = new string('s', 40), accounts = new[] { Account("alice"), Account("bob") } }));
        File.WriteAllText(settings.UiEnvironmentRegistryPath, """{"version":1,"environments":[{"id":"original","slug":"private-path","name":"Original","original":true,"members":["alice"]}]}""");
        var auth = UiAuth.CreateFromSettings(settings)!;
        var environments = new UiEnvironmentRegistry(settings.UiEnvironmentRegistryPath);
        var jobs = new UiJobRegistry(settings);
        var visibility = new UiVisibilityStore(settings);
        var job = new UiJob { Prompt = "Shared prompt" };
        jobs.Add(job);
        job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "/api/a/0", "/api/a/1" } });
        job.Emit(new { type = "gen-result", gen = "describe-claude", ok = true, resultKind = "text", images = Array.Empty<string>(),
            texts = new[] { new { inputIndex = 0, text = "A red apple." } } });
        job.Emit(new { type = "gen-result", gen = "grok-web-video", ok = true, mediaType = "video/mp4", images = new[] { "/api/v/0" } });
        job.Emit(new { type = "gen-result", gen = "recraft", ok = true, images = new[] { "/api/r/0" } });
        job.Emit(new { type = "gen-result", gen = "recraft", ok = true, images = new[] { "/api/r/0" } });
        job.Emit(new { type = "gen-result", gen = "bfl", ok = false, error = "Provider refused.", images = Array.Empty<string>() });
        job.MarkDone();
        var other = new UiJob { Prompt = "Second prompt" };
        jobs.Add(other);
        other.MarkDone();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        typeof(UiWorkflow).GetMethod("MapShareLinks", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            new object?[] { app, settings, auth, jobs, visibility, environments, null });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        var target = $"/public/prompt/{job.Id}?gen=gpt2&n=1";
        var destination = $"https://example.test/private-path/?job={job.Id}&gen=gpt2&n=1";
        async Task<(HttpStatusCode Status, JsonElement Body)> Link(string query)
        {
            using var response = await http.GetAsync("/api/share-link?" + query);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }
        async Task<HttpResponseMessage> Login(string route, string username, string origin = "https://example.test", bool loginHeader = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = username == "wrong" ? "x" : "test-password" }) };
            request.Headers.Add("Origin", origin);
            if (loginHeader) request.Headers.Add("X-Mic-Login", "1");
            return await http.SendAsync(request);
        }
        async Task<HttpResponseMessage> Get(string route, string? cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            if (cookie != null) request.Headers.Add("Cookie", cookie);
            return await http.SendAsync(request);
        }
        try
        {
            var whole = await Link("jobId=" + job.Id);
            Assert.Equal(HttpStatusCode.OK, whole.Status);
            Assert.Equal($"https://example.test/shared/original/prompt/{job.Id}", whole.Body.GetProperty("url").GetString());
            Assert.Equal("Original", whole.Body.GetProperty("siteName").GetString());
            Assert.Equal($"https://example.test/shared/original{target["/public".Length..]}",
                (await Link($"jobId={job.Id}&generator=gpt2&imageIndex=1")).Body.GetProperty("url").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Link($"jobId={job.Id}&generator=describe-claude&imageIndex=0")).Status);
            Assert.Equal("This result is unavailable.", (await Link($"jobId={job.Id}&generator=gpt2&imageIndex=2")).Body.GetProperty("error").GetString());
            Assert.Equal("This result is unavailable.", (await Link($"jobId={job.Id}&generator=bfl&imageIndex=0")).Body.GetProperty("error").GetString());
            Assert.Equal("Share a video through its prompt link.", (await Link($"jobId={job.Id}&generator=grok-web-video&imageIndex=0")).Body.GetProperty("error").GetString());
            Assert.Equal("This result has more than one record.", (await Link($"jobId={job.Id}&generator=recraft&imageIndex=0")).Body.GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await Link($"jobId={job.Id}&generator=gpt2")).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Link("jobId=NOT-A-JOB")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await Link("jobId=ffffffffffff")).Status);

            using (var anonymous = await Get(target, null))
            {
                var html = await anonymous.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
                Assert.Contains("<title>Log in to Original</title>", html);
                Assert.Contains("Need an account? Ask Ernie.", html);
                Assert.DoesNotContain("private-path", html);
                Assert.DoesNotContain("Shared prompt", html);
                Assert.Equal("no-referrer", anonymous.Headers.GetValues("Referrer-Policy").Single());
                Assert.True(anonymous.Headers.CacheControl!.NoStore);
                Assert.Contains("frame-ancestors 'none'", anonymous.Headers.GetValues("Content-Security-Policy").Single());
                Assert.Contains("script-src 'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(UiWorkflow.ShareLinkLoginScript))) + "'",
                    anonymous.Headers.GetValues("Content-Security-Policy").Single());
            }
            using (var unknown = await Get("/public/prompt/ffffffffffff", null))
                Assert.Contains("Log in to Original", await unknown.Content.ReadAsStringAsync());
            using (var damaged = await Get($"/public/prompt/{job.Id}?gen=gpt2", null))
            {
                Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode);
                Assert.Contains("This link is damaged.", await damaged.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.NotFound, (await Get("/public/prompt/0123456789AB", null)).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await Login(target, "alice", origin: "https://other.test")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Login(target, "alice", origin: "null")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Login(target, "alice", loginHeader: false)).StatusCode);
            using (var wrong = await Login(target, "wrong"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
                Assert.False(wrong.Headers.Contains("Set-Cookie"));
                Assert.Contains("Wrong username or password.", await wrong.Content.ReadAsStringAsync());
            }
            using (var outsider = await Login(target, "bob"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);
                Assert.False(outsider.Headers.Contains("Set-Cookie"));
                var body = await outsider.Content.ReadAsStringAsync();
                Assert.Contains("This account cannot open Original.", body);
                Assert.DoesNotContain("private-path", body);
            }
            string cookie;
            using (var member = await Login(target, "ALICE"))
            {
                Assert.Equal(HttpStatusCode.OK, member.StatusCode);
                using var body = JsonDocument.Parse(await member.Content.ReadAsStringAsync());
                Assert.Equal(destination, body.RootElement.GetProperty("destination").GetString());
                var setCookie = member.Headers.GetValues("Set-Cookie").Single();
                Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
                cookie = setCookie.Split(';')[0];
            }
            using (var signedIn = await Get(target, cookie))
            {
                Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
                Assert.Equal(destination, signedIn.Headers.Location!.ToString());
            }
            using (var wholePrompt = await Get($"/public/prompt/{job.Id}", cookie))
                Assert.Equal($"https://example.test/private-path/?job={job.Id}", wholePrompt.Headers.Location!.ToString());
            using (var missing = await Get("/public/prompt/ffffffffffff", cookie))
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.True(auth.TryLogin("bob", "test-password", "test", out var bobCookie, out _));
            using (var wrongAccount = await Get(target, auth.SessionCookieName + "=" + Uri.EscapeDataString(bobCookie)))
            {
                Assert.Equal(HttpStatusCode.OK, wrongAccount.StatusCode);
                var html = await wrongAccount.Content.ReadAsStringAsync();
                Assert.Contains("Your current account cannot open Original.", html);
                Assert.DoesNotContain("private-path", html);
            }

            visibility.Hide(new UiHiddenResource { Kind = "image", JobId = job.Id, Generator = "gpt2", ImageIndex = 1,
                HiddenByLogin = "alice", HiddenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Assert.Equal("This result was deleted.", (await Link($"jobId={job.Id}&generator=gpt2&imageIndex=1")).Body.GetProperty("error").GetString());
            visibility.Hide(new UiHiddenResource { Kind = "prompt", JobId = other.Id,
                HiddenByLogin = "alice", HiddenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Assert.Equal(HttpStatusCode.NotFound, (await Link("jobId=" + other.Id)).Status);
            using (var hidden = await Get($"/public/prompt/{other.Id}", cookie))
            {
                Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
                Assert.Contains("This prompt is unavailable.", await hidden.Content.ReadAsStringAsync());
            }
            using (var hiddenLogin = await Login($"/public/prompt/{other.Id}", "alice"))
            {
                Assert.Equal(HttpStatusCode.NotFound, hiddenLogin.StatusCode);
                Assert.True(hiddenLogin.Headers.Contains("Set-Cookie"));
                var body = await hiddenLogin.Content.ReadAsStringAsync();
                Assert.Contains("You are logged in.", body);
                Assert.DoesNotContain("private-path", body);
            }
        }
        finally { await app.StopAsync(); Directory.Delete(folder, true); }
    }
}
