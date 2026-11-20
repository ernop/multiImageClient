#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

using IdeogramAPIClient;
using RecraftAPIClient;

namespace MultiImageClient
{
    public enum UiTargetKind
    {
        Image,
        Video,
        Describe,
        LayoutMap,
    }

    public sealed record UiTargetDescriptor
    {
        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public required string ContactSheetProvider { get; init; }
        public required UiTargetKind Kind { get; init; }
        public required string SchedulerLane { get; init; }
        public string? ProviderAccountKey { get; init; }
        public string Detail { get; init; } = "";
        public bool InUiConfig { get; init; } = true;
        public bool ImageCapable { get; init; }
        public bool ImageAspectOverride { get; init; }
        public bool SketchCapable { get; init; }
        public bool DefaultSelected { get; init; }
        public bool UsesGptAntiMurkDefault { get; init; }
        public int MaxInputImages { get; init; } = 1;
        public string InputImageRole { get; init; } = "NOT sent (text-only target, prompt only)";
        public int? TextPromptMaxChars { get; init; }
        public int? ImagePromptMaxChars { get; init; }
        public int? MaxPromptUtf8Bytes { get; init; }
        public ImageGeneratorApiType? BflApiType { get; init; }
        public RecraftModel? RecraftModel { get; init; }
        public ImageGeneratorApiType? RecraftApiType { get; init; }

        public bool IsAnalysis => Kind is UiTargetKind.Describe or UiTargetKind.LayoutMap;
        public bool IsConfigurableEndpoint => Kind is UiTargetKind.Image or UiTargetKind.Describe or UiTargetKind.LayoutMap;
        public int? MaxPromptChars(bool imageSent) => imageSent ? ImagePromptMaxChars : TextPromptMaxChars;
    }

    public sealed record UiStandardGeneratorGroup(string Id, string Name, IReadOnlyList<string> GeneratorKeys);

    /// Single source of truth for every UI image, video, describe, and
    /// analysis target. Presentation labels, scheduler lanes, billing hints,
    /// prompt limits, capabilities, and /api/config metadata all derive here.
    public static class UiTargetCatalog
    {
        public const string DefaultGptAntiMurkGuidanceText =
            "Render in normal daytime lighting. Absolutely do not make the image dim, murky, grimy, "
            + "muddy, gloomy, shadow-choked, underexposed, hazy, dusk-like, night-like, or dark. "
            + "No smudged, muddy, overly fine micro-texture.";

        public const string AccountOpenAi = "openai";
        public const string AccountIdeogram = "ideogram";
        public const string AccountRecraft = "recraft";
        public const string AccountBfl = "bfl";
        public const string AccountKrea = "krea";
        public const string AccountGoogle = "google";
        public const string AccountXai = "xai";
        public const string AccountAnthropic = "anthropic";

