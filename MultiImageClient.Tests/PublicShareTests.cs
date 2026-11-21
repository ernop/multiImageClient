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
                (Func<DiscordVibecodersClient>)(() => new DiscordVibecodersClient(settings, discordHttp)), null });
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
                new UiDiscordVibecodersStore(settings), environments, null, null });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        var route = $"/public/{token}/reuse";
        try
        {
            using var anonymousResponse = await http.GetAsync(route);
            var anonymous = await anonymousResponse.Content.ReadAsStringAsync();
            Assert.Contains("Ask Ernie in Discord", anonymous);
            Assert.DoesNotContain("private-path", anonymous);
            Assert.Contains("<form id=\"login\" method=\"post\">", anonymous);
            Assert.DoesNotContain("action=\"reuse\"", anonymous);
            Assert.Contains("<script>" + UiWorkflow.ShareLinkLoginScript + "</script>", anonymous);
            var policy = anonymousResponse.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("connect-src 'self'; script-src 'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(UiWorkflow.ShareLinkLoginScript))) + "'", policy);
            async Task<HttpResponseMessage> Login(string password, string origin, bool loginHeader = true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "alice", ["password"] = password }) };
                request.Headers.Add("Origin", origin);
                if (loginHeader) request.Headers.Add("X-Mic-Login", "1");
                return await http.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("test-password", "https://other.test")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("test-password", "null")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Login("test-password", "https://example.test", loginHeader: false)).StatusCode);
            var wrong = await Login("wrong", "https://example.test");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.False(wrong.Headers.Contains("Set-Cookie"));
            Assert.Contains("Ask Ernie in Discord", await wrong.Content.ReadAsStringAsync());
            var success = await Login("test-password", "https://example.test");
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            using (var destination = JsonDocument.Parse(await success.Content.ReadAsStringAsync()))
                Assert.Equal(settings.UiPublicBaseUrl + "/?shared=" + token, destination.RootElement.GetProperty("destination").GetString());
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

    [Fact]
    public async Task SeriesShareLifecyclePublishesReaderBatchesAndEnforcesVisibility()
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-series-").FullName;
        var settings = new Settings
        {
            ImageDownloadBaseFolder = folder,
            LogFilePath = Path.Combine(folder, "test.log"),
            EnableGenerationArchive = false,
            UiPublicBaseUrl = "https://example.test/private-path",
            UiPublicShareBaseUrl = "https://example.test/shared/original",
            DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/testtoken",
            DiscordVibecodersBotToken = new string('t', 60),
            DiscordVibecodersThreadStorePath = Path.Combine(folder, "threads"),
            UiCommunityDbPath = Path.Combine(folder, "community.sqlite3"),
        };
        var jobs = new UiJobRegistry(settings);
        var community = new UiCommunityStore(settings);
        var groupId = "grp123456789";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var exchange = community.StartClaudePromptExchange(
            "local",
            "alice",
            "claude-opus-5-5",
            PromptSplit.RecordedInstruction("Develop this story through 3 pages.", "Ink and sunlit watercolor."),
            "A short story premise about a lighthouse keeper and a storm.",
            PromptSplit.SystemPrompt,
            "wire prompt",
            nowMs,
            PromptSplit.Purpose);
        var planJson = JsonSerializer.Serialize(new
        {
            imageCount = 3,
            reason = "Three acts.",
            shape = "landscape",
            images = new[]
            {
                new { title = "The warning bell", prompt = "Page 1 prompt text." },
                new { title = "The rescue boat", prompt = "Page 2 prompt text." },
                new { title = "Dawn on the pier", prompt = "Page 3 prompt text." },
            },
        });
        community.CompleteClaudePromptExchange(exchange.Id, planJson, planJson, "", nowMs + 10);
        var exchangeId = exchange.Id;

        var png1 = WriteSolidPng(Path.Combine(folder, "p1.png"), 64, 48, 220, 80, 60);
        var png2a = WriteSolidPng(Path.Combine(folder, "p2a.png"), 64, 48, 40, 160, 80);
        var png2b = WriteSolidPng(Path.Combine(folder, "p2b.png"), 64, 48, 60, 100, 220);
        var png3 = WriteSolidPng(Path.Combine(folder, "p3.png"), 64, 48, 230, 190, 40);

        var partJobs = new UiJob[3];
        var titles = new[] { "The warning bell", "The rescue boat", "Dawn on the pier" };
        var prompts = new[] { "Page 1 prompt text.", "Page 2 prompt text.", "Page 3 prompt text." };
        for (int i = 0; i < 3; i++)
        {
            var job = new UiJob
            {
                Prompt = prompts[i],
                CreatorLogin = "alice",
                GeneratorKeys = new[] { "gpt2" },
                Split = new UiJobSplit(groupId, exchangeId, i + 1, 3, titles[i], "claude-opus-5-5", "Three acts."),
            };
            jobs.Add(job);
            if (i == 0)
            {
                job.StoreImagePath("gpt2", 0, png1, "image/png");
                job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "/private/p1" } });
            }
            else if (i == 1)
            {
                // Two variants on page 2 so we verify non-zero variant selection (index 1).
                job.StoreImagePath("gpt2", 0, png2a, "image/png");
                job.StoreImagePath("gpt2", 1, png2b, "image/png");
                job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "/private/p2a", "/private/p2b" } });
            }
            else
            {
                job.StoreImagePath("gpt2", 0, png3, "image/png");
                job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { "/private/p3" } });
            }
            job.MarkDone();
            partJobs[i] = job;
        }

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
                (Func<DiscordVibecodersClient>)(() => new DiscordVibecodersClient(settings, discordHttp)), community });
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

            // Prepare series selecting variant index 1 for page 2.
            using var prepared = await Post("/api/discord/vibecoders/prepare-series",
                ("groupId", groupId), ("generator", "gpt2"), ("imageIndices", "0,1,0"));
            var preparedText = await prepared.Content.ReadAsStringAsync();
            Assert.True(prepared.IsSuccessStatusCode, preparedText);
            using var draft = JsonDocument.Parse(preparedText);
            var token = draft.RootElement.GetProperty("token").GetString()!;
            var publicUrl = draft.RootElement.GetProperty("publicUrl").GetString()!;
            var publicToken = new Uri(publicUrl).Segments.Last().Trim('/');
            Assert.Equal("series", draft.RootElement.GetProperty("kind").GetString());
            Assert.Equal(3, draft.RootElement.GetProperty("pageCount").GetInt32());
            Assert.Equal(1, draft.RootElement.GetProperty("batches").GetArrayLength());

            // Not public before confirmation.
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/")).StatusCode);
            Assert.Equal(0, handler.Posts);

            // Preview HTML contains source prompt, page titles, reader script, and no private URL.
            var previewHtml = await http.GetStringAsync($"/api/discord/vibecoders/preview/{token}/");
            Assert.Contains("A short story premise about a lighthouse keeper and a storm.", previewHtml);
            Assert.Contains("The warning bell", previewHtml);
            Assert.Contains("The rescue boat", previewHtml);
            Assert.Contains("Dawn on the pier", previewHtml);
            Assert.Contains("Ink and sunlit watercolor.", previewHtml);
            Assert.DoesNotContain("private-path", previewHtml);

            // Confirm sends one Discord message with all 3 original attachments.
            using var confirmed = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
            var confirmedText = await confirmed.Content.ReadAsStringAsync();
            Assert.True(confirmed.IsSuccessStatusCode, confirmedText);
            Assert.Equal(1, handler.Posts);
            Assert.Contains("files[0]", handler.Body);
            Assert.Contains("files[1]", handler.Body);
            Assert.Contains("files[2]", handler.Body);
            Assert.Contains("3 pages · [Read 1–3]", handler.Contents.Single());
            Assert.Contains(publicUrl, handler.Contents.Single());
            Assert.DoesNotContain("private-path", handler.Body);

            // Public series page serves with exact SeriesReaderScriptHash CSP.
            using var publicResp = await http.GetAsync($"/public/{publicToken}/");
            Assert.Equal(HttpStatusCode.OK, publicResp.StatusCode);
            var csp = publicResp.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains(UiPublicShares.SeriesReaderScriptHash, csp);
            var publicHtml = await publicResp.Content.ReadAsStringAsync();
            Assert.Contains("Page 1 of 3", publicHtml);
            Assert.Contains("All pages", publicHtml);
            Assert.Contains("asset/3", publicHtml);
            var seriesFixture = Environment.GetEnvironmentVariable("MIC_PUBLIC_SERIES_SHARE_HTML_FIXTURE");
            if (!string.IsNullOrEmpty(seriesFixture)) File.WriteAllText(seriesFixture, publicHtml);

            // Page 2 asset matches variant 1 (png2b), NOT variant 0 (png2a).
            var servedP2 = await http.GetByteArrayAsync($"/public/{publicToken}/asset/1");
            Assert.Equal(await File.ReadAllBytesAsync(png2b), servedP2);
            Assert.NotEqual(await File.ReadAllBytesAsync(png2a), servedP2);

            // On-demand per-model contact sheet (slot 3 = 0 inputs + 3 pages) renders as a real PNG.
            using var gridResp = await http.GetAsync($"/public/{publicToken}/asset/3");
            Assert.Equal(HttpStatusCode.OK, gridResp.StatusCode);
            Assert.Equal("image/png", gridResp.Content.Headers.ContentType?.MediaType);
            var gridBytes = await gridResp.Content.ReadAsByteArrayAsync();
            Assert.True(gridBytes.Length > 100);
            Assert.Equal(0x89, gridBytes[0]);

            // Out-of-range asset returns 404.
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/asset/4")).StatusCode);

            // Make your own reuse API returns the original source prompt.
            using var reuseResp = await http.GetAsync($"/api/public-shares/{publicToken}/reuse");
            Assert.Equal(HttpStatusCode.OK, reuseResp.StatusCode);
            using var reuseJson = JsonDocument.Parse(await reuseResp.Content.ReadAsStringAsync());
            Assert.Equal("A short story premise about a lighthouse keeper and a storm.", reuseJson.RootElement.GetProperty("prompt").GetString());

            // Duplicate prepare for the same series + generator returns 409 Conflict.
            using var dup = await Post("/api/discord/vibecoders/prepare-series",
                ("groupId", groupId), ("generator", "gpt2"), ("imageIndices", "0,1,0"));
            Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

            // Hiding one page job hides the entire series public page and assets.
            visibility.Hide(new UiHiddenResource
            {
                Kind = "prompt",
                JobId = partJobs[1].Id,
                HiddenByLogin = "alice",
                HiddenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/asset/0")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/public/{publicToken}/asset/3")).StatusCode);
        }
        finally { await app.StopAsync(); Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeriesShareMultiBatchPartitionsAndHaltsOnUncertainDelivery(bool failSecondBatch)
    {
        var folder = Directory.CreateTempSubdirectory("mic-share-multibatch-").FullName;
        var settings = new Settings
        {
            ImageDownloadBaseFolder = folder,
            LogFilePath = Path.Combine(folder, "test.log"),
            EnableGenerationArchive = false,
            UiPublicBaseUrl = "https://example.test/private-path",
            UiPublicShareBaseUrl = "https://example.test/shared/original",
            DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/testtoken",
            DiscordVibecodersBotToken = new string('t', 60),
            DiscordVibecodersThreadStorePath = Path.Combine(folder, "threads"),
        };
        var jobs = new UiJobRegistry(settings);
        var groupId = "grp_large_001";
        // Three 10 MiB files = 30 MiB > 24 MiB -> balanced into 2 batches: [0,1] (20 MiB) + [2] (10 MiB).
        for (int i = 0; i < 3; i++)
        {
            var path = WriteLargeBlobPng(Path.Combine(folder, $"big{i}.png"), 10 * 1024 * 1024);
            var job = new UiJob
            {
                Prompt = $"Large page {i + 1}",
                CreatorLogin = "alice",
                GeneratorKeys = new[] { "gpt2" },
                Split = new UiJobSplit(groupId, "", i + 1, 3, $"Act {i + 1}", "claude-opus-5-5", "Three acts."),
            };
            jobs.Add(job);
            job.StoreImagePath("gpt2", 0, path, "image/png");
            job.Emit(new { type = "gen-result", gen = "gpt2", ok = true, images = new[] { $"/private/big{i}" } });
            job.MarkDone();
        }
        await using var runner = new UiJobRunner(settings, new MultiClientRunStats(), new RunOptions());
        var visibility = new UiVisibilityStore(settings);
        var sent = new UiDiscordVibecodersStore(settings);
        var handler = new DiscordHandler { FailOnPostNumber = failSecondBatch ? 2 : 0 };
        using var discordHttp = new HttpClient(handler);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        typeof(UiWorkflow).GetMethod("MapPublicShares", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            new object?[] { app, settings, null, jobs, visibility, runner, sent, null,
                (Func<DiscordVibecodersClient>)(() => new DiscordVibecodersClient(settings, discordHttp)), null });
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
            using var prepared = await Post("/api/discord/vibecoders/prepare-series",
                ("groupId", groupId), ("generator", "gpt2"), ("imageIndices", "0,0,0"));
            var preparedText = await prepared.Content.ReadAsStringAsync();
            Assert.True(prepared.IsSuccessStatusCode, preparedText);
            using var draft = JsonDocument.Parse(preparedText);
            var token = draft.RootElement.GetProperty("token").GetString()!;
            var batches = draft.RootElement.GetProperty("batches");
            Assert.Equal(2, batches.GetArrayLength());
            Assert.Equal(1, batches[0].GetProperty("startPage").GetInt32());
            Assert.Equal(2, batches[0].GetProperty("endPage").GetInt32());
            Assert.Equal(3, batches[1].GetProperty("startPage").GetInt32());
            Assert.Equal(3, batches[1].GetProperty("endPage").GetInt32());

            using var confirmed = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
            var confirmedText = await confirmed.Content.ReadAsStringAsync();
            Assert.Equal(2, handler.Posts);
            Assert.Contains("Pages 1–2 of 3", handler.Contents[0]);
            if (failSecondBatch)
            {
                Assert.Equal(HttpStatusCode.BadGateway, confirmed.StatusCode);
                Assert.Contains("pending", confirmedText);
                Assert.Equal("pending", sent.Snapshot().Records.Single().State);
                // Retry is blocked while uncertain delivery is pending.
                using var retry = await Post("/api/discord/vibecoders", ("token", token), ("confirmed", "true"));
                Assert.False(retry.IsSuccessStatusCode);
                Assert.Equal(2, handler.Posts);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
                Assert.Contains("Page 3 of 3", handler.Contents[1]);
                Assert.Equal("sent", sent.Snapshot().Records.Single().State);
            }
        }
        finally { await app.StopAsync(); Directory.Delete(folder, true); }
    }

    private static string WriteSolidPng(string path, int width, int height, byte r, byte g, byte b)
    {
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));
        surface.Canvas.Clear(new SkiaSharp.SKColor(r, g, b));
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static string WriteLargeBlobPng(string path, int minBytes)
    {
        var tiny = WriteSolidPng(path, 8, 8, 120, 140, 160);
        // Append trailing bytes after IEND so the file exceeds the batch budget without slow compression.
        using var fs = new FileStream(tiny, FileMode.Append, FileAccess.Write);
        fs.SetLength(minBytes);
        return tiny;
    }

    private sealed class DiscordHandler : HttpMessageHandler
    {
        public int Posts;
        public bool LoseResponse;
        public int FailOnPostNumber;
        public string Body = "";
        public List<string> Bodies { get; } = new();
        public List<string> Contents { get; } = new();
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
            Assert.Equal("?wait=true&thread_id=999", request.RequestUri!.Query);
            var parts = (MultipartFormDataContent)request.Content!;
            var payload = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "payload_json");
            var payloadText = await payload.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(payloadText);
            var content = json.RootElement.GetProperty("content").GetString() ?? "";
            var fileNames = parts
                .Select(p => p.Headers.ContentDisposition?.Name?.Trim('"') ?? "")
                .Where(n => n.StartsWith("files[", StringComparison.Ordinal))
                .ToList();
            Body = string.Join(" ", fileNames) + "\n" + payloadText;
            Bodies.Add(Body);
            Contents.Add(content);
            if (LoseResponse || (FailOnPostNumber > 0 && Posts == FailOnPostNumber))
                throw new HttpRequestException("simulated response loss");
            var attachments = fileNames.Select((_, i) => new { id = $"44{Posts}{i}" }).ToArray();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = $"33{Posts}", channel_id = "999", content, attachments })) };
        }
    }
}
