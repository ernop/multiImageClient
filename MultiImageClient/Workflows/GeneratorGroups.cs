using IdeogramAPIClient;

using OpenAI.Images;

using RecraftAPIClient;

using System.Collections.Generic;

namespace MultiImageClient
{
    /// Declares which image generators are active for the current run.
    ///
    /// Design rules:
    ///   1. Only generators in the list returned by GetAll() are ever constructed.
    ///      If you don't use Recraft, you never need a Recraft API key.
    ///   2. Each generator is built by its own factory method so you can read the
    ///      active set at a glance and tweak one line at a time.
    ///   3. Add a new variant by adding a factory method below, then adding a call
    ///      in GetAll().
    ///
    /// Keep the factory methods even when commented out of GetAll() — they're the
    /// menu of things you can flip on.
    public class GeneratorGroups
    {
        private readonly Settings _settings;
        private readonly int _concurrency;
        private readonly MultiClientRunStats _stats;
        private readonly bool _fast;
        private readonly bool _quickTest;

        public GeneratorGroups(Settings settings, int concurrency, MultiClientRunStats stats, bool fast = false, bool quickTest = false)
        {
            _settings = settings;
            _concurrency = concurrency;
            _stats = stats;
            _fast = fast;
            _quickTest = quickTest;
        }

        /// The active generator set for the current run.
        /// Edit this list to pick which generators hit each prompt.
        ///
        /// DEFAULT MODE: gpt-image-2 random-AR + random-quality with moderation=low.
        /// Each call rolls a fresh {square|portrait|landscape} x {low|medium|high}
        /// so a single batch exercises the full option space of the endpoint.
        ///
        /// FAST MODE (--fast): a single fixed gpt-image-2 low/square/moderation=low
        /// call per prompt — cheapest (~$0.02) and usually completes in 10-15s.
        /// Intended for iteration and smoke-testing, not production runs.
        public IEnumerable<IImageGenerator> GetAll()
        {
            if (_quickTest)
            {
                return new List<IImageGenerator>
                {
                    GptImage2QuickTest(),
                };
            }

            if (_fast)
            {
                return new List<IImageGenerator>
                {
                    GptImage2FastTest(),
                };
            }

            return new List<IImageGenerator>
            {
                GptImage2RandomMinimalSafety(),
                // --- fixed gpt-image-2 variants, useful for targeted tests ---
                // GptImage2HighSquare(),
                // GptImage2MediumPortrait(),
                // GptImage2HighWide(),
                // GptImage2High2K(),
                // --- other OpenAI image models ---
                // Dalle3Square(),
                // Dalle3Wide(),
                // GptImage1HighSquare(),
                // GptImageMiniHighWide(),
                // --- non-OpenAI providers (require extra keys in settings.json) ---
                // Ideogram_V3_Wide_Quality(),
                // RecraftV4AnyStyle(),
                // BFLv11_3_2(),
                // BFLv11Ultra_1_1(),
                // BFLFlux2Pro_Square(),
                // BFLFlux2Max_Square(),
                // BFLFlux2Flex_Square(),       // typography-tuned; enable when you have text prompts
                // BFLFlux2Klein9b_Square(),    // sub-second cheap draft variant
                // RecraftV4ProRealisticPortrait(),
                // RecraftAnyStyle(),           // legacy V3
                // xAI Grok Imagine (launched 2026-01-28). Pro is 3.5x the price
                // (~$0.07 vs $0.02) and rate-limited to 30 rpm vs 300 rpm, so
                // we default to the cheap tier here and keep Pro as a toggle.
                GrokImagine_Square(),
                // GrokImaginePro_Square(),
                // GeminiNanoBanana(),
                // GoogleImagen4_2_5(),
            };
        }

        // ---------- OpenAI: DALL·E 3 ----------

        private Dalle3Generator Dalle3Square() =>
            new Dalle3Generator(_settings.OpenAIApiKey, _concurrency,
                GeneratedImageQuality.High, GeneratedImageSize.W1024xH1024, _stats, "");

        private Dalle3Generator Dalle3Wide() =>
            new Dalle3Generator(_settings.OpenAIApiKey, _concurrency,
                GeneratedImageQuality.High, GeneratedImageSize.W1792xH1024, _stats, "");

        // ---------- OpenAI: gpt-image-1 / gpt-image-1-mini ----------

        private GptImageOneGenerator GptImage1HighSquare() =>
            new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency,
                "1024x1024", "low", OpenAIGPTImageOneQuality.high,
                ImageGeneratorApiType.GptImage1, _stats, "");

