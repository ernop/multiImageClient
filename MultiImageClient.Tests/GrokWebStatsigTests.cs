using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace MultiImageClient.Tests;

public class GrokWebStatsigDerivationTests
{
    // Each synthetic key keeps only the bytes the deployed signer reads from
    // one recorded grok.com page key: byte 5 (frame set), byte 11 (row), and
    // bytes 11, 45, 1 (frame time). Expected values are the animation keys
    // Chromium computed for those page keys on 2026-10-07.
    [Theory]
    [InlineData("AA4AAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAAA", "5040ed100100")]
    [InlineData("AA4AAAADAAAAAAANAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "a0378c100100")]
    [InlineData("AAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAA", "cfad5b100100")]
    [InlineData("AAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABwAA", "70bf8100100")]
    [InlineData("AAAAAAAAAAAAAAAFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgAA", "ca2098100100")]
    [InlineData("AAAAAAAAAAAAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADQAA", "e7614b100100")]
    [InlineData("AAAAAAABAAAAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADwAA", "461f14100100")]
    [InlineData("AAAAAAABAAAAAAAOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAAA", "30d920100100")]
    [InlineData("AAQAAAADAAAAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "451187100100")]
    [InlineData("AAUAAAABAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "c523eb100100")]
    [InlineData("AAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA", "c8576100100")]
    [InlineData("AAwAAAADAAAAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "bd06e100100")]
    [InlineData("AAIAAAACAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACwAA", "e0d94910051eb851eb851ec0051eb851eb851ec100")]
    [InlineData("AAEAAAAAAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAA", "70b08610028f5c28f5c28f60028f5c28f5c28f6100")]
    [InlineData("AA4AAAACAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA", "ded74a10170a3d70a3d70a0170a3d70a3d70a100")]
    [InlineData("AAUAAAACAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACwAA", "2957700f0a3d70a3d70a0599999999999980599999999999980f0a3d70a3d70a00")]
    [InlineData("AA4AAAABAAAAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAA", "f1d3de10170a3d70a3d70a0170a3d70a3d70a100")]
    [InlineData("AAUAAAADAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADgAA", "bd1f30fd70a3d70a3d702b851eb851eb8602b851eb851eb860fd70a3d70a3d700")]
    [InlineData("AAMAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAAA", "903f70fd70a3d70a3d702b851eb851eb8602b851eb851eb860fd70a3d70a3d700")]
    [InlineData("AAYAAAABAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADQAA", "6af9b10147ae147ae147b0147ae147ae147b100")]
    [InlineData("AAsAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADwAA", "d3ae580fd70a3d70a3d7023d70a3d70a3d8023d70a3d70a3d80fd70a3d70a3d700")]
    [InlineData("AAUAAAACAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA", "68d9e100f5c28f5c28f5c00f5c28f5c28f5c100")]
    [InlineData("AA0AAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAA", "5131f0051eb851eb851ec0f3333333333330f333333333333051eb851eb851ec00")]
    [InlineData("AAwAAAACAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACwAA", "1550750b0a3d70a3d70a0b851eb851eb850b851eb851eb850b0a3d70a3d70a00")]
    [InlineData("AAUAAAACAAAAAAAOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAA", "1e57c10028f5c28f5c28f60028f5c28f5c28f6100")]
    [InlineData("AAYAAAACAAAAAAAKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA", "5c6df30d47ae147ae147808f5c28f5c28f608f5c28f5c28f60d47ae147ae147800")]
    [InlineData("AA0AAAADAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwAA", "ac73650ccccccccccccd0999999999999980999999999999980ccccccccccccd00")]
    [InlineData("AAUAAAAAAAAAAAAHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACQAA", "a85c910f851eb851eb8503d70a3d70a3d703d70a3d70a3d70f851eb851eb8500")]
    [InlineData("AAoAAAACAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAA", "67dee10170a3d70a3d70a0170a3d70a3d70a100")]
    [InlineData("AAwAAAACAAAAAAAGAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA", "c7f1b30f5c28f5c28f5c0428f5c28f5c290428f5c28f5c290f5c28f5c28f5c00")]
    [InlineData("AAkAAAADAAAAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADgAA", "fce6b10170a3d70a3d70a0170a3d70a3d70a100")]
    [InlineData("AAcAAAABAAAAAAAHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAAA", "8804e0f0a3d70a3d70a0547ae147ae147c0547ae147ae147c0f0a3d70a3d70a00")]
    [InlineData("AAkAAAADAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACQAA", "a7775e047ae147ae147b0f5c28f5c28f5c0f5c28f5c28f5c047ae147ae147b00")]
    [InlineData("AAYAAAAAAAAAAAAOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACQAA", "83429c0fae147ae147ae02b851eb851eb8602b851eb851eb860fae147ae147ae00")]
    [InlineData("AAsAAAACAAAAAAAJAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACwAA", "6c2b690f5c28f5c28f5c04a3d70a3d70a3c04a3d70a3d70a3c0f5c28f5c28f5c00")]
    [InlineData("AAgAAAABAAAAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADQAA", "467b1e028f5c28f5c28f60fd70a3d70a3d70fd70a3d70a3d7028f5c28f5c28f600")]
    [InlineData("AA4AAAABAAAAAAALAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACgAA", "d4af900733333333333340e3d70a3d70a3d80e3d70a3d70a3d807333333333333400")]
    [InlineData("AA8AAAACAAAAAAAKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAAA", "5e6ff90b5c28f5c28f5c0b3333333333330b3333333333330b5c28f5c28f5c00")]
    public void DerivationMatchesChromium(string syntheticKey, string chromiumAnimationKey)
    {
        var key = Convert.FromBase64String(syntheticKey);
        var sets = GrokWebStatsigFixtures.FrameSets();

        var derived = GrokWebStatsigSigner.DeriveAnimationKey(
            key,
            sets[key[5] % sets.Count],
            rowIndexByteIndex: 11,
            frameTimeByteIndices: new[] { 11, 45, 1 });

        Assert.Equal(chromiumAnimationKey, derived);
    }

    // Frame set 0, row 12 of the second 2026-10-07 deployment. Its easing curve
    // dips below 0, so early frame times push red and green past 255, and
    // Chromium clamps them. The last case stays inside 0-255.
    [Theory]
    [InlineData(3, 2, 10, "feff610f33333333333304f5c28f5c28f5c04f5c28f5c28f5c0f33333333333300")]
    [InlineData(6, 4, 10, "ffff4d0570a3d70a3d70c0f0a3d70a3d70a0f0a3d70a3d70a0570a3d70a3d70c00")]
    [InlineData(13, 11, 13, "e1ef760d1eb851eb851e80947ae147ae14780947ae147ae14780d1eb851eb851e800")]
    public void OvershootingColorChannelsClampLikeChromium(int a, int b, int c, string chromiumAnimationKey)
    {
        IReadOnlyList<int> frame = new[] { 244, 255, 104, 72, 111, 228, 219, 41, 49, 18, 70 };
        var key = new byte[48];
        key[1] = (byte)a;
        key[2] = (byte)b;
        key[3] = (byte)c;

        var derived = GrokWebStatsigSigner.DeriveAnimationKey(
            key,
            Enumerable.Repeat(frame, 16).ToArray(),
            rowIndexByteIndex: 0,
            frameTimeByteIndices: new[] { 1, 2, 3 });

        Assert.Equal(chromiumAnimationKey, derived);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(2, 2, 6, 20)]
    [InlineData(5, 5, 1, 30)]
    [InlineData(11, 15, 1, 170)]
    [InlineData(15, 15, 15, 3380)]
    [InlineData(16, 15, 15, 0)]
    public void FrameTimeRoundsHalvesUpToTens(int a, int b, int c, long expected)
    {
        var key = new byte[48];
        key[1] = (byte)a;
        key[2] = (byte)b;
        key[3] = (byte)c;

        Assert.Equal(expected, GrokWebStatsigSigner.FrameTime(key, new[] { 1, 2, 3 }));
    }

    [Fact]
    public void OfflineSelfTestPasses()
    {
        GrokWebStatsigSigner.RunSelfTest();
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(150, "2 min")]
    [InlineData(5400, "90 min")]
    [InlineData(9000, "2.5 h")]
    [InlineData(612000, "7.1 d")]
    public void AgesAreShortAndReadable(int seconds, string expected)
    {
        Assert.Equal(expected, GrokWebStatsigSigner.FormatAge(TimeSpan.FromSeconds(seconds)));
    }
}

public class GrokWebStatsigParserTests
{
    [Fact]
    public void PageParserReadsDashVariantMetaFramesAndGrokScripts()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 48).Select(i => (byte)i).ToArray());

        var page = GrokWebStatsigParser.ParsePage(
            GrokWebStatsigFixtures.PageHtml(key),
            GrokWebStatsigSource.PageUri);

        Assert.Equal(key, page.VerificationKeyBase64);
        Assert.Equal(4, page.FrameSets.Count);
        Assert.All(page.FrameSets, set => Assert.Equal(16, set.Count));
        Assert.Equal(
            new[] { 88, 102, 226, 47, 41, 68, 213, 129, 16, 191, 240 },
            page.FrameSets[2][10]);
        Assert.Equal(
            new[] { GrokWebStatsigFixtures.ExportChunk, GrokWebStatsigFixtures.LoaderChunk },
            page.ScriptUrls.Select(url => url.AbsoluteUri));
    }

    [Fact]
    public void VerificationKeyMustBeOneMetaTagWith48Bytes()
    {
        var key = Convert.ToBase64String(new byte[48]);

        Assert.Contains("found 0", ParseKeyError("<html></html>"));
        Assert.Contains(
            "found 2",
            ParseKeyError(GrokWebStatsigFixtures.Meta(key) + GrokWebStatsigFixtures.Meta(key)));
        Assert.Contains(
            "32 bytes",
            ParseKeyError(GrokWebStatsigFixtures.Meta(Convert.ToBase64String(new byte[32]))));
        Assert.Contains("not base64", ParseKeyError(GrokWebStatsigFixtures.Meta("%%%")));
        Assert.Equal(
            key,
            GrokWebStatsigParser.ParseVerificationKey(
                "<meta content=\"" + key + "\" name=\"GROK-SITE-VERIFICATION\">"));
    }

    [Fact]
    public void FrameTableCopiesMustAgreeAndMatchTheExpectedShape()
    {
        var json = GrokWebStatsigFixtures.FramesJson;
        var changed = json.Replace("[200,5,118", "[201,5,118", StringComparison.Ordinal);

        Assert.Contains(
            "differ",
            FramesError(GrokWebStatsigFixtures.EscapedFrames(json) + GrokWebStatsigFixtures.EscapedFrames(changed)));
        Assert.Contains("not found", FramesError("<html></html>"));

        const string row = "{\"color\":[1,2,3,4,5,6],\"deg\":7,\"bezier\":[8,9,10,11]}";
        var fifteenRows = "[[" + string.Join(",", Enumerable.Repeat(row, 15)) + "]]";
        Assert.Contains("does not have 16 rows", FramesError(GrokWebStatsigFixtures.EscapedFrames(fifteenRows)));

        var extraField = "[[" + string.Join(",", Enumerable.Repeat(row.Replace("\"deg\":7", "\"deg\":7,\"x\":1"), 16)) + "]]";
        Assert.Contains("unexpected field", FramesError(GrokWebStatsigFixtures.EscapedFrames(extraField)));

        var outOfRange = "[[" + string.Join(",", Enumerable.Repeat(row.Replace("\"deg\":7", "\"deg\":256"), 16)) + "]]";
        Assert.Contains("outside 0-255", FramesError(GrokWebStatsigFixtures.EscapedFrames(outOfRange)));
    }

    [Fact]
    public void SignerExportResolvesItsAsyncModule()
    {
        Assert.Equal(
            4629918,
            GrokWebStatsigParser.FindSignerModuleId(GrokWebStatsigFixtures.ExportChunkText));
        Assert.Null(GrokWebStatsigParser.FindSignerModuleId("e.s([\"reportBotoxSignError\",0,b],1393856);"));

        var twoImports = GrokWebStatsigFixtures.ExportChunkText.Replace(
            "(await e.A(4629918)).default()",
            "(await e.A(1)).default(),(await e.A(2)).default()",
            StringComparison.Ordinal);
        Assert.Throws<GrokWebException>(() => GrokWebStatsigParser.FindSignerModuleId(twoImports));
    }

    [Fact]
    public void AsyncLoadersMapModulesToChunkPaths()
    {
        var loaders = GrokWebStatsigParser.ParseAsyncChunkLoaders(GrokWebStatsigFixtures.LoaderChunkText);

        Assert.Equal(new[] { "static/chunks/signer.js" }, loaders[4629918]);
        Assert.Equal(new[] { "static/chunks/a.js", "static/chunks/b.css" }, loaders[9746840]);

        var conflicting = GrokWebStatsigParser.ParseAsyncChunkLoaders(
            "[" + GrokWebStatsigFixtures.LoaderEntry(7, "static/chunks/x.js")
            + "," + GrokWebStatsigFixtures.LoaderEntry(7, "static/chunks/y.js") + "]");
        Assert.Null(conflicting[7]);
    }

    [Fact]
    public void SignerParametersComeFromTheSignerChunk()
    {
        var parameters = GrokWebStatsigParser.TryParseSignerParameters(
            GrokWebStatsigFixtures.SignerChunkText(4));

        Assert.NotNull(parameters);
        Assert.Equal(11, parameters!.RowIndexByteIndex);
        Assert.Equal(new[] { 11, 45, 1 }, parameters.FrameTimeByteIndices);
        Assert.Equal(5, parameters.FrameSetByteIndex);
        Assert.Equal(4, parameters.FrameSetCount);
        Assert.Null(GrokWebStatsigParser.TryParseSignerParameters("let x=1;"));

        var threeReads = GrokWebStatsigFixtures.SignerChunkText(4)
            .Replace("m.f(n[1],16)", "m.f(n[1])", StringComparison.Ordinal);
        Assert.Contains(
            "3 key-byte",
            Assert.Throws<GrokWebException>(() => GrokWebStatsigParser.TryParseSignerParameters(threeReads)).Message);

        var noEpoch = GrokWebStatsigFixtures.SignerChunkText(4)
            .Replace("0x644f6370", "0x1", StringComparison.Ordinal);
        Assert.Contains(
            "counter epoch",
            Assert.Throws<GrokWebException>(() => GrokWebStatsigParser.TryParseSignerParameters(noEpoch)).Message);
    }

    private static string ParseKeyError(string html)
    {
        return Assert.Throws<GrokWebException>(() => GrokWebStatsigParser.ParseVerificationKey(html)).Message;
    }

    private static string FramesError(string html)
    {
        return Assert.Throws<GrokWebException>(() => GrokWebStatsigParser.ParseFrameSets(html)).Message;
    }
}

public class GrokWebStatsigSourceTests
{
    private const string Cookies = "sso=a; sso-rw=b";
    private const string SignedPath = "/rest/app-chat/conversations/new";
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Page = GrokWebStatsigSource.PageUri.AbsoluteUri;

    [Fact]
    public async Task PageKeyIsFetchedOnceAndReusedWithinMaxAge()
    {
        var site = new FakeGrokSite();
        var now = Start;
        using var source = GrokWebStatsigSource.ForPage(site, () => now);

        var first = await source.GetSignerAsync(Cookies);
        now += TimeSpan.FromMinutes(59);
        var second = await source.GetSignerAsync(Cookies);

        Assert.Same(first, second);
        Assert.Equal(GrokWebStatsigOrigin.Page, first.Origin);
        Assert.Equal(Start, first.ObtainedAtUtc);
        Assert.Equal(1, site.Count(Page));
        Assert.Equal(1, site.Count(GrokWebStatsigFixtures.SignerChunk));
        Assert.Equal(new[] { Cookies }, site.PageCookies);
        Assert.Equal(0, site.ScriptRequestsWithCookies);
    }

    [Fact]
    public async Task PageKeyIsReplacedAfterMaxAgeWithoutRescanningChunks()
    {
        var site = new FakeGrokSite();
        var now = Start;
        using var source = GrokWebStatsigSource.ForPage(site, () => now);

        var first = await source.GetSignerAsync(Cookies);
        now += GrokWebStatsigSource.PageKeyMaxAge + TimeSpan.FromSeconds(1);
        var second = await source.GetSignerAsync(Cookies);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.KeyFingerprint, second.KeyFingerprint);
        Assert.Equal(2, site.Count(Page));
        Assert.Equal(1, site.Count(GrokWebStatsigFixtures.ExportChunk));
        Assert.Equal(1, site.Count(GrokWebStatsigFixtures.LoaderChunk));
        Assert.Equal(1, site.Count(GrokWebStatsigFixtures.SignerChunk));
    }

    [Fact]
    public async Task RejectedPageKeyIsReplacedOnTheNextRequest()
    {
        var site = new FakeGrokSite();
        using var source = GrokWebStatsigSource.ForPage(site, () => Start);

        var first = await source.GetSignerAsync(Cookies);
        first.MarkRejected("out of date");
        var second = await source.GetSignerAsync(Cookies);

        Assert.NotSame(first, second);
        Assert.False(second.IsRejected);
        Assert.Equal(2, site.Count(Page));
    }

    [Fact]
    public async Task ChangedCookiesFetchANewKey()
    {
        var site = new FakeGrokSite();
        using var source = GrokWebStatsigSource.ForPage(site, () => Start);

        await source.GetSignerAsync(Cookies);
        await source.GetSignerAsync("sso=c");

        Assert.Equal(new[] { Cookies, "sso=c" }, site.PageCookies);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var site = new FakeGrokSite
        {
            PageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var source = GrokWebStatsigSource.ForPage(site, () => Start);

        var calls = Enumerable.Range(0, 5).Select(_ => source.GetSignerAsync(Cookies)).ToArray();
        await site.PageRequested.Task;
        site.PageGate.SetResult();
        var signers = await Task.WhenAll(calls);

        Assert.All(signers, signer => Assert.Same(signers[0], signer));
        Assert.Equal(1, site.Count(Page));
    }

    [Fact]
    public async Task PageKeySignsLikeTheBrowserPair()
    {
        const string key = "AAYAAAACAAAAAAAKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAA";
        const string chromiumAnimationKey = "5c6df30d47ae147ae147808f5c28f5c28f608f5c28f5c28f60d47ae147ae147800";
        var site = new FakeGrokSite();
        site.Keys.Enqueue(key);
        using var source = GrokWebStatsigSource.ForPage(site, () => Start);

        var signer = await source.GetSignerAsync(Cookies);
        var browser = new GrokWebStatsigSigner(key, chromiumAnimationKey);

        Assert.Equal(
            browser.Generate("POST", SignedPath, 106_000_000, 7),
            signer.Generate("POST", SignedPath, 106_000_000, 7));
    }

    [Fact]
    public async Task RefreshFailureFailsClosedThenCoolsDown()
    {
        var site = new FakeGrokSite { PageStatus = HttpStatusCode.Forbidden };
        var now = Start;
        using var source = GrokWebStatsigSource.ForPage(site, () => now);

        var failure = await Assert.ThrowsAsync<GrokWebException>(() => source.GetSignerAsync(Cookies));
        Assert.Contains("Could not get a grok.com signing key", failure.Message);
        Assert.Contains("HTTP 403", failure.Message);

        var cooled = await Assert.ThrowsAsync<GrokWebException>(() => source.GetSignerAsync(Cookies));
        Assert.Contains("HTTP 403", cooled.Message);
        Assert.Contains("30 s after that failure", cooled.Message);
        Assert.Equal(1, site.Count(Page));

        now += GrokWebStatsigSource.RefreshFailureCooldown + TimeSpan.FromSeconds(1);
        site.PageStatus = HttpStatusCode.OK;
        var signer = await source.GetSignerAsync(Cookies);

        Assert.Equal(GrokWebStatsigOrigin.Page, signer.Origin);
        Assert.Equal(2, site.Count(Page));
    }

    [Fact]
    public async Task SignerAndPageMustAgreeOnFrameSetCount()
    {
        var site = new FakeGrokSite(signerFrameSets: 3);
        using var source = GrokWebStatsigSource.ForPage(site, () => Start);

        var error = await Assert.ThrowsAsync<GrokWebException>(() => source.GetSignerAsync(Cookies));

        Assert.Contains("grok.com page format changed", error.Message);
        Assert.Contains("among 3", error.Message);
    }

    [Fact]
    public async Task PinnedPairNeverFetchesAndFailsFastAfterRejection()
    {
        var site = new FakeGrokSite();
        var settings = new Settings
        {
            GrokWebStatsigVerificationKey = Convert.ToBase64String(new byte[48]),
            GrokWebStatsigAnimationKey = "0a",
            GrokWebStatsigCapturedAtUtc = "2026-10-01T08:00:00Z",
        };
        using var source = GrokWebStatsigSource.FromSettings(settings, site, () => Start);

        Assert.Null(source.Problem);
        Assert.True(source.IsPinned);
        Assert.Contains("captured 2026-10-01 08:00 UTC (6.2 d ago)", source.DescribeMode());

        var signer = await source.GetSignerAsync(Cookies);
        Assert.Equal(GrokWebStatsigOrigin.Pinned, signer.Origin);

        signer.MarkRejected("out of date");
        var error = await Assert.ThrowsAsync<GrokWebException>(() => source.GetSignerAsync(Cookies));
        Assert.Contains("Clear both GrokWebStatsig keys", error.Message);
        Assert.Equal(0, site.TotalRequests);
    }

    [Fact]
    public void BlankSettingsSelectPageKeys()
    {
        using var source = GrokWebStatsigSource.FromSettings(new Settings());

        Assert.Null(source.Problem);
        Assert.False(source.IsPinned);
        Assert.Contains("fetches keys from https://grok.com/", source.DescribeMode());
    }

    [Theory]
    [InlineData(ZeroKey, "", "", "incomplete")]
    [InlineData("", "0a", "", "incomplete")]
    [InlineData("", "", "2026-10-01T08:00:00Z", "without")]
    [InlineData(ZeroKey, "0a", "yesterday", "ISO-8601")]
    [InlineData("AAAA", "0a", "", "malformed")]
    [InlineData(ZeroKey, "xyz", "", "malformed")]
    public void UnusablePinnedSettingsDisableSigning(
        string verificationKey,
        string animationKey,
        string capturedAt,
        string expected)
    {
        var settings = new Settings
        {
            GrokWebStatsigVerificationKey = verificationKey,
            GrokWebStatsigAnimationKey = animationKey,
            GrokWebStatsigCapturedAtUtc = capturedAt,
        };
        using var source = GrokWebStatsigSource.FromSettings(settings);

        Assert.Contains(expected, source.Problem);
    }

    private const string ZeroKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
}

public class GrokWebAppChatRejectionTests
{
    [Theory]
    [InlineData(
        403,
        "{\"error\":{\"code\":7,\"message\":\"This page is out of date. Reload to continue.\",\"details\":[]}}",
        GrokWebAppChatRejection.StaleSigningKey)]
    [InlineData(
        403,
        "{\"error\":{\"code\":7,\"message\":\"Request rejected by anti-bot rules.\",\"details\":[]}}",
        GrokWebAppChatRejection.AntiBot)]
    [InlineData(403, "{\"error\":{\"code\":7,\"message\":\"Forbidden\"}}", GrokWebAppChatRejection.None)]
    [InlineData(403, "", GrokWebAppChatRejection.None)]
    [InlineData(500, "This page is out of date. Reload to continue.", GrokWebAppChatRejection.None)]
    public void RejectionsAreClassifiedByMessage(
        int status,
        string body,
        GrokWebAppChatRejection expected)
    {
        Assert.Equal(expected, GrokWebClient.ClassifyAppChatRejection(status, body));
    }
}

internal static class GrokWebStatsigFixtures
{
    public const string ExportChunk = "https://cdn.grok.com/_next/static/chunks/export.js";
    public const string LoaderChunk = "https://cdn.grok.com/_next/static/chunks/loader.js";
    public const string SignerChunk = "https://cdn.grok.com/_next/static/chunks/signer.js";

    // The public frame table grok.com served on 2026-10-07, one set per line.
    private static readonly string[] FrameSetJson =
    {
        """[{"color":[200,5,118,247,20,24],"deg":156,"bezier":[148,249,167,159]},{"color":[207,173,91,35,137,200],"deg":241,"bezier":[182,76,170,51]},{"color":[112,11,248,14,132,222],"deg":143,"bezier":[0,245,143,81]},{"color":[132,66,179,0,165,229],"deg":93,"bezier":[243,155,155,35]},{"color":[146,57,3,85,246,112],"deg":200,"bezier":[240,245,108,47]},{"color":[202,32,152,157,179,245],"deg":216,"bezier":[128,92,129,139]},{"color":[114,241,243,174,66,141],"deg":105,"bezier":[249,232,177,33]},{"color":[175,90,146,23,136,127],"deg":197,"bezier":[126,162,165,249]},{"color":[112,176,134,112,207,225],"deg":23,"bezier":[159,171,235,76]},{"color":[36,6,51,219,13,47],"deg":158,"bezier":[43,161,26,244]},{"color":[186,246,178,234,199,51],"deg":44,"bezier":[101,40,219,192]},{"color":[95,51,233,59,212,148],"deg":241,"bezier":[146,151,227,65]},{"color":[231,97,75,29,3,188],"deg":221,"bezier":[21,72,148,69]},{"color":[90,210,225,124,11,45],"deg":127,"bezier":[246,120,43,83]},{"color":[126,65,153,90,54,132],"deg":16,"bezier":[225,37,159,219]},{"color":[41,29,167,97,7,159],"deg":196,"bezier":[202,129,242,176]}]""",
        """[{"color":[80,64,237,84,188,30],"deg":197,"bezier":[147,171,14,43]},{"color":[197,35,235,37,248,240],"deg":162,"bezier":[183,229,68,255]},{"color":[6,176,154,44,74,217],"deg":234,"bezier":[189,157,9,222]},{"color":[240,210,219,202,159,13],"deg":217,"bezier":[224,69,249,216]},{"color":[32,74,227,245,34,214],"deg":196,"bezier":[16,78,212,25]},{"color":[188,234,68,240,54,181],"deg":248,"bezier":[185,253,106,98]},{"color":[136,40,204,74,73,148],"deg":111,"bezier":[211,13,251,184]},{"color":[129,27,75,87,252,54],"deg":53,"bezier":[118,64,68,71]},{"color":[202,191,254,173,189,200],"deg":141,"bezier":[86,182,230,249]},{"color":[42,248,88,157,12,106],"deg":241,"bezier":[127,44,45,89]},{"color":[73,191,225,110,132,163],"deg":233,"bezier":[221,195,122,26]},{"color":[252,199,163,157,143,118],"deg":75,"bezier":[29,11,48,238]},{"color":[70,31,20,70,207,39],"deg":81,"bezier":[7,182,204,249]},{"color":[208,225,145,172,217,165],"deg":116,"bezier":[102,84,31,63]},{"color":[48,217,32,1,230,180],"deg":225,"bezier":[95,174,24,120]},{"color":[221,22,220,240,121,73],"deg":25,"bezier":[97,157,190,213]}]""",
        """[{"color":[184,228,188,82,43,196],"deg":113,"bezier":[95,233,163,249]},{"color":[224,217,73,107,90,136],"deg":254,"bezier":[75,157,133,233]},{"color":[57,93,108,242,158,61],"deg":149,"bezier":[69,12,83,83]},{"color":[121,113,150,126,224,155],"deg":31,"bezier":[37,212,75,228]},{"color":[238,165,73,227,132,250],"deg":43,"bezier":[100,241,154,107]},{"color":[41,54,232,161,76,248],"deg":245,"bezier":[112,54,240,74]},{"color":[193,238,171,102,192,38],"deg":160,"bezier":[149,67,123,219]},{"color":[150,68,72,251,189,254],"deg":56,"bezier":[151,67,231,239]},{"color":[105,209,13,130,11,0],"deg":20,"bezier":[193,50,118,31]},{"color":[91,39,105,160,54,106],"deg":7,"bezier":[84,129,105,254]},{"color":[88,102,226,47,41,68],"deg":213,"bezier":[129,16,191,240]},{"color":[104,121,105,133,253,212],"deg":109,"bezier":[146,250,73,45]},{"color":[86,122,134,168,101,110],"deg":202,"bezier":[239,254,75,111]},{"color":[245,197,220,153,206,70],"deg":65,"bezier":[71,213,55,123]},{"color":[1,229,124,44,133,184],"deg":212,"bezier":[208,126,234,39]},{"color":[191,120,123,151,219,22],"deg":54,"bezier":[29,124,23,62]}]""",
        """[{"color":[68,99,36,49,67,26],"deg":225,"bezier":[164,159,206,26]},{"color":[67,239,22,138,248,241],"deg":179,"bezier":[146,29,50,5]},{"color":[14,206,227,45,174,27],"deg":50,"bezier":[51,45,227,245]},{"color":[11,208,110,105,167,41],"deg":44,"bezier":[91,157,207,107]},{"color":[244,99,5,4,62,77],"deg":57,"bezier":[211,134,101,112]},{"color":[4,91,180,18,11,38],"deg":106,"bezier":[164,92,227,90]},{"color":[171,41,162,70,87,146],"deg":126,"bezier":[106,116,156,80]},{"color":[108,177,179,53,86,101],"deg":195,"bezier":[38,167,175,181]},{"color":[178,110,109,134,147,49],"deg":194,"bezier":[123,234,113,163]},{"color":[100,168,213,154,70,72],"deg":225,"bezier":[19,32,5,80]},{"color":[9,73,243,187,203,168],"deg":33,"bezier":[15,243,153,223]},{"color":[198,73,121,65,117,242],"deg":245,"bezier":[143,27,123,170]},{"color":[69,17,135,210,255,233],"deg":187,"bezier":[194,80,146,187]},{"color":[160,55,140,44,177,116],"deg":56,"bezier":[7,77,251,103]},{"color":[135,221,7,40,33,27],"deg":108,"bezier":[234,30,176,179]},{"color":[215,226,5,239,167,106],"deg":22,"bezier":[61,240,185,52]}]""",
    };

    public static string FramesJson => "[" + string.Join(",", FrameSetJson) + "]";

    public const string ExportChunkText =
        "e.s([\"reportBotoxSignError\",0,b],1393856);"
        + "let A=(i=async()=>(await e.A(4629918)).default(),async function(e,t){n??=i().catch(e=>{throw n=void 0,e});"
        + "let a=await n;return await a(e,t)});e.s([\"botoxSign\",0,A],831076);";

    public static string LoaderChunkText =>
        "(globalThis.TURBOPACK||(globalThis.TURBOPACK=[])).push([\"x\","
        + LoaderEntry(8520926, "static/chunks/other.js") + ","
        + LoaderEntry(4629918, "static/chunks/signer.js") + ","
        + LoaderEntry(9746840, "static/chunks/a.js", "static/chunks/b.css") + "]);";

    public static IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>> FrameSets()
    {
        return GrokWebStatsigParser.ParseFrameSets(EscapedFrames(FramesJson));
    }

    public static string EscapedFrames(string json)
    {
        return "<script>self.__next_f.push([1,\"7:" + json.Replace("\"", "\\\"", StringComparison.Ordinal) + "\\n\"])</script>";
    }

    public static string Meta(string key)
    {
        return "<meta name=\"grok-site\u2015verification\" content=\"" + key + "\"/>";
    }

    public static string PageHtml(string key)
    {
        return "<!DOCTYPE html><html><head>" + Meta(key)
            + "<script src=\"https://static.cloudflareinsights.com/beacon.min.js\" defer></script>"
            + "<script src=\"" + ExportChunk + "\" async=\"\"></script>"
            + "<script src=\"" + LoaderChunk + "\" async=\"\"></script>"
            + EscapedFrames(FramesJson) + EscapedFrames(FramesJson)
            + "</head><body></body></html>";
    }

    public static string LoaderEntry(long moduleId, params string[] paths)
    {
        var list = string.Join(",", paths.Select(path => "\"" + path + "\""));
        return moduleId + ",s=>{s.v(t=>Promise.all([" + list + "].map(t=>s.l(t))).then(()=>t(1645e3)))}";
    }

    public static string SignerChunkText(int frameSets)
    {
        return "let[f,x]=[m.a(n[11],16),m.b(m.c(m.d(n[11],16),m.e(n[45],16)),m.f(n[1],16))];"
            + "y=(W,t)=>H(l(W))[t[5]%" + frameSets + "];[T,X,A]=[3,0x644f6370,0];";
    }
}

internal sealed class FakeGrokSite : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _requests = new();
    private readonly ConcurrentQueue<string?> _pageCookies = new();
    private readonly int _signerFrameSets;
    private int _generatedKeys;
    private int _scriptRequestsWithCookies;

    public FakeGrokSite(int signerFrameSets = 4)
    {
        _signerFrameSets = signerFrameSets;
    }

    public ConcurrentQueue<string> Keys { get; } = new();
    public HttpStatusCode PageStatus { get; set; } = HttpStatusCode.OK;
    public TaskCompletionSource? PageGate { get; init; }
    public TaskCompletionSource PageRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<string?> PageCookies => _pageCookies.ToArray();
    public int ScriptRequestsWithCookies => Volatile.Read(ref _scriptRequestsWithCookies);
    public int TotalRequests => _requests.Values.Sum();

    public int Count(string url)
    {
        return _requests.TryGetValue(url, out var count) ? count : 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        _requests.AddOrUpdate(url, 1, (_, count) => count + 1);
        var cookie = request.Headers.TryGetValues("Cookie", out var values)
            ? string.Join("; ", values)
            : null;

        if (url == GrokWebStatsigSource.PageUri.AbsoluteUri)
        {
            PageRequested.TrySetResult();
            if (PageGate != null)
            {
                await PageGate.Task.WaitAsync(cancellationToken);
            }
            _pageCookies.Enqueue(cookie);
            return PageStatus == HttpStatusCode.OK
                ? Text(GrokWebStatsigFixtures.PageHtml(NextKey()), "text/html")
                : new HttpResponseMessage(PageStatus) { Content = new StringContent("blocked") };
        }

        if (cookie != null)
        {
            Interlocked.Increment(ref _scriptRequestsWithCookies);
        }
        return url switch
        {
            GrokWebStatsigFixtures.ExportChunk => Text(GrokWebStatsigFixtures.ExportChunkText, "application/javascript"),
            GrokWebStatsigFixtures.LoaderChunk => Text(GrokWebStatsigFixtures.LoaderChunkText, "application/javascript"),
            GrokWebStatsigFixtures.SignerChunk => Text(GrokWebStatsigFixtures.SignerChunkText(_signerFrameSets), "application/javascript"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private string NextKey()
    {
        if (Keys.TryDequeue(out var queued))
        {
            return queued;
        }
        var number = Interlocked.Increment(ref _generatedKeys);
        var key = new byte[48];
        key[0] = (byte)number;
        key[47] = (byte)(number * 7);
        return Convert.ToBase64String(key);
    }

    private static HttpResponseMessage Text(string body, string mediaType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
    }
}
