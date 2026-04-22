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

        public GeneratorGroups(Settings settings, int concurrency, MultiClientRunStats stats)
        {
            _settings = settings;
            _concurrency = concurrency;
            _stats = stats;
        }

        /// The active generator set for the current run.
        /// Edit this list to pick which generators hit each prompt.
        ///
        /// CURRENT MODE: gpt-image-2 smoke test — a single variant per prompt
        /// so we can watch one request/response at a time.
        public IEnumerable<IImageGenerator> GetAll()
        {
            return new List<IImageGenerator>
            {
                GptImage2HighSquare(),
                // --- other gpt-image-2 variants, enable once square is confirmed ---
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
                // RecraftAnyStyle(),
                // BFLv11_3_2(),
                // BFLv11Ultra_1_1(),
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
        // Quality must be low/medium/high (no "auto"). Size can be any of
        // 1024x1024, 1536x1024, 1024x1536, 2048x2048, 2048x1152, 3840x2160,
        // 2160x3840, or "auto". Moderation is optional ("" means don't send).
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
