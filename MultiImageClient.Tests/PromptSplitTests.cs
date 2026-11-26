using System.Text.Json;

namespace MultiImageClient;

// Opus 5.5 splits one prompt into several images; job intake makes one job
// per part from the user's stored plan. See docs/prompt-split-prd.md.
public sealed class PromptSplitTests
{
    private const string Alice = "login:alice";
    private const string Source = "A comic with eight panels about a fox who opens a bakery.";
    private const string FirstPart = "Bright comic page, four panels in a 2 by 2 grid: the fox finds an empty shop, signs a lease, paints the sign, and bakes bread.";
    private const string SecondPart = "Bright comic page, four panels in a 2 by 2 grid: opening day, a long queue, a sold-out shelf, and the fox asleep on flour sacks.";
    private static readonly string TwoPartReply = Reply(2, "Eight panels read better as two pages.", ("Panels 1 to 4", FirstPart), ("Panels 5 to 8", SecondPart));
    private static readonly IReadOnlyDictionary<string, string> NoExtraText =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void ReplyContractIsStrict()
    {
        var plan = PromptSplit.ParseReply("```json\n" + TwoPartReply + "\n```", 1_000, "Opus 5.5");
        Assert.Equal(2, plan.ImageCount);
        Assert.Equal("Eight panels read better as two pages.", plan.Reason);
        Assert.Equal("portrait", plan.SuggestedShape);
        Assert.Equal(new[] { new PromptSplitPart("Panels 1 to 4", FirstPart), new PromptSplitPart("Panels 5 to 8", SecondPart) }, plan.Parts);

        var single = PromptSplit.ParseReply("""{"imageCount":1,"reason":"One scene fits one image.","images":[]}""", 1_000, "Opus 5.5");
        Assert.Equal(1, single.ImageCount);
        Assert.Null(single.SuggestedShape);
        Assert.Empty(single.Parts);

        var singleWithShape = PromptSplit.ParseReply("""{"imageCount":1,"reason":"One scene fits one image.","shape":"auto","images":[]}""", 1_000, "Opus 5.5");
        Assert.Equal(1, singleWithShape.ImageCount);
        Assert.Equal("auto", singleWithShape.SuggestedShape);

        var caseFoldedShape = PromptSplit.ParseReply(Reply(2, "r", "Wide", ("a", "x"), ("b", "y")), 1_000, "Opus 5.5");
        Assert.Equal("wide", caseFoldedShape.SuggestedShape);

        var crlf = PromptSplit.ParseReply(Reply(2, "r", ("a", "line one\r\nline two "), ("b", "x")), 1_000, "Opus 5.5");
        Assert.Equal("line one\nline two", crlf.Parts[0].Prompt);

        foreach (var reply in new[]
        {
            "",
            "Here is my plan: two images.",
            "[]",
            """{"imageCount":-1,"reason":"r","images":[]}""",
            """{"imageCount":0,"reason":"r","images":[{"title":"a","prompt":"x"}]}""",
            """{"imageCount":0,"reason":"","images":[]}""",
            """{"imageCount":9,"reason":"r","images":[]}""",
            """{"imageCount":2.5,"reason":"r","images":[]}""",
            """{"imageCount":"2","reason":"r","images":[]}""",
            """{"imageCount":1,"reason":"","images":[]}""",
            """{"imageCount":1,"images":[]}""",
            """{"imageCount":1,"reason":"r","images":[],"note":"x"}""",
            """{"imageCount":1,"reason":"r","reason":"again","images":[]}""",
            """{"imageCount":1,"reason":"r","images":{}}""",
            // Live multi-image replies must include a valid canonical shape.
            """{"imageCount":2,"reason":"r","images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":2,"reason":"r","shape":"16:9","images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":2,"reason":"r","shape":"vertical","images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":2,"reason":"r","shape":"","images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":2,"reason":"r","shape":null,"images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":2,"reason":"r","shape":3,"images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y"}]}""",
            """{"imageCount":1,"reason":"r","shape":"bogus","images":[]}""",
            Reply(1, "r", ("a", "x")),
            Reply(2, "r"),
            Reply(3, "r", ("a", "x"), ("b", "y")),
            Reply(2, "r", ("a", "x"), ("two\nlines", "y")),
            Reply(2, "r", ("a", "x"), (new string('t', 81), "y")),
            Reply(2, "r", ("a", "x"), ("b", "  ")),
            Reply(2, new string('r', 1_001), ("a", "x"), ("b", "y")),
            """{"imageCount":2,"reason":"r","shape":"portrait","images":[{"title":"a","prompt":"x"},{"title":"b","prompt":"y","seed":1}]}""",
            """{"imageCount":2,"reason":"r","shape":"portrait","images":[{"title":"a","prompt":"x"},"y"]}""",
        })
        {
            Assert.Throws<InvalidDataException>(() => PromptSplit.ParseReply(reply, 1_000, "Opus 5.5"));
        }
    }

