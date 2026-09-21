using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using MultiImageClient;

namespace MultiImageClient.Tests;

public sealed class DiscordAccountRequestTests
{
    [Fact]
    public async Task OnlyTheDmRecipientCanCreateAnAccountAndTheLinkWorksOnce()
    {
        using var fixture = new Fixture();
        var originalAuth = File.ReadAllText(fixture.Settings.UiAuthFilePath);
        var service = fixture.Service();
        var delivery = await service.RequestAsync("@Alice.Discord", "192.0.2.1", new string('a', 64), default);
        Assert.Equal("sent", delivery.State);
        Assert.Empty(fixture.Auth.LoginLinks!.List());
        Assert.Equal(1, fixture.Discord.Messages);
        Assert.DoesNotContain("private-path", fixture.Discord.Content);
        Assert.Contains("/shared/original/signup/claim#", fixture.Discord.Content);
        Assert.DoesNotContain(fixture.Discord.Token, JsonSerializer.Serialize(delivery));
        var saved = File.ReadAllText(fixture.StorePath);
        Assert.DoesNotContain(fixture.Discord.Token[33..], saved);
        Assert.DoesNotContain("192.0.2.1", saved);
        await service.RequestAsync("alice.discord", "192.0.2.2", "", default);
        Assert.Equal(1, fixture.Discord.Messages);
        var signedIn = await fixture.Service().RedeemAsync(fixture.Discord.Token, default);
        Assert.True(fixture.Auth.TryValidateCookie(signedIn.Cookie, out var login));
        Assert.Equal("alice.discord", login);
        Assert.True(fixture.Registry.CanEnter(UiDiscordAccountRequests.EnvironmentId, login));
        Assert.False(fixture.Registry.CanEnter("original", login));
        Assert.Equal("https://share.test/vibecoders-ai-generation/?shared=" + new string('a', 64), signedIn.Destination);
        Assert.Equal("333", fixture.Auth.LoginLinks.List().Single().DiscordUserId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(fixture.Discord.Token, default));
        Assert.Equal(originalAuth, File.ReadAllText(fixture.Settings.UiAuthFilePath));
    }

