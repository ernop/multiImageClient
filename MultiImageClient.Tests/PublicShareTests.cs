using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MultiImageClient;

namespace MultiImageClient.Tests;

public sealed class PublicShareTests
{
    [Fact]
    public void AnonymousAllowlistContainsOnlyPageAssetsAndLoginHandoff()
    {
        var token = new string('a', 64);
        Assert.True(UiPublicShares.IsPublicRequest($"/public/{token}/", "GET"));
        Assert.True(UiPublicShares.IsPublicRequest($"/public/{token}/asset/0", "GET"));
        Assert.True(UiPublicShares.IsPublicRequest($"/public/{token}/reuse", "POST"));
        Assert.False(UiPublicShares.IsPublicRequest($"/public/{token}/asset/0", "POST"));
        Assert.False(UiPublicShares.IsPublicRequest("/api/jobs", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/../../api/jobs", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/environment.js", "GET"));
    }
    [Fact]
    public void SnapshotOmitsPrivateMetadataAndEscapesPromptHtml()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-snapshot-").FullName;
        try
        {
        var job = new UiJob { Prompt = "<script>secret prompt</script>", CreatorLogin = "private-user", InputImagePaths = new[] { "/private/input.png" } };
        new UiJobRegistry(new Settings { ImageDownloadBaseFolder = folder }).Add(job);
        job.Emit(new { type = "accepted", generatorExtraTexts = new { gpt2 = "Bright" }, apiKey = "DO-NOT-PUBLISH" });
        job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "https://private/path" }, label = "Private label" });
        job.Emit(new { type = "grid", url = "/private/grid", path = "/private/disk.png" });
        job.MarkDone();
        var record = new UiPublicShareRecord { Snapshot = UiPublicShares.Capture(job) };
        var html = UiPublicShares.Html(record, "asset/", "reuse");
        Assert.Contains("&lt;script&gt;secret prompt&lt;/script&gt;", html);
        Assert.Contains("Contact sheet", html);
        Assert.DoesNotContain("private-user", html);
        Assert.DoesNotContain("/private/", html);
        Assert.DoesNotContain("DO-NOT-PUBLISH", html);
        Assert.DoesNotContain("Private label", html);
        Assert.DoesNotContain("<script>", html);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("https://example.test/private/", "https://example.test/private/")]
    [InlineData("https://example.test/shared/original?token=x", "")]
    [InlineData("https://user@example.test/shared/original", "")]
    [InlineData("http://example.test/shared/original", "")]
    [InlineData("https://example.test/shared/original", "https://example.test/shared")]
    public void PublicAddressCannotUsePrivatePrefixOrCredentials(string url, string privateUrl)
    {
        Assert.Throws<InvalidOperationException>(() => UiPublicShares.BaseUrl(new Settings { UiPublicShareBaseUrl = url, UiPublicBaseUrl = privateUrl }));
    }

    [Fact]
    public void DraftsStayPrivateAndPersistWithoutAnInMemoryIndex()
    {
        var folder = Directory.CreateTempSubdirectory("mic-shares-").FullName;
        try
        {
            var store = new UiPublicShareStore(folder);
            var token = UiPublicShareStore.NewToken();
            store.Save(new UiPublicShareRecord { Token = token, ExpiresAt = 1 });
            Assert.False(new UiPublicShareStore(folder).Get(token)!.Published);
            Assert.Null(store.Get("../../settings"));
            store.PruneExpiredDrafts();
            Assert.Null(store.Get(token));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PromptPagesPersistAcrossPreviewsRestartsAndConcurrentReservations()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-pages-").FullName;
        try
        {
            var snapshot = new UiPublicShareSnapshot("Same prompt text", new(), new(), new());
            var store = new UiPublicShareStore(folder);
            var pages = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => store.GetOrCreatePage("run-a", snapshot))));
            Assert.Single(pages.Select(page => page.Token).Distinct());
            var page = pages[0];
            Assert.False(page.Published);
            store.Save(new UiPublicShareRecord { Token = UiPublicShareStore.NewToken(), PageToken = page.Token,
                JobId = "run-a", Snapshot = snapshot, ExpiresAt = 1 });
            store.PruneExpiredDrafts();
            store = new UiPublicShareStore(folder);
            Assert.Equal(page.Token, store.GetOrCreatePage("run-a", snapshot).Token);
            Assert.False(store.Get(page.Token)!.Published);
            Assert.NotEqual(page.Token, store.GetOrCreatePage("run-b", snapshot).Token);
            Assert.Throws<InvalidOperationException>(() => store.GetOrCreatePage("run-a", snapshot with { Prompt = "Changed" }));
            var share = new UiPublicShareRecord { Token = UiPublicShareStore.NewToken(), PageToken = page.Token,
                JobId = "run-a", Snapshot = snapshot };
            store.PublishPage(share);
            Assert.True(new UiPublicShareStore(folder).PageForShare(share).Published);
            Assert.Throws<InvalidDataException>(() => store.PageForShare(share with { PageToken = UiPublicShareStore.NewToken() }));
            File.Delete(Path.Combine(folder, "UiPublicShares", page.Token + ".json"));
            Assert.Throws<InvalidDataException>(() => store.GetOrCreatePage("run-a", snapshot));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ExistingPublicationsAdoptTheEarliestExactRunAndKeepEveryOldLink()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-legacy-").FullName;
        try
        {
            var store = new UiPublicShareStore(folder);
            var first = new UiPublicShareRecord { Token = new string('b', 64), JobId = "run", State = "sent",
                CreatedAt = 10, Snapshot = new("Prompt", new(), new(), new()) };
            var second = first with { Token = new string('a', 64), CreatedAt = 20, State = "pending" };
            store.Save(first);
            store.Save(second);
            Assert.Equal(first.Token, store.GetOrCreatePage("run", first.Snapshot).Token);
            Assert.True(store.Get(first.Token)!.Published);
            Assert.True(store.Get(second.Token)!.Published);
            Assert.Equal(first.Token, new UiPublicShareStore(folder).GetOrCreatePage("run", first.Snapshot).Token);
            store.Save(second with { Token = new string('c', 64), JobId = "conflicting-run" });
            store.Save(first with { Token = new string('d', 64), JobId = "conflicting-run",
                Snapshot = first.Snapshot with { Prompt = "Different" } });
            Assert.Throws<InvalidOperationException>(() => store.GetOrCreatePage("conflicting-run", first.Snapshot));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void PublicPageSelectionUsesExactAssetSlotsAndKeepsReuseSeparate()
    {
        var record = new UiPublicShareRecord { Generator = "model-b", ImageIndex = 0,
            Snapshot = new("A bright landscape with hills and a lake.",
                new() { new("input", "input", 0, "Input 1"), new("image", "model-a", 0, "Model A · 1"),
                    new("image", "model-a", 1, "Model A · 2"), new("image", "model-b", 0, "Model B · 1"),
                    new("video", "model-c", 0, "Model C · 1"), new("grid", "grid", 0, "Contact sheet") },
                new() { new("Model B", "Use clear daylight.") }, new()) };
        var slot = UiPublicShares.SelectedSlot(record);
        Assert.Equal(3, slot);
        var publicUrl = "https://share.test/shared/original/" + new string('a', 64) + "/";
        Assert.Equal($"[View prompt](<{publicUrl}#output-3>) · [Make your own](<{publicUrl}reuse>)",
            UiPublicShares.Caption(publicUrl, slot));
        Assert.Throws<InvalidDataException>(() => UiPublicShares.SelectedSlot(record with { ImageIndex = 99 }));
        var html = UiPublicShares.Html(record, "asset/", "reuse");
        Assert.Contains("id=\"output-3\"", html);
        Assert.Contains("id=\"output-4\"", html);
        Assert.DoesNotContain("id=\"output-0\"", html);
        Assert.Contains("4 outputs from this prompt.", html);
        Assert.Contains("Selected output", html);
        Assert.Contains("href=\"#prompt\"", html);
        Assert.Contains("href=\"#outputs\"", html);
        Assert.DoesNotContain("<script", html);
        // The browser regression consumes this exact renderer with synthetic content only.
        var fixture = Environment.GetEnvironmentVariable("MIC_PUBLIC_SHARE_HTML_FIXTURE");
        if (!string.IsNullOrEmpty(fixture)) File.WriteAllText(fixture, html);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewConfirmPublicReadAndUncertainDeliveryPreserveExactScope(bool loseResponse)
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-http-").FullName;
        var settings = new Settings { ImageDownloadBaseFolder = folder, LogFilePath = Path.Combine(folder, "test.log"),
            DiscordVibecodersBotToken = new string('t', 60), DiscordVibecodersThreadStorePath = Path.Combine(folder, "threads"),
            EnableGenerationArchive = false, DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/testtoken",
            UiPublicBaseUrl = "https://example.test/private-path", UiPublicShareBaseUrl = "https://example.test/shared/original" };
        var jobs = new UiJobRegistry(settings);
        var job = new UiJob { Prompt = "Public test prompt", CreatorLogin = "alice", GeneratorKeys = new[] { "gpt2" } };
        jobs.Add(job);
        var file = Path.Combine(folder, "result.png");
        File.WriteAllBytes(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII="));
        job.StoreImagePath("gpt2", 0, file, "image/png");
        job.StoreImagePath("gpt2", 1, file, "image/png");
        job.StoreImagePath("grid", 0, file, "image/png");
        job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "/private/image", "/private/image-2" } });
        job.Emit(new { type = "grid", url = "/private/grid" });
        job.MarkDone();
        await using var runner = new UiJobRunner(settings, new MultiClientRunStats(), new RunOptions());
        var visibility = new UiVisibilityStore(settings);
        var sent = new UiDiscordVibecodersStore(settings);
        var handler = new DiscordHandler();
        using var discordHttp = new HttpClient(handler);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        typeof(UiWorkflow).GetMethod("MapPublicShares", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            new object?[] { app, settings, null, jobs, visibility, runner, sent, null,
                (Func<DiscordVibecodersClient>)(() => new DiscordVibecodersClient(settings, discordHttp)) });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        try
        {
            async Task<HttpResponseMessage> Post(string route, params (string Key, string Value)[] values)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new FormUrlEncodedContent(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))) };
                request.Headers.Add("X-MIC-Share", "1");
                return await http.SendAsync(request);
            }
            using var oldSend = await Post("/api/discord/vibecoders", ("jobId", job.Id), ("generator", "gpt2"), ("imageIndex", "0"));
            Assert.Equal(HttpStatusCode.BadRequest, oldSend.StatusCode);
            using var prepared = await Post("/api/discord/vibecoders/prepare", ("jobId", job.Id), ("generator", "gpt2"), ("imageIndex", "0"));
            var preparedText = await prepared.Content.ReadAsStringAsync();
            Assert.True(prepared.IsSuccessStatusCode, preparedText);
            using var draft = JsonDocument.Parse(preparedText);
            var token = draft.RootElement.GetProperty("token").GetString()!;
            var publicUrl = draft.RootElement.GetProperty("publicUrl").GetString()!;
            var publicToken = new Uri(publicUrl).Segments.Last().Trim('/');
            Assert.NotEqual(token, publicToken);
            Assert.False(draft.RootElement.GetProperty("pagePublished").GetBoolean());
            Assert.Equal(publicUrl + "#output-0", draft.RootElement.GetProperty("viewUrl").GetString());
            // Two independently confirmed images reserve the same page before either is published.
            using var preparedSecond = await Post("/api/discord/vibecoders/prepare", ("jobId", job.Id), ("generator", "gpt2"), ("imageIndex", "1"));
            preparedSecond.EnsureSuccessStatusCode();
            using var second = JsonDocument.Parse(await preparedSecond.Content.ReadAsStringAsync());
            var secondToken = second.RootElement.GetProperty("token").GetString()!;
            Assert.NotEqual(token, secondToken);
            Assert.Equal(publicUrl, second.RootElement.GetProperty("publicUrl").GetString());
            Assert.Equal(publicUrl + "#output-1", second.RootElement.GetProperty("viewUrl").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{token}/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/")).StatusCode);
            Assert.Equal(0, handler.Posts);
            Assert.Contains("Public test prompt", await http.GetStringAsync($"/api/discord/vibecoders/preview/{token}/"));
            // Confirmation cannot publish a modified destination under an old preview.
            settings.DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/changed";
            using var changed = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
            Assert.False(changed.IsSuccessStatusCode);
            Assert.Equal(0, handler.Posts);
            settings.DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/testtoken";
            // Simulate a lost response after Discord accepted the request.
            handler.LoseResponse = loseResponse;
            using var confirmed = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
            Assert.Equal(loseResponse ? HttpStatusCode.BadGateway : HttpStatusCode.OK, confirmed.StatusCode);
            Assert.Contains(loseResponse ? "pending" : "sent", await confirmed.Content.ReadAsStringAsync());
            Assert.Equal(1, handler.Posts);
            Assert.Equal(loseResponse ? "pending" : "sent", sent.Snapshot().Records.Single().State);
            var html = await http.GetStringAsync($"/public/{publicToken}/");
            Assert.Contains("Public test prompt", html);
            Assert.DoesNotContain("private-path", html);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/public/{publicToken}/asset/0")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/asset/999")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{token}/")).StatusCode);
            using var repeat = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
            Assert.False(repeat.IsSuccessStatusCode);
            Assert.Equal(1, handler.Posts);
            Assert.DoesNotContain("private-path", handler.Body);
            Assert.Contains("View prompt", handler.Body);
            Assert.Contains("allowed_mentions", handler.Body);
            Assert.Contains("flags", handler.Body);
            Assert.Contains(publicUrl + "#output-0", handler.Body);
            // Cancelling another preview cannot change the existing page or its address.
            using var cancelledPreview = await Post("/api/discord/vibecoders/prepare", ("jobId", job.Id), ("generator", "gpt2"), ("imageIndex", "1"));
            cancelledPreview.EnsureSuccessStatusCode();
            using var cancelled = JsonDocument.Parse(await cancelledPreview.Content.ReadAsStringAsync());
            Assert.True(cancelled.RootElement.GetProperty("pagePublished").GetBoolean());
            Assert.Equal(publicUrl, cancelled.RootElement.GetProperty("publicUrl").GetString());
            handler.LoseResponse = false;
            using var confirmedSecond = await Post("/api/discord/vibecoders", ("token", secondToken), ("confirmed", "true"));
            Assert.True(confirmedSecond.IsSuccessStatusCode, await confirmedSecond.Content.ReadAsStringAsync());
            Assert.Equal(2, handler.Posts);
            Assert.Contains(publicUrl + "#output-1", handler.Body);
            Assert.Equal(html, await http.GetStringAsync($"/public/{publicToken}/"));
            using var duplicateImage = await Post("/api/discord/vibecoders/prepare", ("jobId", job.Id), ("generator", "gpt2"), ("imageIndex", "1"));
            Assert.Equal(HttpStatusCode.Conflict, duplicateImage.StatusCode);
            visibility.Hide(new UiHiddenResource { Kind = "image", JobId = job.Id, Generator = "gpt2", ImageIndex = 1,
                HiddenByLogin = "alice", HiddenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/asset/0")).StatusCode);
        }
        finally { await app.StopAsync(); Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PublicReuseRequiresLoginAndCurrentEnvironmentMembership()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-login-").FullName;
        var settings = new Settings { ImageDownloadBaseFolder = folder, LogFilePath = Path.Combine(folder, "test.log"),
            EnableGenerationArchive = false, UiAuthFilePath = Path.Combine(folder, "auth.json"),
            UiEnvironmentId = "original", UiEnvironmentRegistryPath = Path.Combine(folder, "environments.json"),
            UiPublicBaseUrl = "https://example.test/private-path", UiPublicShareBaseUrl = "https://example.test/shared/original" };
        File.WriteAllText(settings.UiAuthFilePath, JsonSerializer.Serialize(new { version = 2, enabled = true,
            secret = new string('s', 40), accounts = new[] { new { username = "alice", passwordHash = "pbkdf2-sha256$600000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2("test-password", new byte[16], 600000, HashAlgorithmName.SHA256, 32)) } } }));
        File.WriteAllText(settings.UiEnvironmentRegistryPath, """{"version":1,"environments":[{"id":"original","slug":"private-path","name":"Original","original":true,"members":["alice"]}]}""");
        var auth = UiAuth.CreateFromSettings(settings)!;
        var environments = new UiEnvironmentRegistry(settings.UiEnvironmentRegistryPath);
        var jobs = new UiJobRegistry(settings);
        var job = new UiJob { Prompt = "Public prompt" };
        jobs.Add(job);
        job.MarkDone();
        var token = UiPublicShareStore.NewToken();
        new UiPublicShareStore(folder).Save(new UiPublicShareRecord { Token = token, JobId = job.Id, State = "sent",
            Snapshot = new("Public prompt", new(), new(), new()) });
        await using var runner = new UiJobRunner(settings, new MultiClientRunStats(), new RunOptions());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        typeof(UiWorkflow).GetMethod("MapPublicShares", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            new object?[] { app, settings, auth, jobs, new UiVisibilityStore(settings), runner,
                new UiDiscordVibecodersStore(settings), environments, null });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        var route = $"/public/{token}/reuse";
        try
        {
            var anonymous = await http.GetStringAsync(route);
            Assert.Contains("Ask Ernie in Discord", anonymous);
            Assert.DoesNotContain("private-path", anonymous);
            async Task<HttpResponseMessage> Login(string password, string origin)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "alice", ["password"] = password }) };
                request.Headers.Add("Origin", origin);
                return await http.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("test-password", "https://other.test")).StatusCode);
            var wrong = await Login("wrong", "https://example.test");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.False(wrong.Headers.Contains("Set-Cookie"));
            var success = await Login("test-password", "https://example.test");
            Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
            Assert.Equal(settings.UiPublicBaseUrl + "/?shared=" + token, success.Headers.Location!.ToString());
            var cookie = success.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            using var loggedIn = new HttpRequestMessage(HttpMethod.Get, route);
            loggedIn.Headers.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.Redirect, (await http.SendAsync(loggedIn)).StatusCode);
            var environment = environments.Get("original");
            environment.Members.Clear();
            environments.Save(environment);
            using var revoked = new HttpRequestMessage(HttpMethod.Get, route);
            revoked.Headers.Add("Cookie", cookie);
            Assert.Contains("Ask Ernie", await (await http.SendAsync(revoked)).Content.ReadAsStringAsync());
            var denied = await Login("test-password", "https://example.test");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
        finally { await app.StopAsync(); Directory.Delete(folder, true); }
    }

    private sealed class DiscordHandler : HttpMessageHandler
    {
        public int Posts;
        public bool LoseResponse;
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/threads")) {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bot", request.Headers.Authorization?.Scheme);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = "999", guild_id = "111", parent_id = "222", type = 11, name = DiscordDailyThreads.Name(DiscordDailyThreads.Day(DateTimeOffset.UtcNow)), thread_metadata = new { locked = false } })) };
            }
            if (request.Method == HttpMethod.Get) {
                if (request.RequestUri.AbsolutePath.EndsWith("/channels/999"))
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = "999", guild_id = "111", parent_id = "222", type = 11, name = DiscordDailyThreads.Name(DiscordDailyThreads.Day(DateTimeOffset.UtcNow)), thread_metadata = new { locked = false } })) };
                var data = request.RequestUri.AbsolutePath.Contains("/guilds/") ? "{\"id\":\"111\",\"name\":\"Test server\"}"
                    : request.RequestUri.AbsolutePath.Contains("/channels/") ? "{\"id\":\"222\",\"guild_id\":\"111\",\"type\":0,\"name\":\"test-channel\"}"
                    : "{\"guild_id\":\"111\",\"channel_id\":\"222\"}";
                return new(HttpStatusCode.OK) { Content = new StringContent(data) };
            }
            Posts++;
            Body = Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Assert.Equal("?wait=true&thread_id=999", request.RequestUri!.Query);
            if (LoseResponse) throw new HttpRequestException("simulated response loss");
            var parts = (MultipartFormDataContent)request.Content!;
            var payload = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "payload_json");
            using var json = JsonDocument.Parse(await payload.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = "333", channel_id = "999", content = json.RootElement.GetProperty("content").GetString(), attachments = new[] { new { id = "444" } } })) };
        }
    }
}
