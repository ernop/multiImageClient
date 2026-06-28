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
                // GptImage2HighQhdWide(),        // 2560x1440 QHD, cookbook's recommended upper reliability boundary
                // GptImage2LogoVariants(n: 4),   // one call -> N candidates; see cookbook 4.5
                // --- other OpenAI image models ---
                // Dalle3Square(),
                // Dalle3Wide(),
                // GptImage1HighSquare(),
                // GptImageMiniHighWide(),
                // --- non-OpenAI providers (require extra keys in settings.json) ---
                // IdeogramV4_Square(),         // Ideogram 4.0 (2026-06-03), current flagship
                // Ideogram_V3_Wide_Quality(),
                // RecraftV41AnyStyle(),        // Recraft V4.1 (2026), current flagship
                // RecraftV4AnyStyle(),
                // BFLv11_3_2(),
                // BFLv11Ultra_1_1(),
                // BFLFlux2ProPreview_Square(), // BFL's latest [pro], lands improvements first
                // BFLFlux2Pro_Square(),
                // BFLFlux2Max_Square(),
                // BFLFlux2Flex_Square(),       // typography-tuned; enable when you have text prompts
                // BFLFlux2Klein9b_Square(),    // sub-second cheap draft variant
                // LocalFlux2Uncensored_Square(), // local ComfyUI Flux2 Klein + uncensored text encoder workflow
                // RecraftV4ProRealisticPortrait(),
                // RecraftAnyStyle(),           // legacy V3
                // xAI Grok Imagine (launched 2026-01-28). Pro is 3.5x the price
                // (~$0.07 vs $0.02) and rate-limited to 30 rpm vs 300 rpm, so
                // we default to the cheap tier here and keep Pro as a toggle.
                GrokImagine_Square(),
                // GrokImaginePro_Square(),
                // GrokVideo_Wide(),            // VIDEO: mp4 saved to day\Video\, card in the grid
                // GeminiNanoBanana(),          // gemini-3.1-flash-image (Nano Banana 2), 1:1 2K
                // GeminiNanoBananaPro(),       // gemini-3-pro-image (Nano Banana Pro), 1:1 2K
                // GeminiNanoBananaTallPortrait(), // 9:16 2K — replacement for the old Imagen 2:5 slot
                // GeminiNanoBananaPro4K(),     // pro tier at 4K (~2x token cost)
                // GoogleImagen4_2_5(),         // DEAD 2026-06-24..30 (Imagen shutdown) — do not re-enable
            };
        }

        /// One representative generator per provider — the "contact sheet"
        /// acceptance set used by --all-providers. Each entry is the current
        /// flagship (June 2026) for that provider:
        ///   OpenAI   gpt-image-2 (high, square)
        ///   Ideogram Ideogram 4.0 (DEFAULT speed, 2048x2048)
        ///   BFL      flux-2-pro-preview (latest [pro])
        ///   Recraft  V4.1 (any style)
        ///   xAI      grok-imagine-image (high, 2k)
        ///   Google   gemini-3-pro-image (Nano Banana Pro)
        /// includeVideo additionally appends the xAI grok-imagine-video
        /// generator (mp4 saved to disk, PNG card in the grid).
        public IEnumerable<IImageGenerator> GetOnePerProvider(bool includeVideo = false)
        {
            var list = new List<IImageGenerator>
            {
                GptImage2HighSquare(),
                IdeogramV4_Square(),
                BFLFlux2ProPreview_Square(),
                RecraftV41AnyStyle(),
                GrokImagine_Square(),
                GeminiNanoBananaPro(),
            };
            if (includeVideo)
            {
                list.Add(GrokVideo_Wide());
            }
            return list;
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
        // 2048x1152, 2560x1440 (QHD — cookbook's recommended upper
        // reliability boundary), 3824x2144 (near-4K), or "auto". Cookbook
        // enforces max edge STRICTLY < 3840 so the legacy 3840x2160 is
        // treated as experimental in our size validator. Moderation is
        // "auto" (default) or "low" (minimal safety, permissible content
        // only).
        //
        // Default for batch runs: random AR x random quality with moderation="low".
        // The size pool sticks to the three canonical 1024-edge aspect ratios so
        // per-call cost stays bounded. Quality pool is low/medium/high (no "auto"
        // — we want explicit control and predictable billing).
        //
        // Variant factories take an optional `n` (images per call) to support
        // cookbook patterns like logo-variant generation (section 4.5) where
        // one round-trip returns N candidates. N=1 preserves prior behavior.
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

        private GptImage2Generator GptImage2RandomMinimalSafety(int n = 1) =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                sizePool: new[] { "1024x1024", "1024x1536", "1536x1024" },
                moderation: "low",
                qualityPool: new[]
                {
                    OpenAIGPTImageOneQuality.low,
                    OpenAIGPTImageOneQuality.medium,
                    OpenAIGPTImageOneQuality.high,
                },
                stats: _stats, name: "", imageCount: n);

        // Logo/variant exploration per cookbook 4.5: one API call, N
        // candidates returned in the same round-trip. Filenames pick up the
        // imgN suffix automatically via FileNameGenerator so the grid shows
        // all variants side-by-side. Kept off by default (in the commented
        // GetAll block) because it multiplies per-prompt cost linearly.
        private GptImage2Generator GptImage2LogoVariants(int n = 4) =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                sizePool: new[] { "1024x1024" },
                moderation: "low",
                qualityPool: new[] { OpenAIGPTImageOneQuality.medium },
                stats: _stats, name: "variants", imageCount: n);

        private GptImage2Generator GptImage2HighSquare() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1024x1024", "low", OpenAIGPTImageOneQuality.high, _stats, "");

        private GptImage2Generator GptImage2MediumPortrait() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1024x1536", "low", OpenAIGPTImageOneQuality.medium, _stats, "");

        private GptImage2Generator GptImage2HighWide() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "1536x1024", "low", OpenAIGPTImageOneQuality.high, _stats, "");

        private GptImage2Generator GptImage2High2K() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "2048x2048", "low", OpenAIGPTImageOneQuality.high, _stats, "");

        // Cookbook "recommended upper reliability boundary" at 2K / QHD.
        // Use when you want more detail than 2048 square but don't want to
        // tip into the experimental near-4K territory.
        private GptImage2Generator GptImage2HighQhdWide() =>
            new GptImage2Generator(_settings.OpenAIApiKey, _concurrency,
                "2560x1440", "low", OpenAIGPTImageOneQuality.high, _stats, "");

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

        // ---------- Ideogram 4.0 (released 2026-06-03) ----------
        // JSON endpoint /v1/ideogram-v4/generate; 2K-native resolutions only
        // ("2048x2048", "2304x1728", "2560x1440", ...). rendering_speed:
        // FLASH (cheapest) | TURBO | DEFAULT | QUALITY. No style_type /
        // magic_prompt knobs — text_prompt is auto-expanded server-side into
        // the structured JSON prompt contract.

        private IdeogramV4Generator IdeogramV4_Square() =>
            new IdeogramV4Generator(_settings.IdeogramApiKey, _concurrency,
                "2048x2048", IdeogramRenderingSpeed.DEFAULT, _stats, "");

        private IdeogramV4Generator IdeogramV4_Wide_Quality() =>
            new IdeogramV4Generator(_settings.IdeogramApiKey, _concurrency,
                "2560x1440", IdeogramRenderingSpeed.QUALITY, _stats, "");

        private IdeogramV4Generator IdeogramV4_Flash() =>
            new IdeogramV4Generator(_settings.IdeogramApiKey, _concurrency,
                "2048x2048", IdeogramRenderingSpeed.FLASH, _stats, "");

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

        // flux-2-pro-preview: where BFL lands the latest [pro] improvements
        // first (~2x speed at no quality cost as of mid-2026). Same wire
        // contract and price as flux-2-pro; prefer this unless a run needs
        // pinned-model reproducibility.
        private BFLGenerator BFLFlux2ProPreview_Square() =>
            new BFLGenerator(ImageGeneratorApiType.BFLFlux2ProPreview, _settings.BFLApiKey,
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

        private LocalFlux2ComfyGenerator LocalFlux2Uncensored_Square() =>
            new LocalFlux2ComfyGenerator(_settings, _concurrency, _stats, "local-flux2-uncensored");

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
        //
        // IMPORTANT: V4/V4.1 use a DIFFERENT size set than V2/V3. The old
        // 1365x1024 / 2048x1024 etc. are rejected with invalid_request_parameter.
        // Standard models: 1:1 is 1024x1024 (others: 1536x768, 1280x832,
        // 1216x896, ...). Pro models: 1:1 is 2048x2048 (others: 3072x1536,
        // 2560x1664, 2432x1792, ...). See recraft.ai/docs appendix.

        private RecraftGenerator RecraftV4AnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1024x1024, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4RealisticStudioPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1024x1024, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4VectorLineArt() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1024x1024, RecraftStyle.vector_illustration,
                RecraftVectorIllustrationSubstyle.line_art, null, null,
                _stats, "", model: RecraftModel.recraftv4);

        private RecraftGenerator RecraftV4ProRealisticPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x2048, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "", model: RecraftModel.recraftv4pro);

        private RecraftGenerator RecraftV4ProAnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x2048, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4pro);

        // ---------- Recraft V4.1 (2026, current flagship) ----------
        // Drop-in over V4: better photorealism, new illustration styles, and
        // strong short-prompt aesthetics. API model strings: recraftv4_1 /
        // recraftv4_1_pro (the API's own default model is now recraftv4_1).
        // Same style/substyle/artistic_level plumbing as V3/V4, and the same
        // V4 size set (1024x1024 standard / 2048x2048 pro for 1:1).

        private RecraftGenerator RecraftV41AnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1024x1024, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4_1);

        private RecraftGenerator RecraftV41RealisticStudioPortrait() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._1024x1024, RecraftStyle.realistic_image,
                null, null, RecraftRealisticImageSubstyle.studio_portrait,
                _stats, "", model: RecraftModel.recraftv4_1);

        private RecraftGenerator RecraftV41ProAnyStyle() =>
            new RecraftGenerator(_settings.RecraftApiKey, _concurrency,
                RecraftImageSize._2048x2048, RecraftStyle.any, null, null, null,
                _stats, "", model: RecraftModel.recraftv4_1_pro);

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
                aspectRatio: "1:1", quality: "high", resolution: "2k", settings: _settings);

        public GrokImagineGenerator GrokImagine_Wide() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImagine, _stats, "",
                aspectRatio: "16:9", quality: "high", resolution: "2k", settings: _settings);

        public GrokImagineGenerator GrokImagine_Portrait() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImagine, _stats, "",
                aspectRatio: "3:4", quality: "high", resolution: "2k", settings: _settings);

        public GrokImagineGenerator GrokImaginePro_Square() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImaginePro, _stats, "",
                aspectRatio: "1:1", quality: "high", resolution: "2k", settings: _settings);

        public GrokImagineGenerator GrokImaginePro_Portrait() =>
            new GrokImagineGenerator(_settings.XAIGrokApiKey, _concurrency,
                ImageGeneratorApiType.GrokImaginePro, _stats, "",
                aspectRatio: "3:4", quality: "high", resolution: "2k", settings: _settings);

        // ---------- xAI Grok Imagine VIDEO ----------
        //
        // grok-imagine-video via the async /v1/videos/generations endpoint
        // (start + poll). Duration 1-15s; resolution 480p/720p/1080p; per-
        // second billing (roughly $0.05/s at 480p). The generator downloads
        // the mp4 itself into {day}\Video\ and contributes a PNG "video card"
        // to combined grids, so it multiplexes alongside image generators.

        public GrokImagineVideoGenerator GrokVideo_Wide() =>
            new GrokImagineVideoGenerator(_settings.XAIGrokApiKey, _concurrency,
                _stats, _settings, "",
                aspectRatio: "16:9", resolution: "480p", durationSeconds: 6);

        public GrokImagineVideoGenerator GrokVideo_Wide720p() =>
            new GrokImagineVideoGenerator(_settings.XAIGrokApiKey, _concurrency,
                _stats, _settings, "",
                aspectRatio: "16:9", resolution: "720p", durationSeconds: 8);

        // ---------- Google ----------
        //
        // June 2026: ALL dedicated Imagen endpoints shut down 06-24..30.
        // Gemini image models ("Nano Banana") are the replacement family:
        //   gemini-3.1-flash-image  (Nano Banana 2)  — fast/cheap tier
        //   gemini-3-pro-image      (Nano Banana Pro) — reasoning tier, 4K-capable
        // Both take an optional aspectRatio ("1:1","2:3","3:2","3:4","4:3",
        // "4:5","5:4","9:16","16:9","21:9") and imageSize ("512" flash-only,
        // "1K","2K","4K" — uppercase K). 2K costs the same tokens as 1K, so
        // it's the default here; only 4K is pricier.
        // These need only GoogleGeminiApiKey (AI Studio key) — no Vertex
        // project/service-account setup like the dead Imagen path required.

        private GoogleGenerator GeminiNanoBanana() =>
            new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana,
                _settings.GoogleGeminiApiKey, _concurrency, _stats,
                aspectRatio: "1:1", imageSize: "2K");

        private GoogleGenerator GeminiNanoBananaPro() =>
            new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBananaPro,
                _settings.GoogleGeminiApiKey, _concurrency, _stats,
                aspectRatio: "1:1", imageSize: "2K");

        // Direct replacement for the old GoogleImagen4_2_5() slot: tall
        // portrait output on the cheap flash tier. Imagen's "2:5" has no
        // exact Gemini equivalent; 9:16 is the closest supported ratio.
        private GoogleGenerator GeminiNanoBananaTallPortrait() =>
            new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBanana,
                _settings.GoogleGeminiApiKey, _concurrency, _stats,
                aspectRatio: "9:16", imageSize: "2K");

        private GoogleGenerator GeminiNanoBananaPro4K() =>
            new GoogleGenerator(ImageGeneratorApiType.GoogleNanoBananaPro,
                _settings.GoogleGeminiApiKey, _concurrency, _stats,
                aspectRatio: "1:1", imageSize: "4K");

#pragma warning disable CS0618 // kept until the Imagen shutdown actually lands
        private GoogleImagen4Generator GoogleImagen4_2_5() =>
            new GoogleImagen4Generator(_settings.GoogleGeminiApiKey, _concurrency,
                _stats, "", "2:5", "BLOCK_NONE",
                location: _settings.GoogleCloudLocation,
                projectId: _settings.GoogleCloudProjectId,
                googleServiceAccountKeyPath: _settings.GoogleServiceAccountKeyPath);
#pragma warning restore CS0618
    }
}