        public static readonly IReadOnlyList<UiTargetDescriptor> All =
        [
            // OpenAI GPT Image family (2.6, 2, 2.5)
            new()
            {
                Key = UiJobRunner.KeyGpt26Sunburst,
                DisplayName = "gpt-image-2.6 sunburst",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI flagship image model (2026-11-04). /edits with up to 4 images, /generations otherwise; supports xhigh/max quality and streaming.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                UsesGptAntiMurkDefault = true,
                MaxInputImages = 4,
                InputImageRole = "edit source",
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGpt26Flare,
                DisplayName = "gpt-image-2.6 flare",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI fast GPT Image 2.6 tier (2026-11-04). /edits with up to 4 images, /generations otherwise; supports xhigh/max quality and streaming.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                UsesGptAntiMurkDefault = true,
                MaxInputImages = 4,
                InputImageRole = "edit source",
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGpt2,
                DisplayName = "gpt-image-2",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI gpt-image-2. /edits with up to 4 ordered images, /generations otherwise; default AR matches the primary input.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                DefaultSelected = true,
                UsesGptAntiMurkDefault = true,
                MaxInputImages = 4,
                InputImageRole = "edit source",
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGpt25Sunburst,
                DisplayName = "gpt-image-2.5 sunburst",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI previous flagship image model (2026-09-08). /edits with up to 4 images, /generations otherwise; supports xhigh/max quality.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                UsesGptAntiMurkDefault = true,
                MaxInputImages = 4,
                InputImageRole = "edit source",
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGpt25Flare,
                DisplayName = "gpt-image-2.5 flare",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI fast GPT Image 2.5 tier (2026-09-08). /edits with up to 4 images, /generations otherwise; supports xhigh/max quality.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                UsesGptAntiMurkDefault = true,
                MaxInputImages = 4,
                InputImageRole = "edit source",
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },

            // xAI Grok web & API
            new()
            {
                Key = UiJobRunner.KeyGrokWeb,
                DisplayName = "grok-web pro",
                ContactSheetProvider = "xAI via grok.com",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneGrokWebWs,
                Detail = "grok.com cookie session. Text uses imagine WebSocket (auto = 1:1, side-by-side up to 4); edits use app-chat and inherit source shape.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                DefaultSelected = true,
                InputImageRole = "edit source",
                MaxPromptUtf8Bytes = GrokWebClient.MaxPromptUtf8Bytes,
            },
            new()
            {
                Key = UiJobRunner.KeyGrokWebChat,
                DisplayName = "grok-web chat",
                ContactSheetProvider = "xAI via grok.com",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneGrokWebWs,
                Detail = "grok.com chat door (grok-3 calls the image tool). Sends no aspect ratio; explicit AR is unavailable with an image.",
                ImageCapable = true,
                ImageAspectOverride = false,
                SketchCapable = true,
                InputImageRole = "edit source (chat door)",
            },
            new()
            {
                Key = UiJobRunner.KeyGrokApi,
                DisplayName = "grok-api",
                ContactSheetProvider = "xAI API · grok-imagine-image",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneXaiApi,
                ProviderAccountKey = AccountXai,
                Detail = "api.x.ai legacy grok-imagine-image 1.0 (no quality knob; primary input only on edits).",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                InputImageRole = "edit source",
                TextPromptMaxChars = GrokImagineGenerator.LegacyModelMaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGrokApiPro,
                DisplayName = "grok-api 2.0",
                ContactSheetProvider = "xAI API · grok-imagine-image-2.0",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneXaiApi,
                ProviderAccountKey = AccountXai,
                Detail = "api.x.ai grok-imagine-image-2.0. Quality maps to low/medium/auto; edits accept up to 5 ordered images; supports 21:9 and 5:2.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                MaxInputImages = 5,
                InputImageRole = "edit source",
            },

            // Krea 2
            new()
            {
                Key = UiJobRunner.KeyKrea,
                DisplayName = "Krea 2 Medium",
                ContactSheetProvider = "Krea",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneKrea,
                ProviderAccountKey = AccountKrea,
                Detail = "Krea 2 foundation model (1K only). Attached image rides as a 0.6-strength style reference; n runs separate calls.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "style/reference image",
            },
            new()
            {
                Key = UiJobRunner.KeyKreaTurbo,
                DisplayName = "Krea 2 Medium Turbo",
                ContactSheetProvider = "Krea",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneKrea,
                ProviderAccountKey = AccountKrea,
                Detail = "Fastest Krea 2 variant (1K only). Attached image rides as a 0.6-strength style reference; n runs separate calls.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "style/reference image",
            },
            new()
            {
                Key = UiJobRunner.KeyKreaLarge,
                DisplayName = "Krea 2 Large",
                ContactSheetProvider = "Krea",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneKrea,
                ProviderAccountKey = AccountKrea,
                Detail = "Highest-fidelity Krea 2 variant (1K only). Attached image rides as a 0.6-strength style reference; n runs separate calls.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "style/reference image",
            },

            // Ideogram 4.6 / 4.5 / 4.0 / V3 / V2
            new()
            {
                Key = UiJobRunner.KeyIdeogramV46,
                DisplayName = "Ideogram 4.6",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Ideogram 4.6 (2026-11-19). Text: 2K ($0.03–$0.10) or native 4K at detail=max ($0.06–$0.20). Image: Precise Edit at source size (+4 references; no AR override). 10,000-char limit.",
                ImageCapable = true,
                ImageAspectOverride = false,
                MaxInputImages = 5,
                InputImageRole = "precise edit source (first image edited, later images references)",
                TextPromptMaxChars = IdeogramClient.IdeogramV45MaxPromptChars,
                ImagePromptMaxChars = IdeogramClient.IdeogramV45MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyIdeogramV45,
                DisplayName = "Ideogram 4.5",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Ideogram 4.5 (2026-09-30). Text: 2K Generate ($0.03–$0.10). Image: Precise Edit at source size (+4 references; no AR override). 10,000-char limit.",
                ImageCapable = true,
                ImageAspectOverride = false,
                DefaultSelected = true,
                MaxInputImages = 5,
                InputImageRole = "precise edit source (first image edited, later images references)",
                TextPromptMaxChars = IdeogramClient.IdeogramV45MaxPromptChars,
                ImagePromptMaxChars = IdeogramClient.IdeogramV45MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyIdeogram,
                DisplayName = "Ideogram 4.0",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Ideogram 4.0 (2K). Text uses Generate; image uses Remix at nearest 2K size (explicit shape overrides). n runs separate calls.",
                ImageCapable = true,
                ImageAspectOverride = true,
                DefaultSelected = true,
                InputImageRole = "image remix source",
            },
            new()
            {
                Key = UiJobRunner.KeyIdeogramV3,
                DisplayName = "Ideogram V3",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Ideogram 3.0. Attached image rides as style reference (auto matches source); supports explicit AR and n up to 8.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "style/reference image",
            },
            new()
            {
                Key = UiJobRunner.KeyIdeogramV2,
                DisplayName = "Ideogram V2",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Legacy Ideogram 2.0 text-to-image only; shape and Magic Prompt honored.",
                ImageCapable = false,
                ImageAspectOverride = false,
            },

            // Recraft V4.1 / V4 / V3
            new()
            {
                Key = UiJobRunner.KeyRecraft,
                DisplayName = "Recraft V4.1",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Recraft V4.1 raster (1MP, n up to 6). Image-to-image keeps source size with no AR override.",
                ImageCapable = true,
                ImageAspectOverride = false,
                DefaultSelected = true,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1),
                RecraftModel = RecraftModel.recraftv4_1,
                RecraftApiType = ImageGeneratorApiType.RecraftV41,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV41Flash,
                DisplayName = "Recraft V4.1 Flash V2",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Fast, low-cost 1K Recraft raster ($0.007/image, n up to 6, 10,000-char limit). Text-to-image only.",
                ImageCapable = false,
                ImageAspectOverride = false,
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_flash_v2),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_flash_v2),
                RecraftModel = RecraftModel.recraftv4_1_flash_v2,
                RecraftApiType = ImageGeneratorApiType.RecraftV41FlashV2,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV41Utility,
                DisplayName = "Recraft V4.1 Utility",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Simpler V4.1 raster with flatter lighting (1MP, n up to 6). Image-to-image keeps source size.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_utility),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_utility),
                RecraftModel = RecraftModel.recraftv4_1_utility,
                RecraftApiType = ImageGeneratorApiType.RecraftV41Utility,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV41Pro,
                DisplayName = "Recraft V4.1 Pro",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "High-resolution 4MP V4.1 raster (n up to 6). Image-to-image keeps source size.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_pro),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_pro),
                RecraftModel = RecraftModel.recraftv4_1_pro,
                RecraftApiType = ImageGeneratorApiType.RecraftV41Pro,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV41Vector,
                DisplayName = "Recraft V4.1 Vector Woke",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Native SVG vector output for logos, icons, and typography (n up to 6); cards show raster previews.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_vector),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_1_vector),
                RecraftModel = RecraftModel.recraftv4_1_vector,
                RecraftApiType = ImageGeneratorApiType.RecraftV41Vector,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV3,
                DisplayName = "Recraft V3",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Previous-generation Recraft V3 raster (1,000-char limit, n up to 6). Image-to-image keeps source size.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv3),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv3),
                RecraftModel = RecraftModel.recraftv3,
                RecraftApiType = ImageGeneratorApiType.Recraft,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV4,
                DisplayName = "Recraft V4",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Previous-generation Recraft V4 raster (1MP, n up to 6). Image-to-image keeps source size.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4),
                RecraftModel = RecraftModel.recraftv4,
                RecraftApiType = ImageGeneratorApiType.RecraftV4,
            },
            new()
            {
                Key = UiJobRunner.KeyRecraftV4Pro,
                DisplayName = "Recraft V4 Pro",
                ContactSheetProvider = "Recraft",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneRecraft,
                ProviderAccountKey = AccountRecraft,
                Detail = "Previous-generation Recraft V4 Pro raster (4MP, n up to 6). Image-to-image keeps source size.",
                ImageCapable = true,
                ImageAspectOverride = false,
                InputImageRole = "image-to-image source",
                TextPromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_pro),
                ImagePromptMaxChars = RecraftGenerator.MaxPromptChars(RecraftModel.recraftv4_pro),
                RecraftModel = RecraftModel.recraftv4_pro,
                RecraftApiType = ImageGeneratorApiType.RecraftV4Pro,
            },

            // BFL FLUX 3 / 2 / 1
            new()
            {
                Key = UiJobRunner.KeyBflFlux3,
                DisplayName = "FLUX 3 Image",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "BFL flagship FLUX 3 (/v1/flux-3-image). Up to 10 reference images; detail maps to 1K ($0.048), 2K ($0.100), or 4K ($0.607); reference edits cap at 2K or match_input.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                MaxInputImages = 10,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux3,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux3Klein,
                DisplayName = "FLUX 3 Image Klein",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "BFL fast FLUX 3 Klein (/v1/flux-3-image-klein). Up to 10 reference images; detail maps to 1K ($0.009), 2K ($0.014), or 4K ($0.036); reference edits cap at 2K or match_input.",
                ImageCapable = true,
                ImageAspectOverride = true,
                MaxInputImages = 10,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux3Klein,
            },
            new()
            {
                Key = UiJobRunner.KeyBfl,
                DisplayName = "FLUX.2 Pro Preview",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "BFL FLUX.2 Pro Preview. Generation and editing; shape + detail map to ~1 MP or ~4 MP; no n.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                DefaultSelected = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2ProPreview,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Pro,
                DisplayName = "FLUX.2 Pro (pinned)",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Fixed FLUX.2 Pro snapshot for reproducible generation and editing.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Pro,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Max,
                DisplayName = "FLUX.2 Max",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Highest-quality FLUX.2 model with grounding search.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Max,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Flex,
                DisplayName = "FLUX.2 Flex",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Typography and fine-detail FLUX.2 model (40 steps, guidance 4.5).",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Flex,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Klein4b,
                DisplayName = "FLUX.2 Klein 4B",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Hosted FLUX.2 Klein 4B (currently disabled by operator).",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Klein4b,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Klein9bPreview,
                DisplayName = "FLUX.2 Klein 9B Preview",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Hosted FLUX.2 Klein 9B Preview (currently disabled by operator).",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Klein9bPreview,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux2Klein9b,
                DisplayName = "FLUX.2 Klein 9B (pinned)",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Hosted FLUX.2 Klein 9B snapshot (currently disabled by operator).",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFlux2Klein9b,
            },
            new()
            {
                Key = UiJobRunner.KeyBflKontextPro,
                DisplayName = "FLUX.1 Kontext Pro",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Previous-generation FLUX.1 Kontext Pro generation/editing model.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFluxKontextPro,
            },
            new()
            {
                Key = UiJobRunner.KeyBflKontextMax,
                DisplayName = "FLUX.1 Kontext Max",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Previous-generation FLUX.1 Kontext Max generation/editing model.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "edit/reference source",
                BflApiType = ImageGeneratorApiType.BFLFluxKontextMax,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux11Ultra,
                DisplayName = "FLUX1.1 Pro Ultra",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Previous-generation up-to-4MP model with optional image remix.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "image remix/reference source",
                BflApiType = ImageGeneratorApiType.BFLv11Ultra,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFlux11,
                DisplayName = "FLUX1.1 Pro",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Previous-generation text-to-image model with optional image remix (edges <= 1440).",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "image remix/reference source",
                BflApiType = ImageGeneratorApiType.BFLv11,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFluxPro,
                DisplayName = "FLUX.1 Pro (compatibility)",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Compatibility target (disabled after HTTP 403).",
                ImageCapable = false,
                ImageAspectOverride = false,
                BflApiType = ImageGeneratorApiType.BFLFluxPro,
            },
            new()
            {
                Key = UiJobRunner.KeyBflFluxDev,
                DisplayName = "FLUX.1 Dev",
                ContactSheetProvider = "Black Forest Labs",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneBfl,
                ProviderAccountKey = AccountBfl,
                Detail = "Older hosted FLUX.1 Dev endpoint with optional image prompt (edges <= 1440).",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "image remix/reference source",
                BflApiType = ImageGeneratorApiType.BFLFluxDev,
            },

            // Google Nano Banana
            new()
            {
                Key = UiJobRunner.KeyGoogle,
                DisplayName = "Nano Banana 2.1",
                ContactSheetProvider = "Google · gemini-nano-banana-2.1",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = "Google gemini-nano-banana-2.1. Detail maps to 1K/2K/4K; auto matches nearest Gemini AR on edits; no n.",
                ImageCapable = true,
                ImageAspectOverride = true,
                SketchCapable = true,
                DefaultSelected = true,
                InputImageRole = "style/reference image",
            },
            new()
            {
                Key = UiJobRunner.KeyGooglePro,
                DisplayName = "Nano Banana Pro",
                ContactSheetProvider = "Google · gemini-3-pro-image",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = "Google gemini-3-pro-image. Detail maps to 1K/2K/4K; auto matches nearest Gemini AR on edits; no n.",
                ImageCapable = true,
                ImageAspectOverride = true,
                InputImageRole = "style/reference image",
            },

            // Legacy OpenAI gpt-image-1 / mini
            new()
            {
                Key = UiJobRunner.KeyGpt1,
                DisplayName = "gpt-image-1",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "Retired by OpenAI on 2026-10-23; kept only for historical job display.",
                ImageCapable = false,
                ImageAspectOverride = false,
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },
            new()
            {
                Key = UiJobRunner.KeyGpt1Mini,
                DisplayName = "gpt-image-1-mini",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = "OpenAI lower-cost 1K image model. Shape, quality, moderation, and n honored; text-only.",
                ImageCapable = false,
                ImageAspectOverride = false,
                TextPromptMaxChars = GptImage2Generator.MaxPromptChars,
                ImagePromptMaxChars = GptImage2Generator.MaxPromptChars,
            },

            // Local ComfyUI & non-chooser image/video targets
            new()
            {
                Key = UiJobRunner.KeyLocalKlein,
                DisplayName = "local FLUX.2 Klein",
                ContactSheetProvider = "Black Forest Labs model via local ComfyUI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneComfyUi,
                Detail = "Configured local ComfyUI FLUX.2 Klein workflow.",
                ImageCapable = false,
                ImageAspectOverride = false,
            },
            new()
            {
                Key = UiJobRunner.KeyLocalZImage,
                DisplayName = "local Z-Image Turbo",
                ContactSheetProvider = "Tongyi-MAI model via local ComfyUI",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneComfyUi,
                Detail = "Configured local ComfyUI Z-Image workflow.",
                ImageCapable = false,
                ImageAspectOverride = false,
            },
            new()
            {
                Key = UiJobRunner.KeyMetaWeb,
                DisplayName = "Meta AI Muse Image (meta-web)",
                ContactSheetProvider = "Meta via meta.ai",
                Kind = UiTargetKind.Image,
                SchedulerLane = UiTargetScheduler.LaneMetaWeb,
                InUiConfig = false,
                ImageCapable = false,
                ImageAspectOverride = false,
            },
            new()
            {
                Key = UiJobRunner.KeyGrokWebVideo,
                DisplayName = "grok-web video",
                ContactSheetProvider = "xAI via grok.com",
                Kind = UiTargetKind.Video,
                SchedulerLane = UiTargetScheduler.LaneGrokWebBrowser,
                InUiConfig = false,
                InputImageRole = "video source",
            },

            // Describe & analysis targets (image -> text / layout map)
            new()
            {
                Key = UiJobRunner.KeyDescribeIdeogram,
                DisplayName = "Ideogram describe",
                ContactSheetProvider = "Ideogram",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneIdeogram,
                ProviderAccountKey = AccountIdeogram,
                Detail = "Ideogram /describe (V_3). Fixed built-in captioner ($0.01/image); ignores your prompt text.",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeOpenAi,
                DisplayName = $"OpenAI describe ({OpenAIVisionDescriber.DefaultModel})",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = $"OpenAI {OpenAIVisionDescriber.DefaultModel} vision at low reasoning ($2/$10 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeOpenAiAstra,
                DisplayName = $"OpenAI describe ({OpenAIVisionDescriber.AstraModel})",
                ContactSheetProvider = "OpenAI",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneOpenAi,
                ProviderAccountKey = AccountOpenAi,
                Detail = $"OpenAI {OpenAIVisionDescriber.AstraModel} flagship vision at low reasoning ($8/$40 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeClaude,
                DisplayName = $"Claude describe ({ClaudeVisionDescriber.DefaultModel})",
                ContactSheetProvider = "Anthropic",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneAnthropic,
                ProviderAccountKey = AccountAnthropic,
                Detail = $"Anthropic {ClaudeVisionDescriber.DefaultModel} vision ($3/$15 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGemini,
                DisplayName = $"Gemini describe ({GeminiVisionDescriber.DefaultModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.DefaultModel} fast vision at low thinking.",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGeminiNeon,
                DisplayName = $"Gemini describe ({GeminiVisionDescriber.NeonModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.NeonModel} flagship vision at low thinking ($3/$15 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGeminiCarbon,
                DisplayName = $"Gemini describe ({GeminiVisionDescriber.CarbonModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.CarbonModel} balanced vision at low thinking ($2/$10 per MTok until 2026-12-01).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGeminiHydrogen,
                DisplayName = $"Gemini describe ({GeminiVisionDescriber.HydrogenModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.HydrogenModel} fast Gemini 4 vision at low thinking ($1.50/$7.50 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGeminiArgon,
                DisplayName = $"Gemini describe ({GeminiVisionDescriber.ArgonModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.ArgonModel} high-reasoning vision at low thinking.",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyDescribeGrok,
                DisplayName = $"Grok describe ({GrokVisionDescriber.DefaultModel})",
                ContactSheetProvider = "xAI API",
                Kind = UiTargetKind.Describe,
                SchedulerLane = UiTargetScheduler.LaneXaiApi,
                ProviderAccountKey = AccountXai,
                Detail = $"xAI {GrokVisionDescriber.DefaultModel} vision via api.x.ai ($2/$6 per MTok).",
                InputImageRole = "describe source (image \u2192 text)",
            },
            new()
            {
                Key = UiJobRunner.KeyLayoutMap,
                DisplayName = $"Layout map ({GeminiVisionDescriber.DefaultModel})",
                ContactSheetProvider = "Google",
                Kind = UiTargetKind.LayoutMap,
                SchedulerLane = UiTargetScheduler.LaneGoogle,
                ProviderAccountKey = AccountGoogle,
                Detail = $"Google {GeminiVisionDescriber.DefaultModel} labels image regions as a flat-color section map PNG with legend and summary.",
                InputImageRole = "layout-map source (image \u2192 labeled section map)",
            },
        ];

        public static readonly IReadOnlyList<UiStandardGeneratorGroup> StandardGroups =
        [
            new(
                "only-sota",
                "only SOTA",
                [
                    UiJobRunner.KeyGoogle,
                    UiJobRunner.KeyGpt26Sunburst,
                    UiJobRunner.KeyGpt26Flare,
                    UiJobRunner.KeyGpt25Sunburst,
                    UiJobRunner.KeyGpt25Flare,
                    UiJobRunner.KeyGrokWeb,
                    UiJobRunner.KeyGrokApiPro,
                    UiJobRunner.KeyIdeogramV46,
                    UiJobRunner.KeyIdeogramV45,
                    UiJobRunner.KeyIdeogram,
                    UiJobRunner.KeyBflFlux3,
                ]),
            new(
                "thinking-ones",
                "Thinking ones",
                [
                    UiJobRunner.KeyGpt2,
                    UiJobRunner.KeyGooglePro,
                    UiJobRunner.KeyGrokWeb,
                    UiJobRunner.KeyGrokApiPro,
                ]),
            new(
                "can-do-famous-people",
                "can do famous people",
                [
                    UiJobRunner.KeyGooglePro,
                    UiJobRunner.KeyGoogle,
                    UiJobRunner.KeyGrokWebChat,
                    UiJobRunner.KeyGrokApi,
                    UiJobRunner.KeyGrokApiPro,
                    UiJobRunner.KeyGrokWeb,
                    UiJobRunner.KeyGpt2,
                ]),
            new(
                "text-ok",
                "text ok",
                [
                    UiJobRunner.KeyGpt26Sunburst,
                    UiJobRunner.KeyGpt26Flare,
                    UiJobRunner.KeyGooglePro,
                    UiJobRunner.KeyGoogle,
                    UiJobRunner.KeyGpt2,
                    UiJobRunner.KeyGrokWeb,
                    UiJobRunner.KeyGrokApiPro,
                    UiJobRunner.KeyGrokApi,
                    UiJobRunner.KeyGrokWebChat,
                    UiJobRunner.KeyBflFlux3,
                    UiJobRunner.KeyBflFlux2Max,
                    UiJobRunner.KeyRecraftV41Vector,
                    UiJobRunner.KeyBflKontextMax,
                    UiJobRunner.KeyGpt1Mini,
                    UiJobRunner.KeyRecraftV4Pro,
                    UiJobRunner.KeyRecraftV4,
                    UiJobRunner.KeyRecraftV41Pro,
                    UiJobRunner.KeyRecraftV41Utility,
                    UiJobRunner.KeyRecraftV41Flash,
                    UiJobRunner.KeyRecraft,
                    UiJobRunner.KeyIdeogramV3,
                    UiJobRunner.KeyIdeogram,
                    UiJobRunner.KeyIdeogramV45,
                    UiJobRunner.KeyIdeogramV46,
                ]),
        ];

        private static readonly Dictionary<string, UiTargetDescriptor> ByKey = BuildByKey();
        private static readonly Dictionary<string, string[]> StandardGroupIdsByKey = BuildStandardGroupIdsByKey();

        public static IReadOnlyList<UiTargetDescriptor> UiConfigTargets { get; } =
            All.Where(d => d.InUiConfig).ToArray();

        public static string[] ImageGeneratorKeys { get; } =
            All.Where(d => d.Kind == UiTargetKind.Image).Select(d => d.Key).ToArray();

        public static string[] DescribeKeys { get; } =
            All.Where(d => d.Kind == UiTargetKind.Describe).Select(d => d.Key).ToArray();

        public static string[] ImageCapableKeys { get; } =
            All.Where(d => d.Kind == UiTargetKind.Image && d.ImageCapable).Select(d => d.Key).ToArray();

        public static string[] SketchCapableKeys { get; } =
            All.Where(d => d.Kind == UiTargetKind.Image && d.SketchCapable).Select(d => d.Key).ToArray();

        public static string[] MultiInputImageKeys { get; } =
            All.Where(d => d.Kind == UiTargetKind.Image && d.MaxInputImages > 1).Select(d => d.Key).ToArray();

        public static UiTargetDescriptor? Find(string? key)
            => !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key, out var d) ? d : null;

        public static UiTargetDescriptor GetRequired(string key)
            => Find(key) ?? throw new ArgumentException($"Unknown UI generator key '{key}'.", nameof(key));

        public static string[] StandardGroupIdsFor(string key)
            => StandardGroupIdsByKey.TryGetValue(key, out var ids) ? ids : Array.Empty<string>();

        private static Dictionary<string, UiTargetDescriptor> BuildByKey()
        {
            var map = new Dictionary<string, UiTargetDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in All)
            {
                if (!map.TryAdd(descriptor.Key, descriptor))
                {
                    throw new InvalidOperationException($"Duplicate UI target key '{descriptor.Key}' in UiTargetCatalog.");
                }
            }
            return map;
        }

        private static Dictionary<string, string[]> BuildStandardGroupIdsByKey()
        {
            var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in StandardGroups)
            {
                foreach (var key in group.GeneratorKeys)
                {
                    if (!lists.TryGetValue(key, out var groupIds))
                    {
                        groupIds = new List<string>();
                        lists[key] = groupIds;
                    }
                    groupIds.Add(group.Id);
                }
            }
            return lists.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