    [Fact]
    public async Task DeclineShowsTheModelsReasonWithoutAProviderHint()
    {
        const string decline = """{"imageCount":0,"reason":"I will not write this. Check your API key and billing.","images":[]}""";
        const string message = "Opus 5.5 declined to split this prompt, so nothing was sent. "
            + "Its reason: I will not write this. Check your API key and billing.";
        Assert.Equal(message, Assert.Throws<PromptSplitDeclinedException>(() => PromptSplit.ParseReply(decline, 1_000, "Opus 5.5")).Message);

        var declined = await PromptSplit.RequestAsync(new FakeWriter(decline), "m", 1_000, "Opus 5.5", CancellationToken.None);
        Assert.Null(declined.Plan);
        Assert.True(declined.Declined);
        Assert.Equal(decline, declined.RawResponse);
        Assert.Equal(message, declined.Error);
        Assert.Null(PromptSplit.ActionHint(declined));
        Assert.NotNull(ProviderActionHints.For(UiJobRunner.KeyDescribeClaude, message));
        Assert.NotNull(PromptSplit.ActionHint(new PromptSplitOutcome { Error = "Opus 5.5 request failed: credit balance is too low" }));

        var stored = Assert.Throws<InvalidDataException>(() => PromptSplit.ParseStored("""{"imageCount":0,"reason":"r","images":[]}"""));
        Assert.Equal("The stored split must give imageCount as a whole number from 1 to 8.", stored.Message);
        Assert.Contains("If you will not write image prompts for this source, return imageCount 0", PromptSplit.SystemPrompt);
    }

    [Fact]
    public void PartsOverTheBudgetFailTheWholeSplit()
    {
        var error = Assert.Throws<InvalidDataException>(() => PromptSplit.ParseReply(TwoPartReply, SecondPart.Length - 1, "Opus 5.5"));

        Assert.Equal(
            $"Opus 5.5 wrote {SecondPart.Length:N0} characters for image 2; at most {SecondPart.Length - 1:N0} fit. Nothing was sent. Try again.",
            error.Message);
    }