        private GptImageOneGenerator GptImageMiniHighWide() =>
            new GptImageOneGenerator(_settings.OpenAIApiKey, _concurrency,
                "1536x1024", "low", OpenAIGPTImageOneQuality.high,
                ImageGeneratorApiType.GptImage1Mini, _stats, "");

        // ---------- OpenAI: gpt-image-2 (released 2026-04-21) ----------
        // Quality: low / medium / high / auto.
        // Size can be any of 1024x1024, 1536x1024, 1024x1536, 2048x2048,
        // 2048x1152, 3840x2160, 2160x3840, or "auto". Moderation is "auto"
        // (default) or "low" (minimal safety, permissible content only).
        //
        // Default for batch runs: random AR x random quality with moderation="low".
        // The size pool sticks to the three canonical 1024-edge aspect ratios so
        // per-call cost stays bounded. Quality pool is low/medium/high (no "auto"
        // — we want explicit control and predictable billing).
        // --fast: lowest quality, smallest size, permissive moderation. Deterministic
        // fixed variant so test runs are cheap and consistent across invocations.
        private GptImage2Generator GptImage2FastTest() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1024x1024", "low", OpenAIGPTImageOneQuality.low, _stats, "fast");

        // --quick-test: identical cheapest config as --fast, but saves each
        // streamed partial PNG under the day folder and pops it up in the
        // default image viewer as it arrives. Intended for interactive
        // development where watching the refinement trajectory is the point.
        private GptImage2Generator GptImage2QuickTest() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                sizePool: new[] { "1024x1024" },
                moderation: "low",
                qualityPool: new[] { OpenAIGPTImageOneQuality.low },
                stats: _stats,
                name: "quick",
                partialSaveFolder: _settings.ImageDownloadBaseFolder,
                popUpPartials: true);

        private GptImage2Generator GptImage2RandomMinimalSafety() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                sizePool: new[] { "1024x1024", "1024x1536", "1536x1024" },
                moderation: "low",
                qualityPool: new[]
                {
                    OpenAIGPTImageOneQuality.low,
                    OpenAIGPTImageOneQuality.medium,
                    OpenAIGPTImageOneQuality.high,
                },
                stats: _stats, name: "");

        private GptImage2Generator GptImage2HighSquare() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1024x1024", "", OpenAIGPTImageOneQuality.high, _stats, "");

        private GptImage2Generator GptImage2MediumPortrait() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1024x1536", "", OpenAIGPTImageOneQuality.medium, _stats, "");

        private GptImage2Generator GptImage2HighWide() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1536x1024", "", OpenAIGPTImageOneQuality.high, _stats, "");

        private GptImage2Generator GptImage2High2K() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "2048x2048", "", OpenAIGPTImageOneQuality.high, _stats, "");

        // ---------- Ideogram ----------

        private IdeogramGenerator Ideogram_V2_Design_16_10() =>
            new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency,
                IdeogramMagicPromptOption.ON, IdeogramAspectRatio.ASPECT_16_10,
                IdeogramStyleType.DESIGN, "", IdeogramModel.V_2, _stats, "");

        private IdeogramGenerator Ideogram_V2Turbo_Square() =>
            new IdeogramGenerator(_settings.IdeogramApiKey, _concurrency,
                IdeogramMagicPromptOption.OFF, IdeogramAspectRatio.ASPECT_1_1,
                null, "", IdeogramModel.V_2_TURBO, _stats, "");

        private IdeogramV3Generator Ideogram_V3_Wide_Quality() =>
            new IdeogramV3Generator(_settings.IdeogramApiKey, _concurrency,
                IdeogramV3StyleType.AUTO, IdeogramMagicPromptOption.ON,
                IdeogramAspectRatio.ASPECT_16_10, IdeogramRenderingSpeed.QUALITY,
                "", _stats, "");

        // ---------- Black Forest Labs (Flux) ----------

        private BFLGenerator BFLv11_3_2() =>
            new BFLGenerator(ImageGeneratorApiType.BFLv11, _settings.BFLApiKey,
                _concurrency, "3:2", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLv11Ultra_1_1() =>
            new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLv11Ultra_3_2_Upsampled() =>
            new BFLGenerator(ImageGeneratorApiType.BFLv11Ultra, _settings.BFLApiKey,
                _concurrency, "3:2", true, 1024, 1024, _stats, "");

        // ---------- Black Forest Labs (FLUX.2 — current generation) ----------
        //
        // FLUX.2 is megapixel-priced. 1024x1024 is roughly 1 MP so the per-image
        // cost tracks the headline rate in the docs (pro $0.03, max $0.07, etc.).
        // Klein is the cheap fast tier; flex is the typography-tuned variant with
        // adjustable steps/guidance (BFLGenerator sets sensible defaults).

        private BFLGenerator BFLFlux2Pro_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Pro, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLFlux2Pro_Wide() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Pro, _settings.BFLApiKey,
                _concurrency, "16:9", false, 1536, 864, _stats, "");

        private BFLGenerator BFLFlux2Max_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Max, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLFlux2Flex_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Flex, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLFlux2Klein4b_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Klein4b, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        private BFLGenerator BFLFlux2Klein9b_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2Klein9b, _settings.BFLApiKey,
                _concurrency, "1:1", false, 1024, 1024, _stats, "");

        // ---------- Recraft ----------

        private RecraftGenerator RecraftAnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1365x1024, RecraftStyle.any, null, null, null,
                _stats, "");

        private RecraftGenerator RecraftRealisticStudioPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x1024, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "");

        private RecraftGenerator RecraftVectorLineArt() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1365x1024, RecraftStyle.vector_illustration,
                RecraftVectorIllustrationSubstyle.line_art, null, null,
                _stats, "");

        // ---------- Recraft V4 (drop-in upgrade over V3) ----------
        // V4 raster is priced identically to V3 at $0.04/image and claims better
        // prompt adherence + text rendering. V4 Pro is $0.25 for high-res, print-
        // ready output. Pass model: RecraftModel.recraftv4 or .recraftv4pro to
        // RecraftGenerator and everything else (style/substyle/artistic_level)
        // works the same.

        private RecraftGenerator RecraftV4AnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1365x1024, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4RealisticStudioPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x1024, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4VectorLineArt() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1365x1024, RecraftStyle.vector_illustration,
                RecraftVectorIllustrationSubstyle.line_art, null, null,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4ProRealisticPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x1024, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "", model: RecraftModel.recraftv4pro);

        private RecraftGenerator RecraftV4ProAnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1365x1024, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4pro);

        // ---------- xAI Grok Imagine ----------
        //
        // Two tiers, same REST endpoint:
        //   grok-imagine-image       $0.02/image, 300 rpm
        //   grok-imagine-image-pro   $0.07/image,  30 rpm
        //
        // aspect_ratio accepts the xAI-documented set ("1:1", "3:4", "4:3",
        // "16:9", "9:16", "2:3", "3:2", "9:19.5", "19.5:9", "9:20", "20:9",
        // "1:2", "2:1", "auto"). quality is low|medium|high; resolution is
        // 1k|2k. All other knobs (size, style) are explicitly unsupported by
        // the xAI API and we do not send them.

        // Standard-tier Grok Imagine defaults to the maximum quality and
        // resolution this tier supports (high + 2k). Per xAI's pricing page
        // resolution doesn't affect per-image price on the standard tier
        // ($0.02 flat), so 2k is a free upgrade over 1k.
        public GrokImagineGenerator GrokImagine_Square() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImagine, _stats, "",
                aspectRatio: "1:1", quality: "high", resolution: "2k");

        public GrokImagineGenerator GrokImagine_Wide() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImagine, _stats, "",
                aspectRatio: "16:9", quality: "high", resolution: "2k");

        public GrokImagineGenerator GrokImagine_Portrait() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImagine, _stats, "",
                aspectRatio: "3:4", quality: "high", resolution: "2k");

        public GrokImagineGenerator GrokImaginePro_Square() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImaginePro, _stats, "",
                aspectRatio: "1:1", quality: "high", resolution: "2k");

        public GrokImagineGenerator GrokImaginePro_Portrait() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImaginePro, _stats, "",
                aspectRatio: "3:4", quality: "high", resolution: "2k");

        // ---------- Google ----------

        private GoogleGenerator GeminiNanoBanana() =>
            new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana,
                _settings.GoogleGeminiApiKey, _concurrency, _stats);

        private GoogleImagen4Generator GoogleImagen4_2_5() =>
            new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency,
                _stats, "", "2:5", "BLOCK_NONE",
                location: _settings.GoogleCloudLocation,
                projectId: _settings.GoogleCloudProjectId,
                googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath);
    }
}