    [Fact]
    public async Task SimultaneousRedemptionsIssueOnlyOneSession()
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        await service.RequestAsync("alice.discord", "ip", "", default);
        fixture.Discord.BlockMemberLookup = true;
        var first = service.RedeemAsync(fixture.Discord.Token, default);
        await fixture.Discord.LookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(fixture.Discord.Token, default));
        }
        finally { fixture.Discord.ContinueLookup.SetResult(); }
        Assert.NotNull(await first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(fixture.Discord.Token, default));
        Assert.Single(fixture.Auth.LoginLinks!.List());
    }

    [Fact]
    public async Task SignupCannotTargetTheOriginalOrWriteFromAnotherInstance()
    {
        using var fixture = new Fixture();
        var original = fixture.Registry.Get("original");
        original.DiscordAccountRequests = true;
        Assert.Throws<InvalidDataException>(() => fixture.Registry.Save(original));
        fixture.Settings.UiEnvironmentController = false;
        fixture.Settings.UiEnvironmentId = UiDiscordAccountRequests.EnvironmentId;
        var worker = fixture.Service();
        Assert.False(worker.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RequestAsync("alice.discord", "ip", "", default));
        Assert.Equal(0, fixture.Discord.Messages);
        Assert.Empty(fixture.Auth.LoginLinks!.List());
    }

    [Fact]
    public async Task UsernameChangesKeepOneAccountAndDoNotRotateExistingCredentials()
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        await service.RequestAsync("alice.discord", "ip", "", default);
        var first = await service.RedeemAsync(fixture.Discord.Token, default);
        var account = fixture.Auth.LoginLinks!.List().Single();
        fixture.Now += TimeSpan.FromMinutes(6);
        fixture.Discord.Username = "renamed.alice";
        await service.RequestAsync("renamed.alice", "ip", "", default);
        var next = await service.RedeemAsync(fixture.Discord.Token, default);
        Assert.Equal(first.Cookie, next.Cookie);
        Assert.Equal(account.Id, fixture.Auth.LoginLinks.List().Single().Id);
        Assert.Equal(account.TokenHash, fixture.Auth.LoginLinks.List().Single().TokenHash);
        Assert.Equal("alice.discord", next.Login);
    }

    [Theory]
    [InlineData("nickname")]
    [InlineData("prefix")]
    [InlineData("duplicate")]
    [InlineData("pending")]
    [InlineData("bot")]
    [InlineData("no-access")]
    [InlineData("wrong-member")]
    public async Task IneligibleOrAmbiguousMembersReceiveNoDm(string failure)
    {
        using var fixture = new Fixture();
        fixture.Discord.Failure = failure;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RequestAsync("alice.discord", "ip", "", default));
        Assert.Equal(0, fixture.Discord.DmOpens);
        Assert.Equal(0, fixture.Discord.Messages);
        Assert.Empty(fixture.Auth.LoginLinks!.List());
    }

    [Theory]
    [InlineData("no-access")]
    [InlineData("pending")]
    [InlineData("destination")]
    public async Task RedemptionChecksCurrentChannelAccessAndDestination(string failure)
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        await service.RequestAsync("alice.discord", "ip", "", default);
        fixture.Discord.Failure = failure;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(fixture.Discord.Token, default));
        Assert.Empty(fixture.Auth.LoginLinks!.List());
    }

    [Fact]
    public async Task ExpiredLinksCannotCreateAccounts()
    {
        using var fixture = new Fixture();
        await fixture.Service().RequestAsync("alice.discord", "ip", "", default);
        fixture.Now += TimeSpan.FromMinutes(30);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RedeemAsync(fixture.Discord.Token, default));
        Assert.Empty(fixture.Auth.LoginLinks!.List());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevocationAndMembershipRemovalCannotBeUndoneByAnotherRequest(bool revoke)
    {
        using var fixture = new Fixture();
        var service = fixture.Service();
        await service.RequestAsync("alice.discord", "ip", "", default);
        await service.RedeemAsync(fixture.Discord.Token, default);
        var account = fixture.Auth.LoginLinks!.List().Single();
        if (revoke) fixture.Auth.LoginLinks.Revoke(account.Id);
        else { var environment = fixture.Registry.Get(UiDiscordAccountRequests.EnvironmentId); environment.Members.Clear(); fixture.Registry.Save(environment); }
        fixture.Now += TimeSpan.FromMinutes(6);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync("alice.discord", "ip", "", default));
        Assert.Equal(1, fixture.Discord.Messages);
    }

    [Fact]
    public async Task MatchingAnExistingSiteNameNeverClaimsThatAccount()
    {
        using var fixture = new Fixture();
        var existing = fixture.Auth.LoginLinks!.Create("alice.discord", _ => { });
        await fixture.Service().RequestAsync("alice.discord", "ip", "", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RedeemAsync(fixture.Discord.Token, default));
        Assert.Null(fixture.Auth.LoginLinks.List().Single().DiscordUserId);
        Assert.True(fixture.Auth.TryLoginLink(existing.Token, "ip", out _, out _, out _));
    }

    [Theory]
    [InlineData("lost-response", "pending", true)]
    [InlineData("dm-blocked", "failed", false)]
    [InlineData("wrong-recipient", "pending", false)]
    public async Task DeliveryFailuresNeverExposeLinksOrAutomaticallyResend(string failure, string state, bool redeemable)
    {
        using var fixture = new Fixture();
        fixture.Discord.Failure = failure;
        var result = await fixture.Service().RequestAsync("alice.discord", "ip", "", default);
        Assert.Equal(state, result.State);
        Assert.DoesNotContain("claim#", result.Message);
        if (failure == "wrong-recipient") Assert.Equal(0, fixture.Discord.Messages);
        else Assert.Equal(1, fixture.Discord.Messages);
        if (redeemable)
        {
            fixture.Discord.Failure = "";
            Assert.NotNull(await fixture.Service().RedeemAsync(fixture.Discord.Token, default));
        }
        else if (fixture.Discord.Token.Length > 0)
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RedeemAsync(fixture.Discord.Token, default));
    }

    [Fact]
    public async Task RequestLimitsPersistAcrossRestartsAndApplyBeforeMemberLookup()
    {
        using var fixture = new Fixture();
        fixture.Discord.Failure = "nickname";
        for (var i = 0; i < 12; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RequestAsync("alice.discord", "ip", "", default));
        var searches = fixture.Discord.Searches;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RequestAsync("alice.discord", "ip", "", default));
        Assert.Contains("Too many", error.Message);
        Assert.Equal(searches, fixture.Discord.Searches);
    }

    [Fact]
    public async Task InterruptedActivationResumesOnlyItsRecordedAccountAndNeverRestoresRemovedMembership()
    {
        using var fixture = new Fixture();
        await fixture.Service().RequestAsync("alice.discord", "ip", "", default);
        var id = new string('b', 32);
        var doc = JsonNode.Parse(File.ReadAllText(fixture.StorePath))!;
        var ticket = doc["tickets"]![0]!;
        ticket["state"] = "activating";
        ticket["accountId"] = id;
        ticket["login"] = "alice.discord";
        ticket["createsAccount"] = true;
        File.WriteAllText(fixture.StorePath, doc.ToJsonString());
        fixture.Auth.LoginLinks!.CreateDiscordAccount(id, "333", "alice.discord");
        var signedIn = await fixture.Service().RedeemAsync(fixture.Discord.Token, default);
        Assert.Equal(id, fixture.Auth.LoginLinks.List().Single().Id);
        Assert.True(fixture.Auth.TryValidateCookie(signedIn.Cookie, out _));
        ticket["state"] = "granting";
        File.WriteAllText(fixture.StorePath, doc.ToJsonString());
        var environment = fixture.Registry.Get(UiDiscordAccountRequests.EnvironmentId);
        environment.Members.Clear(); fixture.Registry.Save(environment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().RedeemAsync(fixture.Discord.Token, default));
        Assert.Empty(fixture.Registry.Get(UiDiscordAccountRequests.EnvironmentId).Members);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0, false)]
    [InlineData(1024, 0, 0, 0, 0, 0, true)]
    [InlineData(1024, 0, 1024, 0, 0, 0, false)]
    [InlineData(0, 0, 1024, 1024, 0, 0, true)]
    [InlineData(0, 0, 0, 1024, 0, 1024, false)]
    [InlineData(0, 0, 0, 0, 1024, 0, true)]
    [InlineData(8, 0, 1024, 0, 0, 1024, true)]
    public void ChannelAccessAppliesRoleAndMemberOverrides(ulong basePermissions, ulong everyoneAllow, ulong everyoneDeny,
        ulong roleAllow, ulong userAllow, ulong userDeny, bool expected)
    {
        using var member = JsonDocument.Parse("""{"user":{"id":"333"},"roles":["444"]}""");
        using var guild = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "111", owner_id = "999", roles = new[] {
            new { id = "111", permissions = basePermissions.ToString() }, new { id = "444", permissions = "0" } } }));
        using var channel = JsonDocument.Parse(JsonSerializer.Serialize(new { guild_id = "111", permission_overwrites = new[] {
            new { id = "111", type = 0, allow = everyoneAllow.ToString(), deny = everyoneDeny.ToString() },
            new { id = "444", type = 0, allow = roleAllow.ToString(), deny = "0" },
            new { id = "333", type = 1, allow = userAllow.ToString(), deny = userDeny.ToString() } } }));
        Assert.Equal(expected, DiscordAccountRequestsClient.CanViewChannel(member.RootElement, guild.RootElement, channel.RootElement));
    }

    [Fact]
    public async Task AnonymousHttpFlowRequiresSameOriginAndNeverSignsInTheRequestingBrowser()
    {
        using var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        UiDiscordAccountEndpoints.Map(app, fixture.Settings, fixture.Auth, fixture.Registry, requests: fixture.Service());
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        try
        {
            async Task<HttpResponseMessage> Post(string route, string key, string value, string origin = "https://share.test")
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route)
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { [key] = value }) };
                request.Headers.Add("Origin", origin);
                return await http.SendAsync(request);
            }
            var page = await http.GetStringAsync("/public/signup");
            Assert.Contains("Discord username", page);
            Assert.DoesNotContain("private-path", page);
            Assert.Equal(HttpStatusCode.Forbidden, (await Post("/public/signup/request", "username", "alice.discord", "https://other.test")).StatusCode);
            Assert.Equal(0, fixture.Discord.Messages);
            using var requested = await Post("/public/signup/request", "username", "alice.discord");
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
            Assert.False(requested.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain(fixture.Discord.Token, await requested.Content.ReadAsStringAsync());
            Assert.Empty(fixture.Auth.LoginLinks!.List());
            using var claimPage = await http.GetAsync("/public/signup/claim");
            Assert.Equal(HttpStatusCode.OK, claimPage.StatusCode);
            Assert.False(claimPage.Headers.Contains("Set-Cookie"));
            Assert.Empty(fixture.Auth.LoginLinks.List());
            Assert.Contains("no-store", claimPage.Headers.CacheControl!.ToString());
            Assert.Contains("sha256-", claimPage.Headers.GetValues("Content-Security-Policy").Single());
            using var claim = await Post("/public/signup/claim", "token", fixture.Discord.Token);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            var cookie = claim.Headers.GetValues("Set-Cookie").Single();
            Assert.Contains("secure", cookie);
            Assert.Contains("httponly", cookie);
            Assert.Contains("path=/", cookie);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post("/public/signup/claim", "token", fixture.Discord.Token)).StatusCode);
            var environment = fixture.Registry.Get(UiDiscordAccountRequests.EnvironmentId);
            environment.DiscordAccountRequests = false; fixture.Registry.Save(environment);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/public/signup")).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void PublicSignupRoutesAndPagesDoNotExposePrivateNavigation()
    {
        using var fixture = new Fixture();
        foreach (var route in new[] { "/public/signup", "/public/signup/claim" }) Assert.True(UiPublicShares.IsPublicRequest(route, "GET"));
        Assert.True(UiPublicShares.IsPublicRequest("/public/signup/request", "POST"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/signup/settings", "GET"));
        Assert.False(UiPublicShares.IsPublicRequest("/public/signup/request", "GET"));
        Assert.Equal("https://share.test/shared/original/signup", UiDiscordAccountRequests.PublicUrl(fixture.Settings, fixture.Auth, fixture.Registry));
        var html = UiPublicShares.Html(new UiPublicShareRecord { Snapshot = new("Public prompt", new() { new("image", "model", 0, "Model") }, new(), new()) },
            "asset/", "reuse", requestAccountUrl: UiDiscordAccountRequests.PublicUrl(fixture.Settings, fixture.Auth, fixture.Registry));
        Assert.Contains("Request account", html);
        Assert.DoesNotContain("private-path", html);
        var fixtureRoot = Environment.GetEnvironmentVariable("MIC_DISCORD_SIGNUP_FIXTURES");
        if (!string.IsNullOrEmpty(fixtureRoot))
        {
            Directory.CreateDirectory(fixtureRoot);
            File.WriteAllText(Path.Combine(fixtureRoot, "request.html"), UiDiscordAccountEndpoints.RequestPage("https://share.test/shared/original/signup", ""));
            File.WriteAllText(Path.Combine(fixtureRoot, "claim.html"), UiDiscordAccountEndpoints.ClaimPage());
            File.WriteAllText(Path.Combine(fixtureRoot, "public.html"), html);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Directory.CreateTempSubdirectory("mic-discord-signup-").FullName;
        public readonly Settings Settings;
        public readonly UiAuth Auth;
        public readonly UiEnvironmentRegistry Registry;
        public readonly DiscordHandler Discord = new();
        public readonly HttpClient Http;
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public string StorePath => Path.Combine(Root, "UiDiscordAccountRequests", "requests.json");
        public Fixture()
        {
            Settings = new() { ImageDownloadBaseFolder = Root, UiEnvironmentId = "original", UiEnvironmentName = "Original",
                UiEnvironmentController = true, UiAuthFilePath = Path.Combine(Root, "auth.json"), UiLoginLinksFilePath = Path.Combine(Root, "links.json"),
                UiEnvironmentRegistryPath = Path.Combine(Root, "registry.json"), UiPublicBaseUrl = "https://share.test/private-path",
                UiPublicShareBaseUrl = "https://share.test/shared/original", DiscordVibecodersBotToken = new string('t', 60),
                DiscordVibecodersWebhookUrl = "https://discord.com/api/webhooks/123/testtoken" };
            File.WriteAllText(Settings.UiAuthFilePath, JsonSerializer.Serialize(new { version = 2, enabled = true, secret = new string('s', 40),
                accounts = new[] { new { username = UiLoginLinks.OwnerLogin, passwordHash = "pbkdf2-sha256$600000$"
                    + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]) } } }));
            File.WriteAllText(Settings.UiLoginLinksFilePath, """{"version":1,"accounts":[]}""");
            File.WriteAllText(Settings.UiEnvironmentRegistryPath, """
                {"version":1,"environments":[{"id":"original","slug":"private-path","name":"Original","original":true},
                {"id":"vibecoders-ai-generation","slug":"vibecoders-ai-generation","name":"Vibecoders","discordAccountRequests":true}]}
                """);
            Auth = UiAuth.CreateFromSettings(Settings)!;
            Registry = new(Settings.UiEnvironmentRegistryPath);
            Http = new(Discord);
        }
        public UiDiscordAccountRequests Service() => new(Settings, Auth, Registry, () => new(Settings, Http), () => Now);
        public void Dispose() { Http.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class DiscordHandler : HttpMessageHandler
    {
        public string Failure = "", Username = "alice.discord", Content = "", Token = "";
        public int Searches, DmOpens, Messages;
        public bool BlockMemberLookup;
        public readonly TaskCompletionSource LookupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ContinueLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private object Member(string? username = null) => new { user = new { id = "333",
            username = username ?? Username, bot = Failure == "bot" }, pending = Failure == "pending", roles = new[] { "444" } };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
            var route = request.RequestUri!.AbsolutePath;
            if (route.Contains("/webhooks/")) return Json(new { guild_id = "111", channel_id = Failure == "destination" ? "999" : "222" });
            Assert.Equal("Bot", request.Headers.Authorization!.Scheme);
            if (route.EndsWith("/members/search"))
            {
                Searches++;
                if (Failure == "nickname") return Json(Array.Empty<object>());
                if (Failure == "prefix") return Json(new[] { Member(Username + ".extra") });
                if (Failure == "duplicate") return Json(new[] { Member(), Member() });
                return Json(new[] { Member() });
            }
            if (route.Contains("/members/"))
            {
                if (BlockMemberLookup) { LookupEntered.TrySetResult(); await ContinueLookup.Task.WaitAsync(ct); }
                return Failure == "wrong-member"
                    ? Json(new { user = new { id = "998", username = Username }, roles = new[] { "444" } }) : Json(Member());
            }
            if (route.EndsWith("/guilds/111")) return Json(new { id = "111", owner_id = "777", roles = new[] {
                new { id = "111", permissions = "0" }, new { id = "444", permissions = "1024" } } });
            if (route.EndsWith("/channels/222")) return Json(new { id = "222", guild_id = "111", type = 0,
                permission_overwrites = new[] { new { id = "333", type = 1, allow = "0", deny = Failure == "no-access" ? "1024" : "0" } } });
            if (route.EndsWith("/users/@me/channels"))
            {
                DmOpens++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("333", body.RootElement.GetProperty("recipient_id").GetString());
                return Json(new { id = "555", type = 1, recipients = new[] { new { id = Failure == "wrong-recipient" ? "888" : "333" } } });
            }
            if (route.EndsWith("/channels/555/messages"))
            {
                Messages++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Content = body.RootElement.GetProperty("content").GetString()!;
                Token = Regex.Match(Content, @"claim#([a-f0-9]{32}\.[A-Za-z0-9_-]{43})").Groups[1].Value;
                Assert.NotEmpty(Token);
                Assert.Equal(4, body.RootElement.GetProperty("flags").GetInt32());
                Assert.Equal(0, body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
                if (Failure == "lost-response") throw new HttpRequestException("Response lost after accepting the DM.");
                if (Failure == "dm-blocked") return new(HttpStatusCode.Forbidden);
                return Json(new { id = "666", channel_id = "555", content = Content });
            }
            throw new InvalidOperationException("Unexpected test route: " + route);
        }
    }
}