    [Fact]
    public void PartBudgetIsTheSmallestTargetLimitAfterExtraText()
    {
        Assert.Equal(1_000 - 2 - 8, PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyRecraftV3, UiJobRunner.KeyGpt2 }, Extra(UiJobRunner.KeyRecraftV3, " daylight "), imageAttached: false));
        Assert.Equal(PromptSplit.MaxPartChars, PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyBfl }, NoExtraText, imageAttached: false));
        Assert.Equal(PromptSplit.MaxPartChars - 2 - 5, PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyBfl }, Extra(UiJobRunner.KeyBfl, "sunny"), imageAttached: false));
        Assert.Equal(16_000, PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyGrokApi }, NoExtraText, imageAttached: false));
        Assert.Equal(PromptSplit.MaxPartChars, PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyGrokApi }, NoExtraText, imageAttached: true));
    }

    [Fact]
    public void PartBudgetRejectsDescribeTargetsRepeatsAndCrowdedLimits()
    {
        Assert.Throws<InvalidDataException>(() => PromptSplit.PartBudget(Array.Empty<string>(), NoExtraText, false));
        Assert.Throws<InvalidDataException>(() => PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyGpt2, UiJobRunner.KeyGpt2 }, NoExtraText, false));
        Assert.Throws<InvalidDataException>(() => PromptSplit.PartBudget(new[] { "no-such-generator" }, NoExtraText, false));
        var describe = Assert.Throws<InvalidDataException>(() => PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyGpt2, UiJobRunner.KeyDescribeClaude }, NoExtraText, false));
        Assert.EndsWith("reads images and makes none. A split runs image generators only.", describe.Message);

        var crowded = Assert.Throws<InvalidDataException>(() => PromptSplit.PartBudget(
            new[] { UiJobRunner.KeyRecraftV3 }, Extra(UiJobRunner.KeyRecraftV3, new string('b', 950)), false));
        Assert.Equal(
            $"Endpoint extra text leaves {48:N0} characters for each part. A part needs at least {100:N0}. Shorten the extra text.",
            crowded.Message);
    }

    [Fact]
    public void UserMessageCarriesInstructionStyleNoteLimitsAndSource()
    {
        var message = PromptSplit.BuildUserMessage(PromptSplit.DefaultInstruction, "Ink and watercolor.", 990, Source);

        Assert.StartsWith(PromptSplit.DefaultInstruction + "\n\nStyle note for every image:\nInk and watercolor.\n\n", message);
        Assert.Contains($"Limits: at most {PromptSplit.MaxImages} images. Each image prompt may have at most {990:N0} characters", message);
        Assert.Contains("Output frame: auto (no fixed frame chosen yet).", message);
        Assert.Contains("JSON", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("imageCount", message);
        Assert.Contains("shape", message);
        Assert.Contains("images", message);
        Assert.EndsWith($"<source_prompt>\n{Source}\n</source_prompt>", message);
        Assert.DoesNotContain("Style note", PromptSplit.BuildUserMessage("Split it.", "", 990, Source));

        var fixedFrame = PromptSplit.BuildUserMessage("Split it.", "", 990, Source, requestedShape: "wide");
        Assert.Contains("the user selected \"wide\" (wide (16:9))", fixedFrame);
        Assert.Contains("return \"shape\": \"wide\"", fixedFrame);

        var attachedAuto = PromptSplit.BuildUserMessage("Split it.", "", 990, Source, requestedShape: "auto", imageAttached: true);
        Assert.Contains("match input image", attachedAuto);
        Assert.Contains("Return \"shape\": \"auto\"", attachedAuto);

        Assert.Throws<InvalidDataException>(() =>
            PromptSplit.BuildUserMessage("Split it.", "", 990, Source, requestedShape: "bogus"));

        Assert.Equal("Split it.", PromptSplit.RecordedInstruction("Split it.", ""));
        Assert.Equal("Split it.\n\nStyle note: Ink.", PromptSplit.RecordedInstruction("Split it.", "Ink."));
        Assert.Contains("\"imageCount\"", PromptSplit.SystemPrompt);
        Assert.Contains("\"shape\"", PromptSplit.SystemPrompt);
        Assert.Contains("Begin every prompt with the same shared preface", PromptSplit.SystemPrompt);
        Assert.Contains("Never make an image dim, murky, or dark unless the source asks for that.", PromptSplit.SystemPrompt);
    }

    [Fact]
    public void StoredPlanRoundTrips()
    {
        var plan = PromptSplit.ParseReply(TwoPartReply, 1_000, "Opus 5.5");
        var stored = PromptSplit.ParseStored(PromptSplit.Serialize(plan));

        Assert.Equal(plan.ImageCount, stored.ImageCount);
        Assert.Equal(plan.Reason, stored.Reason);
        Assert.Equal("portrait", stored.SuggestedShape);
        Assert.Equal(plan.Parts, stored.Parts);

        // Legacy stored splits saved without "shape" remain readable.
        var legacyJson = """{"imageCount":2,"reason":"Legacy plan.","images":[{"title":"a","prompt":"first"},{"title":"b","prompt":"second"}]}""";
        var legacy = PromptSplit.ParseStored(legacyJson);
        Assert.Equal(2, legacy.ImageCount);
        Assert.Null(legacy.SuggestedShape);
        Assert.Equal(2, legacy.Parts.Count);
    }

    [Fact]
    public async Task RequestKeepsTheReplyAndFailsOnProviderStopsAndBadReplies()
    {
        var writer = new FakeWriter(TwoPartReply);
        var ok = await PromptSplit.RequestAsync(writer, "the message", 1_000, "Opus 5.5", CancellationToken.None);
        Assert.Equal(2, ok.Plan?.ImageCount);
        Assert.Equal(TwoPartReply, ok.RawResponse);
        Assert.Equal("", ok.Error);
        Assert.Equal(PromptSplit.SystemPrompt, writer.LastSystemPrompt);
        Assert.Equal("the message", writer.LastUserMessage);

        var refused = await PromptSplit.RequestAsync(new FakeWriter("I cannot split this.", "refusal"), "m", 1_000, "Opus 5.5", CancellationToken.None);
        Assert.Null(refused.Plan);
        Assert.Equal("I cannot split this.", refused.RawResponse);
        Assert.Equal("Opus 5.5 stopped early (refusal). Nothing was sent.", refused.Error);

        var prose = await PromptSplit.RequestAsync(new FakeWriter("Two images would be best."), "m", 1_000, "Opus 5.5", CancellationToken.None);
        Assert.Null(prose.Plan);
        Assert.StartsWith("Opus 5.5's reply is not valid JSON", prose.Error);

        var failing = await PromptSplit.RequestAsync(new FakeWriter(null), "m", 1_000, "Opus 5.5", CancellationToken.None);
        Assert.Null(failing.Plan);
        Assert.Equal("Opus 5.5 request failed: provider unavailable", failing.Error);
    }

    [Fact]
    public void UpToMaxSplitsRunPerAccountAndPerServer()
    {
        Assert.Equal(8, PromptSplit.MaxConcurrentSplitsPerAccount);
        Assert.Equal(16, PromptSplit.MaxConcurrentSplits);
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < PromptSplit.MaxConcurrentSplitsPerAccount; i++)
            {
                var lease = PromptSplit.TryBegin("login:split-a", out _);
                Assert.NotNull(lease);
                leases.Add(lease!);
            }
            Assert.Null(PromptSplit.TryBegin("login:split-a", out var same));
            Assert.StartsWith($"{PromptSplit.MaxConcurrentSplitsPerAccount} splits for your account are already running", same);

            for (var i = 0; i < PromptSplit.MaxConcurrentSplits - PromptSplit.MaxConcurrentSplitsPerAccount; i++)
            {
                var lease = PromptSplit.TryBegin("login:split-b", out _);
                Assert.NotNull(lease);
                leases.Add(lease!);
            }
            Assert.Null(PromptSplit.TryBegin("login:split-c", out var full));
            Assert.StartsWith($"{PromptSplit.MaxConcurrentSplits} splits are already running on this server.", full);

            leases[^1].Dispose();
            leases.RemoveAt(leases.Count - 1);
            using var freed = PromptSplit.TryBegin("login:split-c", out _);
            Assert.NotNull(freed);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
        using var again = PromptSplit.TryBegin("login:split-a", out _);
        Assert.NotNull(again);
    }

    [Fact]
    public void RecordedInstructionRoundTripsInstructionAndStyleNote()
    {
        var plain = PromptSplit.RecordedInstruction("Split this comic.", "");
        Assert.Equal(("Split this comic.", ""), PromptSplit.ParseRecordedInstruction(plain));

        var styled = PromptSplit.RecordedInstruction("Split this comic.", "Ink and watercolor.");
        Assert.Equal(("Split this comic.", "Ink and watercolor."), PromptSplit.ParseRecordedInstruction(styled));
    }

    [Fact]
    public void SubmissionNeedsTheUsersCompletedSplitOfTheExactPrompt()
    {
        WithStore(store =>
        {
            var keys = new[] { UiJobRunner.KeyGpt2 };
            var id = Save(store, Alice, Source, TwoPartReply);

            var (exchange, plan) = Resolve(store, Alice, id, Source, keys, NoExtraText);
            Assert.Equal(id, exchange.Id);
            Assert.Equal(new[] { FirstPart, SecondPart }, plan.Parts.Select(part => part.Prompt));

            string Rejection(string identity, string exchangeId, string prompt, IReadOnlyList<string>? targets = null)
                => Assert.Throws<InvalidDataException>(() => Resolve(store, identity, exchangeId, prompt, targets ?? keys, NoExtraText)).Message;

            Assert.Equal($"Split {id} is not one of your saved prompt versions.", Rejection("login:bob", id, Source));
            Assert.Equal($"Split {id} was made from a different prompt. Split the current prompt again.", Rejection(Alice, id, Source + " "));
            var advice = Save(store, Alice, Source, "A bright comic.", purpose: "");
            Assert.Equal($"Saved prompt version {advice} is not a split.", Rejection(Alice, advice, Source));
            var otherModel = Save(store, Alice, Source, TwoPartReply, model: "claude-sonnet-5-5");
            var (otherExchange, otherPlan) = Resolve(store, Alice, otherModel, Source, keys, NoExtraText);
            Assert.Equal(otherModel, otherExchange.Id);
            Assert.Equal(2, otherPlan.ImageCount);
            var unknownModel = Save(store, Alice, Source, TwoPartReply, model: "not-in-catalog");
            Assert.Equal($"Saved prompt version {unknownModel} is not a split.", Rejection(Alice, unknownModel, Source));
            var failed = Save(store, Alice, Source, "", error: "provider unavailable");
            Assert.Equal($"Split {failed} did not complete.", Rejection(Alice, failed, Source));
            var single = Save(store, Alice, Source, """{"imageCount":1,"reason":"One scene.","images":[]}""");
            Assert.Equal($"Split {single} recommends one image, so there is nothing to split.", Rejection(Alice, single, Source));
            Assert.EndsWith("A split runs image generators only.", Rejection(Alice, id, Source, new[] { UiJobRunner.KeyDescribeClaude }));
        });
    }

    [Fact]
    public void SubmissionRejectsPartsThatNoLongerFit()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, Source, TwoPartReply);

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice, id, Source,
                new[] { UiJobRunner.KeyRecraftV3 }, Extra(UiJobRunner.KeyRecraftV3, new string('x', 900))));

            Assert.Equal(
                $"With its extra text, part 1 has {FirstPart.Length + 2 + 900:N0} characters for "
                + $"{GeneratorPresentation.UiDisplayName(UiJobRunner.KeyRecraftV3)}; at most {1_000:N0} fit. Split again.",
                error.Message);
        });
    }

    [Fact]
    public void JobsKeepTheirPartAcrossRestarts()
    {
        var folder = CreateTempFolder();
        try
        {
            var settings = new Settings { ImageDownloadBaseFolder = folder, EnableGenerationArchive = false };
            var registry = new UiJobRegistry(settings);
            var split = new UiJobSplit("group1", "exchange1", 2, 3, "Panels 5 to 8", "claude-opus-5-5", "Twelve panels fit better on three pages.");
            registry.Add(new UiJob { Id = "part2", Prompt = SecondPart, Split = split });
            registry.Add(new UiJob { Id = "plain", Prompt = Source });

            using (var known = JsonDocument.Parse(registry.ReadEnvelopes(0).Envelopes[0]))
            {
                var metadata = known.RootElement.GetProperty("job").GetProperty("split");
                Assert.Equal("group1", metadata.GetProperty("groupId").GetString());
                Assert.Equal("exchange1", metadata.GetProperty("exchangeId").GetString());
                Assert.Equal(2, metadata.GetProperty("index").GetInt32());
                Assert.Equal(3, metadata.GetProperty("count").GetInt32());
                Assert.Equal("Panels 5 to 8", metadata.GetProperty("title").GetString());
                Assert.Equal("claude-opus-5-5", metadata.GetProperty("model").GetString());
                Assert.Equal("Opus 5.5", metadata.GetProperty("modelLabel").GetString());
                Assert.Equal("Twelve panels fit better on three pages.", metadata.GetProperty("reason").GetString());
            }

            var reloaded = new UiJobRegistry(settings);
            Assert.Equal(split, reloaded.Get("part2")?.Split);
            Assert.Null(reloaded.Get("plain")?.Split);
            Assert.NotNull(reloaded.Get("plain"));

            var path = Path.Combine(folder, "UiHistory", "part2", "job.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Index\": 2", "\"Index\": 4"));
            Assert.Null(new UiJobRegistry(settings).Get("part2"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string Reply(int imageCount, string reason, params (string Title, string Prompt)[] parts)
        => Reply(imageCount, reason, imageCount >= 2 ? "portrait" : "auto", parts);

    private static string Reply(int imageCount, string reason, string shape, params (string Title, string Prompt)[] parts)
        => JsonSerializer.Serialize(new
        {
            imageCount,
            reason,
            shape,
            images = parts.Select(part => new { title = part.Title, prompt = part.Prompt }),
        });

    private static IReadOnlyDictionary<string, string> Extra(string key, string extraText)
        => new Dictionary<string, string>(StringComparer.Ordinal) { [key] = extraText };

    private static string Save(
        UiCommunityStore store,
        string identity,
        string original,
        string result,
        string purpose = PromptSplit.Purpose,
        string model = "claude-opus-5-5",
        string error = "")
    {
        var exchange = store.StartClaudePromptExchange(
            identity, "Alice", model, PromptSplit.DefaultInstruction, original, "system", "wire", 1_800_000_000_000, purpose);
        store.CompleteClaudePromptExchange(exchange.Id, result, result, error, 1_800_000_000_100);
        return exchange.Id;
    }

    private static (UiClaudePromptExchangeRecord Exchange, PromptSplitPlan Plan) Resolve(
        UiCommunityStore store,
        string identity,
        string exchangeId,
        string prompt,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string> extraTexts)
        => PromptSplit.ResolveSubmission(exchangeId, prompt, keys, extraTexts, imageAttached: false,
            id => store.ReadClaudePromptExchanges(identity, 1, exchangeId: id).SingleOrDefault());

    private static void WithStore(Action<UiCommunityStore> body)
    {
        var folder = CreateTempFolder();
        try
        {
            body(new UiCommunityStore(new Settings
            {
                ImageDownloadBaseFolder = folder,
                UiCommunityDbPath = Path.Combine(folder, "community.sqlite3"),
            }));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void SystemPromptAndBuiltinPresetsSupportStoryExpansionAlongsideLosslessDefault()
    {
        Assert.Contains("or when the user's instruction asks you to develop or expand the source across a target number of pages", PromptSplit.SystemPrompt);
        Assert.Contains("Unless the user's instruction asks for a specific page count or story expansion, use the fewest images", PromptSplit.SystemPrompt);
        Assert.Contains("When the user's instruction does ask you to develop a story", PromptSplit.SystemPrompt);
        Assert.Contains("while preserving every detail from the source.", PromptSplit.SystemPrompt);

        Assert.Equal(2, PromptSplit.BuiltinPresets.Count);
        Assert.Equal("default", PromptSplit.BuiltinPresets[0].Id);
        Assert.Equal(PromptSplit.DefaultInstruction, PromptSplit.BuiltinPresets[0].Text);
        Assert.Equal("story-8-pages", PromptSplit.BuiltinPresets[1].Id);
        Assert.Contains("8 pages", PromptSplit.BuiltinPresets[1].Text);
        Assert.Contains("show, don't tell", PromptSplit.BuiltinPresets[1].Name);

        var configJson = JsonSerializer.Serialize(PromptSplit.Config(new Settings()));
        using var configDoc = JsonDocument.Parse(configJson);
        var presets = configDoc.RootElement.GetProperty("presets");
        Assert.Equal(2, presets.GetArrayLength());
        Assert.Equal("story-8-pages", presets[1].GetProperty("id").GetString());

        Assert.Equal(2, DirectedPromptRewrite.BuiltinPresets.Count);
        Assert.Equal("default", DirectedPromptRewrite.BuiltinPresets[0].Id);
        Assert.Equal("", DirectedPromptRewrite.BuiltinPresets[0].Text);
        Assert.Equal("story-page", DirectedPromptRewrite.BuiltinPresets[1].Id);
        Assert.Contains("show, don't tell", DirectedPromptRewrite.BuiltinPresets[1].Name);
    }

    [Fact]
    public void ResolveSubmissionAppliesAndValidatesEditedPartPrompts()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, Source, TwoPartReply);
            var edited = new[]
            {
                "Ink style. Edited part one prompt with bright daylight.",
                "Ink style. Edited part two prompt with bright daylight.",
            };
            var (_, resolved) = PromptSplit.ResolveSubmission(
                id,
                Source,
                new[] { UiJobRunner.KeyRecraftV3 },
                Extra(UiJobRunner.KeyRecraftV3, ""),
                imageAttached: false,
                lookupId => store.ReadClaudePromptExchanges(Alice, 1, exchangeId: lookupId).SingleOrDefault(),
                editedPartPrompts: edited);
            Assert.Equal(2, resolved.ImageCount);
            Assert.Equal("Panels 1 to 4", resolved.Parts[0].Title);
            Assert.Equal(edited[0], resolved.Parts[0].Prompt);
            Assert.Equal("Panels 5 to 8", resolved.Parts[1].Title);
            Assert.Equal(edited[1], resolved.Parts[1].Prompt);

            var wrongCount = Assert.Throws<InvalidDataException>(() => PromptSplit.ResolveSubmission(
                id, Source, new[] { UiJobRunner.KeyRecraftV3 }, Extra(UiJobRunner.KeyRecraftV3, ""),
                imageAttached: false,
                lookupId => store.ReadClaudePromptExchanges(Alice, 1, exchangeId: lookupId).SingleOrDefault(),
                editedPartPrompts: new[] { "Only one" }));
            Assert.Contains("must have 2 entries", wrongCount.Message);

            var blankPart = Assert.Throws<InvalidDataException>(() => PromptSplit.ResolveSubmission(
                id, Source, new[] { UiJobRunner.KeyRecraftV3 }, Extra(UiJobRunner.KeyRecraftV3, ""),
                imageAttached: false,
                lookupId => store.ReadClaudePromptExchanges(Alice, 1, exchangeId: lookupId).SingleOrDefault(),
                editedPartPrompts: new[] { edited[0], "   " }));
            Assert.Contains("Part 2 prompt must not be empty", blankPart.Message);

            var overLimit = Assert.Throws<InvalidDataException>(() => PromptSplit.ResolveSubmission(
                id, Source, new[] { UiJobRunner.KeyRecraftV3 }, Extra(UiJobRunner.KeyRecraftV3, ""),
                imageAttached: false,
                lookupId => store.ReadClaudePromptExchanges(Alice, 1, exchangeId: lookupId).SingleOrDefault(),
                editedPartPrompts: new[] { new string('a', 1100), edited[1] }));
            Assert.Contains("at most 1,000 fit", overLimit.Message);
        });
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mic-split-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // A null reply makes the request itself fail.
    private sealed class FakeWriter : IManagerChatClient
    {
        private readonly string? _reply;
        private readonly string? _stop;
        public FakeWriter(string? reply, string? stop = null) => (_reply, _stop) = (reply, stop);
        public string Model => "fake";
        public string LastSystemPrompt { get; private set; } = "";
        public string LastUserMessage { get; private set; } = "";

        public Task<ManagerChatReply> CompleteAsync(string systemPrompt, IReadOnlyList<ManagerChatMessage> history, CancellationToken ct)
        {
            LastSystemPrompt = systemPrompt;
            LastUserMessage = history.Single().Text;
            if (_reply == null) throw new HttpRequestException("provider unavailable");
            return Task.FromResult(new ManagerChatReply
            {
                Text = _reply,
                ProviderReasoning = "",
                RawResponse = "{}",
                RedactedRequest = "",
                RequestBytes = 0,
                ProviderStop = _stop,
                Ms = 1,
                Model = "fake",
            });
        }
    }
}
