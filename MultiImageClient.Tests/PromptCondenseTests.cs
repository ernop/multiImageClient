using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MultiImageClient;

// A condensed prompt reaches only the targets the user condensed for, and only
// while it was made from the exact job prompt. See docs/prompt-length-limits.md.
public sealed class PromptCondenseTests
{
    private const string Alice = "login:alice";
    private static readonly string LongPrompt = new('a', 1_200);
    private static readonly string Condensed = new('c', 500);
    private static readonly IReadOnlyDictionary<string, string> NoExtraText =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void BudgetLeavesRoomForTheExtraTextAndItsSeparator()
    {
        var plan = UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyRecraftV3 },
            LongPrompt,
            Extra(UiJobRunner.KeyRecraftV3, " daylight "),
            imageAttached: false);

        Assert.Equal(new[] { UiJobRunner.KeyRecraftV3 }, plan.Keys);
        Assert.Equal(1_000 - 2 - 8, plan.Budget);
        Assert.Equal(1_200, plan.SourceCharacters);
    }

    [Fact]
    public void GroupBudgetIsTheSmallestTargetBudget()
    {
        var plan = UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyIdeogramV45, UiJobRunner.KeyRecraftV3 },
            new string('a', 12_000),
            Extra(UiJobRunner.KeyIdeogramV45, new string('x', 50)),
            imageAttached: false);

        Assert.Equal(1_000, plan.Budget);
    }

    [Fact]
    public void TargetsWithinTheirLimitKeepTheFullPrompt()
    {
        var error = Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyRecraftV3, UiJobRunner.KeyGpt2 }, LongPrompt, NoExtraText, imageAttached: false));

        Assert.Equal(
            $"{GeneratorPresentation.UiDisplayName(UiJobRunner.KeyGpt2)} is within its prompt limit, so it receives the full prompt.",
            error.Message);
    }

    [Fact]
    public void TargetsWithoutALimitKeepTheFullPrompt()
    {
        var error = Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyBfl }, new string('a', 50_000), NoExtraText, imageAttached: false));

        Assert.Equal(
            $"{GeneratorPresentation.UiDisplayName(UiJobRunner.KeyBfl)} has no prompt limit, so it receives the full prompt.",
            error.Message);
    }

    [Fact]
    public void ImageJobsUseTheImageRouteLimit()
    {
        var prompt = new string('a', 16_001);

        Assert.Equal(16_000, UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyGrokApi }, prompt, NoExtraText, imageAttached: false).Budget);
        Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyGrokApi }, prompt, NoExtraText, imageAttached: true));
    }

    [Fact]
    public void RequestsNameEachKnownImageTargetOnce()
    {
        Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            Array.Empty<string>(), LongPrompt, NoExtraText, imageAttached: false));
        Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyRecraftV3, UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText, imageAttached: false));
        Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyDescribeClaude }, LongPrompt, NoExtraText, imageAttached: false));
    }

    [Fact]
    public void ExtraTextMustLeaveRoomForAUsefulPrompt()
    {
        var error = Assert.Throws<InvalidDataException>(() => UiJobRunner.PlanPromptCondense(
            new[] { UiJobRunner.KeyRecraftV3 },
            new string('a', 1_000),
            Extra(UiJobRunner.KeyRecraftV3, new string('b', 950)),
            imageAttached: false));

        Assert.Equal(
            $"Endpoint extra text leaves {48:N0} characters for the prompt. "
            + $"A condensed prompt needs at least {UiJobRunner.MinCondensedPromptChars:N0}. Shorten the extra text.",
            error.Message);
    }

    [Fact]
    public void RepliesOverTheBudgetAreRejected()
    {
        var plan = new UiPromptCondensePlan(new[] { UiJobRunner.KeyRecraftV3 }, 990, 1_200);

        Assert.Null(UiJobRunner.DescribeCondensedPromptProblem(new string('a', 989) + "😀", plan, "Sonnet 5.5"));
        Assert.Equal(
            $"Sonnet 5.5 returned {991:N0} characters; at most {990:N0} fit. Nothing was applied. Press condense to try again.",
            UiJobRunner.DescribeCondensedPromptProblem(new string('a', 991), plan, "Sonnet 5.5"));
    }

    [Fact]
    public void InstructionStatesTheBudgetSourceAndTarget()
    {
        var instruction = DirectedPromptRewrite.CondenseInstruction(9_000, 12_000);

        Assert.StartsWith($"Condense this image-generation prompt to at most {9_000:N0} characters. ", instruction);
        Assert.Contains($"The source has {12_000:N0} characters. ", instruction);
        Assert.Contains($"Aim for about {8_100:N0} characters, about 68% of the source. ", instruction);
        Assert.Contains("Keep requested on-image text word for word. ", instruction);
        Assert.Contains("Never make the scene dim, murky, or dark unless the source asks for that. ", instruction);
        Assert.EndsWith("Return only the condensed prompt.", instruction);
    }

    [Fact]
    public void CondensedPromptReplacesOnlyItsTargetsPrompt()
    {
        var pd = new PromptDetails();
        pd.ReplacePrompt(LongPrompt, LongPrompt, TransformationType.InitialPrompt);
        var spec = new UiJobSpec
        {
            GeneratorKeys = [UiJobRunner.KeyRecraftV3, UiJobRunner.KeyGpt2],
            CondensedPrompts = new Dictionary<string, UiCondensedPrompt>(StringComparer.Ordinal)
            {
                [UiJobRunner.KeyRecraftV3] = new("exchange1", DirectedPromptRewrite.CondenseModel, Condensed),
            },
        };

        var recraft = pd.Copy();
        UiJobRunner.ApplyCondensedPrompt(recraft, spec, UiJobRunner.KeyRecraftV3);
        var gpt2 = pd.Copy();
        UiJobRunner.ApplyCondensedPrompt(gpt2, spec, UiJobRunner.KeyGpt2);

        Assert.Equal(Condensed, recraft.Prompt);
        var step = recraft.TransformationSteps.Last();
        Assert.Equal(TransformationType.ClaudeRewrite, step.TransformationType);
        Assert.Contains("prompt version exchange1", step.Explanation);
        Assert.Equal($"{Condensed}\n\ndaylight", UiJobRunner.WithEndpointExtraText(recraft.Prompt, " daylight "));
        Assert.Equal(LongPrompt, gpt2.Prompt);
        Assert.Equal(LongPrompt, pd.Prompt);
    }

    [Fact]
    public void IntakeAcceptsTheUsersCondensedPromptForItsTargetOnly()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed);

            var result = Resolve(store, Alice, Json((UiJobRunner.KeyRecraftV3, id)),
                new[] { UiJobRunner.KeyRecraftV3, UiJobRunner.KeyGpt2 }, LongPrompt, NoExtraText);

            var only = Assert.Single(result);
            Assert.Equal(UiJobRunner.KeyRecraftV3, only.Key);
            Assert.Equal(new UiCondensedPrompt(id, DirectedPromptRewrite.CondenseModel, Condensed), only.Value);
            Assert.Empty(Resolve(store, Alice, null, new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText));
        });
    }

    [Fact]
    public void IntakeRejectsAnotherUsersCondensedPrompt()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed);

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, "login:bob",
                Json((UiJobRunner.KeyRecraftV3, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText));

            Assert.Equal($"Condensed prompt {id} is not one of your saved prompt versions.", error.Message);
        });
    }

    [Fact]
    public void IntakeRejectsACondensedPromptMadeFromAnotherPrompt()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed);

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyRecraftV3, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt + "b", NoExtraText));

            Assert.Equal(
                $"Condensed prompt {id} was made from a different prompt. Condense the current prompt again.",
                error.Message);
        });
    }

    [Fact]
    public void IntakeRejectsOtherSavedPromptVersions()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed, purpose: "");

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyRecraftV3, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText));

            Assert.Equal($"Saved prompt version {id} is not a condensed prompt.", error.Message);
        });
    }

    [Fact]
    public void IntakeRejectsAFailedCondenseRequest()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, "", error: "provider unavailable");

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyRecraftV3, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText));

            Assert.Equal($"Condensed prompt {id} did not complete.", error.Message);
        });
    }

    [Fact]
    public void IntakeRejectsACondensedPromptThatNoLongerFits()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed);

            var error = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyRecraftV3, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt,
                Extra(UiJobRunner.KeyRecraftV3, new string('x', 600))));

            Assert.Equal(
                $"With its extra text, the condensed prompt has {1_102:N0} characters for "
                + $"{GeneratorPresentation.UiDisplayName(UiJobRunner.KeyRecraftV3)}; at most {1_000:N0} fit. Condense again.",
                error.Message);
        });
    }

    [Fact]
    public void IntakeRejectsTargetsThatDoNotNeedIt()
    {
        WithStore(store =>
        {
            var id = Save(store, Alice, LongPrompt, Condensed);

            var withinLimit = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyGpt2, id)), new[] { UiJobRunner.KeyGpt2 }, LongPrompt, NoExtraText));
            var unselected = Assert.Throws<InvalidDataException>(() => Resolve(store, Alice,
                Json((UiJobRunner.KeyRecraft, id)), new[] { UiJobRunner.KeyRecraftV3 }, LongPrompt, NoExtraText));

            Assert.EndsWith("is within its prompt limit, so it receives the full prompt.", withinLimit.Message);
            Assert.Equal($"Condensed prompts name unselected generator '{UiJobRunner.KeyRecraft}'.", unselected.Message);
        });
    }

    [Fact]
    public void IntakeRejectsMalformedCondensedPrompts()
    {
        WithStore(store =>
        {
            var selected = new[] { UiJobRunner.KeyRecraftV3 };

            foreach (var json in new[] { "[]", "{", $$"""{"{{UiJobRunner.KeyRecraftV3}}":5}""" })
            {
                Assert.Throws<InvalidDataException>(() => Resolve(store, Alice, json, selected, LongPrompt, NoExtraText));
            }
        });
    }

    [Fact]
    public void ReuseMatchesExactInputsAndPrefersTheNewestAnswer()
    {
        WithStore(store =>
        {
            Save(store, Alice, LongPrompt, "first", at: 1_800_000_001_000);
            var newest = Save(store, Alice, LongPrompt, "second", at: 1_800_000_002_000);
            Save(store, Alice, LongPrompt, "", at: 1_800_000_003_000, error: "provider unavailable");
            Save(store, Alice, LongPrompt, "advice", at: 1_800_000_004_000, purpose: "");
            UiClaudePromptExchangeRecord? Find(string identity, string model, string instruction, string prompt)
                => store.FindSucceededClaudePromptExchange(
                    identity, DirectedPromptRewrite.CondensePurpose, model, instruction, prompt);

            var found = Find(Alice, DirectedPromptRewrite.CondenseModel, "condense", LongPrompt);

            Assert.Equal(newest, found?.Id);
            Assert.Equal("second", found?.ResultPrompt);
            Assert.Equal(DirectedPromptRewrite.CondensePurpose, found?.Purpose);
            Assert.Null(Find("login:bob", DirectedPromptRewrite.CondenseModel, "condense", LongPrompt));
            Assert.Null(Find(Alice, DirectedPromptRewrite.OpusModel, "condense", LongPrompt));
            Assert.Null(Find(Alice, DirectedPromptRewrite.CondenseModel, "condense again", LongPrompt));
            Assert.Null(Find(Alice, DirectedPromptRewrite.CondenseModel, "condense", LongPrompt + " "));
        });
    }

    [Fact]
    public void CharacterWritingSkipsCondensedPrompts()
    {
        WithStore(store =>
        {
            Save(store, Alice, "a portrait of Mira", "Mira, condensed");
            var advice = Save(store, Alice, "Mira at the beach", "Mira on a bright beach", purpose: "");

            var matches = store.SearchClaudePromptExchanges(Alice, new[] { "Mira" });

            Assert.Equal(advice, Assert.Single(matches).RecordId);
        });
    }

    [Fact]
    public void OlderDatabasesGainThePurposeColumn()
    {
        var folder = CreateTempFolder();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "community.sqlite3")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ui_claude_prompt_exchanges (
                        id TEXT PRIMARY KEY,
                        requested_at_unix_ms INTEGER NOT NULL,
                        completed_at_unix_ms INTEGER,
                        identity_key TEXT NOT NULL,
                        actor_display TEXT NOT NULL,
                        model TEXT NOT NULL,
                        instruction TEXT NOT NULL,
                        original_prompt TEXT NOT NULL,
                        system_prompt TEXT NOT NULL,
                        wire_prompt TEXT NOT NULL,
                        raw_response TEXT NOT NULL,
                        result_prompt TEXT NOT NULL,
                        status TEXT NOT NULL,
                        error TEXT NOT NULL
                    );
                    INSERT INTO ui_claude_prompt_exchanges VALUES(
                        'old1', 1800000000000, 1800000000100, 'login:alice', 'Alice', 'claude-opus-5-5',
                        'expand', 'a city', 'system', 'wire', 'a vast city', 'a vast city', 'succeeded', '');
                    """;
                command.ExecuteNonQuery();
            }

            var store = CreateStore(folder);

            var record = Assert.Single(store.ReadClaudePromptExchanges(Alice));
            Assert.Equal("old1", record.Id);
            Assert.Equal("", record.Purpose);
            Assert.Null(store.FindSucceededClaudePromptExchange(
                Alice, DirectedPromptRewrite.CondensePurpose, "claude-opus-5-5", "expand", "a city"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static IReadOnlyDictionary<string, string> Extra(string key, string extraText)
        => new Dictionary<string, string>(StringComparer.Ordinal) { [key] = extraText };

    private static string Json(params (string Key, string ExchangeId)[] entries)
        => JsonSerializer.Serialize(entries.ToDictionary(entry => entry.Key, entry => entry.ExchangeId));

    private static string Save(
        UiCommunityStore store,
        string identity,
        string original,
        string result,
        string purpose = DirectedPromptRewrite.CondensePurpose,
        string error = "",
        long at = 1_800_000_000_000)
    {
        var exchange = store.StartClaudePromptExchange(
            identity, "Alice", DirectedPromptRewrite.CondenseModel, "condense", original, "system", "wire", at, purpose);
        store.CompleteClaudePromptExchange(exchange.Id, result, result, error, at + 100);
        return exchange.Id;
    }

    private static Dictionary<string, UiCondensedPrompt> Resolve(
        UiCommunityStore store,
        string identity,
        string? json,
        IReadOnlyCollection<string> selected,
        string prompt,
        IReadOnlyDictionary<string, string> extraTexts)
        => UiJobRunner.ResolveCondensedPrompts(json, selected, prompt, extraTexts, imageAttached: false,
            exchangeId => store.ReadClaudePromptExchanges(identity, 1, exchangeId: exchangeId).SingleOrDefault());

    private static void WithStore(Action<UiCommunityStore> body)
    {
        var folder = CreateTempFolder();
        try
        {
            body(CreateStore(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static UiCommunityStore CreateStore(string folder)
        => new(new Settings
        {
            ImageDownloadBaseFolder = folder,
            UiCommunityDbPath = Path.Combine(folder, "community.sqlite3"),
        });

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mic-condense-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
