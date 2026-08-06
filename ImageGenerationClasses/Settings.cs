using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MultiImageClient
{
    
    /// <summary>
    ///  Note: this file is for general confiruation for any use of the system.  Don't put things like "preferredImageOutputType" gif/png/etc here
    /// because those are things that might vary over time.  This is only for global true stuff that'll be set one time and kinda done from that point.
    /// If you have specific stuff like layout type horizontal/vertical etc, that should just be inlined in the way we create the generator at runtime
    /// with edits in the program.cs file which is used by the person making the images.
    /// </summary>
    public class Settings
    {
        public string GoogleCloudProjectId { get; set; }
        /// ffs google
        public string GoogleServiceAccountKeyPath { get; set; }

        /// aka vertex
        public string GoogleCloudApiKey { get; set; }
        public string IdeogramApiKey { get; set; }
        public string OpenAIApiKey { get; set; }
        public string BFLApiKey { get; set; }
        public string KreaApiKey { get; set; }
        public string AnthropicApiKey { get; set; }
        public string RecraftApiKey { get; set; }

        /// xAI (Grok) API key, format "xai-...". Required only when a Grok
        /// image generator is active. Obtain one at https://console.x.ai/.
        public string XAIGrokApiKey { get; set; }

        /// Netscape cookies.txt or raw Cookie-header export for grok.com.
        /// Required for --grok-web consumer-session generators.
        public string GrokWebCookiePath { get; set; } = "";

        /// Public 48-byte grok-site-verification value, base64-encoded, for
        /// browser-free x-statsig-id signing. Capture both signing values
        /// together with --grok-web-capture-statsig after a frontend deploy.
        public string GrokWebStatsigVerificationKey { get; set; } = "";

        /// Public frontend animation key paired with
        /// GrokWebStatsigVerificationKey. The pair enables browser-free
        /// grok-web image editing; stale or incomplete pairs fail closed.
        public string GrokWebStatsigAnimationKey { get; set; } = "";

        /// Optional Chrome/Chromium binary used for grok-web video generation's
        /// browser-backed app-chat POST. Blank = Playwright's bundled Chromium.
        public string GrokWebBrowserExecutablePath { get; set; } = "";

        /// Show the grok-web video browser window. Normally false; useful when
        /// diagnosing an expired session or provider-side page change.
        public bool GrokWebBrowserHeaded { get; set; }

        /// Timeout for grok-web's browser-backed video request.
        public int GrokWebVideoTimeoutSeconds { get; set; } = 900;

        /// How long to poll for a video after app-chat accepted the request but
        /// returned no URL. Grok sometimes silently drops moderated jobs.
        public int GrokWebVideoPollTimeoutSeconds { get; set; } = 180;

        /// Optional Netscape cookies.txt or raw Cookie-header export for
        /// meta.ai, injected into the --meta-web browser session as an
        /// alternative to the one-time interactive login. Needs the HttpOnly
        /// `datr` + session cookie (`ecto_1_sess`; `abra_sess` optional) —
        /// ideally the complete meta.ai jar.
        public string MetaWebCookiePath { get; set; } = "";

        /// --meta-web drives the real meta.ai web app with Playwright (Meta
        /// moved generation to an integrity-checked WebSocket transport
        /// ("DGW"), so plain HTTP GraphQL calls no longer work). This
        /// persistent Chromium profile keeps the logged-in session between
        /// runs; log in once with --meta-web-headed. Blank =
        /// ~/.config/multi-image-client/meta-ai-profile.
        public string MetaWebBrowserProfilePath { get; set; } = "";

        /// Optional path to an existing Chrome/Chromium binary for --meta-web.
        /// Blank = Playwright's own Chromium (one-time --playwright-install).
        public string MetaWebBrowserExecutablePath { get; set; } = "";

        /// Show the meta-web browser window (also forced by --meta-web-headed).
        public bool MetaWebHeaded { get; set; }

        /// Per-prompt generation timeout for --meta-web.
        public int MetaWebTimeoutSeconds { get; set; } = 480;

        /// Opt-in meta-web diagnostics: events.jsonl + failure screenshots
        /// under saves/<day>/meta-web-capture/. Never contains cookie values.
        public bool MetaWebCaptureSessions { get; set; }
        public string XAIBaseUrl { get; set; } = "";
        public string GoogleGeminiApiKey { get; set; }
        public string GoogleCloudLocation { get; set; }
        /// List of prompt-source text files. Every listed file is read and the
        /// lines are pooled together. All listed files must exist at run time;
        /// missing files are a hard error. Prefer this field over the legacy
        /// LoadPromptsFrom single-file setting.
        public List<string> PromptFiles { get; set; } = new List<string>();

        /// Legacy single-file prompt source. If set, appended to PromptFiles.
        /// Kept for backward compatibility with older settings.json files.
        public string LoadPromptsFrom { get; set; }
        public bool EnableLogging { get; set; }
        public string LogFilePath { get; set; }
        public bool SaveJsonLog { get; set; }
        /// save just image.jpg or image.png etc.

        public string ImageDownloadBaseFolder { get; set; }

        /// Local SQLite audit archive for generation attempts, provider calls,
        /// nested request/response fields, errors, and saved-asset paths.
        /// Blank uses ImageDownloadBaseFolder/generation-history.sqlite3.
        public string GenerationArchiveDbPath { get; set; } = "";

        /// Structured generation archiving is on by default. Image/video bytes
        /// remain on disk; the database stores metadata, hashes, and paths.
        public bool EnableGenerationArchive { get; set; } = true;

        /// Optional web-UI access control file for shared deployments. Blank
        /// (the default) means the UI runs open, as before, for local use.
        /// When set, the file must exist and parse; it holds
        /// { "enabled": true, "secret": "...", "accounts": [{"username","password"}] }.
        /// Login issues a long-lived HMAC cookie derived from the account's
        /// password + the shared secret, so removing an account or changing
        /// its password (or the secret) immediately invalidates the cookies
        /// it produced. The server never writes this file.
        public string UiAuthFilePath { get; set; } = "";

        /// Maximum number of memory-heavy UI job finalizations (contact-sheet
        /// rendering and cleanup) allowed at once. Endpoint requests from
        /// different jobs are scheduled independently by target and do not
        /// hold this permit.
        public int UiMaxConcurrentJobs { get; set; } = 4;

        /// Maximum number of generators executing across all active UI jobs.
        /// This is the aggregate cap above the per-target lane limits.
        public int UiMaxConcurrentGenerators { get; set; } = 20;

        /// Maximum accepted UI jobs that may be running or waiting in target
        /// queues. New submissions receive HTTP 503 when this bound is full.
        public int UiMaxPendingJobs { get; set; } = 64;

        /// Optional per-provider/account concurrency overrides. Known lane
        /// names: openai, xai-api, grok-web-ws, grok-web-browser, meta-web,
        /// google, bfl, krea, ideogram, recraft, comfyui. Missing entries use the
        /// scheduler's conservative defaults.
        public Dictionary<string, int> UiTargetConcurrency { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// Optional free-space floor for accepting new UI jobs, in bytes.
        /// Zero disables the guard for local use. Shared deployments should
        /// reserve several GiB so generated assets cannot crowd out colocated
        /// services. This is checked before uploads are read or jobs created.
        public long UiMinimumFreeDiskBytes { get; set; }

        /// unused yet we always do RIGHT
        public string AnnotationSide { get; set; } = "bottom";

        /// Optional flat-folder mirror. If non-empty, every saved raw,
        /// annotated, and combined image is also copied (best-effort) into
        /// this single folder so you don't have to navigate date folders to
        /// grab the latest batch. Leave blank to disable; missing or
        /// unreachable paths are logged and skipped, never fatal.
        public string FlatImageMirrorPath { get; set; } = "";

        /// Optional "prompts I typed by hand" capture file. When the user
        /// types a free-form prompt at the interactive batch loop (anything
        /// other than y/n/q), the typed text is also appended as a single
        /// line to this file — handy for growing a personal prompt corpus
        /// over time. Leave blank to disable; the existing JSON prompt_log
        /// remains the machine-readable history regardless. Embedded
        /// newlines in the typed prompt are collapsed to spaces so the file
        /// is always one-prompt-per-line.
        public string TypedPromptsAppendFile { get; set; } = "";

        /// Backblaze B2 image hosting for the --ui web app (see
        /// docs/b2-image-hosting-plan.md). When enabled, finished UI result and
        /// grid images upload to the public B2 bucket and job events emit B2
        /// capability URLs (opaque random keys) instead of local /api URLs.
        /// Upload failure = retry then visible hard-fail; the local copy is
        /// never served as a substitute (owner decision 2026-08-05).
        public bool EnableB2ImageHosting { get; set; }

        /// Application key pair restricted to the one bucket (keyID +
        /// applicationKey from the B2 dashboard; the secret is shown once).
        public string B2KeyId { get; set; } = "";
        public string B2ApplicationKey { get; set; } = "";

        /// From the bucket details page. BucketId feeds b2_get_upload_url;
        /// BucketName is the public URL path segment.
        public string B2BucketId { get; set; } = "";
        public string B2BucketName { get; set; } = "";

        /// Public URL base, e.g. "https://f004.backblazeb2.com/file/mic-images-xxxx"
        /// (no trailing slash). Owner-pinned because persisted event URLs must
        /// stay correct forever; the client hard-errors at authorize time if
        /// this disagrees with the account's live downloadUrl/bucket.
        public string B2DownloadBaseUrl { get; set; } = "";

        /// true (default): local raw images stay on disk — the dev-install
        /// mode where disk remains a full second archive. false (production):
        /// after a job's finalization completes, local raws whose B2 uploads
        /// were checksum-verified are deleted; B2 is then the source of truth
        /// for raw bytes on that install. Thumbs and job metadata always stay
        /// local. false requires EnableB2ImageHosting.
        public bool B2KeepLocalRawImages { get; set; } = true;

        /// Master switch for the local ComfyUI generators (local-klein, local-zimage).
        /// Default false: they are treated as NOT INSTALLED — shown disabled in the
        /// web UI and skipped by showcase/batch runs, regardless of the ComfyUI*
        /// settings below. Nothing installs them automatically; flip this to true
        /// only after setting up ComfyUI + models + workflows yourself.
        public bool EnableLocalGenerators { get; set; } = false;

        /// Local ComfyUI server used by local/open-weight image generators.
        /// Example: http://127.0.0.1:8188
        public string ComfyUIBaseUrl { get; set; } = "";

        /// API-format ComfyUI workflow JSON for local/open-weight image runs.
        /// Put {{PROMPT}} in the positive prompt field before exporting/saving it.
        public string ComfyUIWorkflowPath { get; set; } = "";

        /// Short label for filenames/metadata when the local ComfyUI workflow is active.
        public string ComfyUIWorkflowName { get; set; } = "";

        /// Optional model filenames to inject into placeholder-bearing ComfyUI API workflows.
        /// These are ComfyUI model names, not filesystem paths, e.g. the filename visible in
        /// a loader node's dropdown under models/diffusion_models, models/vae, etc.
        public string ComfyUIDiffusionModelName { get; set; } = "";
        public string ComfyUICheckpointName { get; set; } = "";
        public string ComfyUIVaeName { get; set; } = "";
        public string ComfyUITextEncoderName { get; set; } = "";
        public string ComfyUITextEncoder2Name { get; set; } = "";
        public string ComfyUILoraName { get; set; } = "";
        public double ComfyUILoraModelStrength { get; set; } = 0.8;
        public double ComfyUILoraClipStrength { get; set; } = 0.8;

        /// Legacy API-format ComfyUI workflow JSON for FLUX.2 Klein 4B. Kept so
        /// existing settings.json files continue to work; prefer ComfyUIWorkflowPath.
        public string ComfyUIFlux2KleinWorkflowPath { get; set; } = "";

        /// API-format ComfyUI workflow JSON for local Z-Image or Z-Image-Turbo runs.
        /// Put {{PROMPT}} in the positive prompt field before exporting/saving it.
        public string ComfyUIZImageWorkflowPath { get; set; } = "";

        /// Short label for filenames/metadata when the local Z-Image workflow is active.
        public string ComfyUIZImageWorkflowName { get; set; } = "";

        /// Optional Z-Image-specific model filenames for placeholder-bearing workflows.
        public string ComfyUIZImageDiffusionModelName { get; set; } = "";
        public string ComfyUIZImageCheckpointName { get; set; } = "";
        public string ComfyUIZImageVaeName { get; set; } = "";
        public string ComfyUIZImageTextEncoderName { get; set; } = "";
        public string ComfyUIZImageTextEncoder2Name { get; set; } = "";
        public string ComfyUIZImageLoraName { get; set; } = "";
        public double ComfyUIZImageLoraModelStrength { get; set; } = 0.8;
        public double ComfyUIZImageLoraClipStrength { get; set; } = 0.8;

        public int ComfyUIPollIntervalMs { get; set; } = 1000;
        public int ComfyUITimeoutSeconds { get; set; } = 900;

        public static Settings LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Settings file not found: {filePath}");
            }

            string json = File.ReadAllText(filePath);
            var settings = JsonConvert.DeserializeObject<Settings>(json);
            settings.NormalizePaths();
            settings.Validate();
            Logger.Initialize(settings.LogFilePath);
            Logger.Log("Current settings:");
            Logger.Log($"Image Download Base:\t{settings.ImageDownloadBaseFolder}");
            Logger.Log($"Save JSON Log:\t\t{settings.SaveJsonLog}");
            Logger.Log($"Enable Logging:\t\t{settings.EnableLogging}");
            Logger.Log($"Annotation Side:\t{settings.AnnotationSide}");
            if (!string.IsNullOrWhiteSpace(settings.FlatImageMirrorPath))
            {
                Logger.Log($"Flat Mirror Path:\t{settings.FlatImageMirrorPath}");
            }
            if (!string.IsNullOrWhiteSpace(settings.TypedPromptsAppendFile))
            {
                Logger.Log($"Typed Prompts File:\t{settings.TypedPromptsAppendFile}");
            }

            return settings;
        }

        /// Expands a leading "~" to the user's home directory and resolves any
        /// environment variables (e.g. "$HOME", "%USERPROFILE%"). .NET does NOT
        /// expand "~" the way a shell does, so a settings.json value like
        /// "~/proj/saves" would otherwise be created as a literal "~" folder in
        /// the current working directory. Returns the input unchanged when it's
        /// null/empty or has nothing to expand.
        public static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());

            if (expanded == "~" || expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                {
                    home = Environment.GetEnvironmentVariable("HOME") ?? "";
                }
                expanded = expanded.Length <= 1
                    ? home
                    : Path.Combine(home, expanded.Substring(2));
            }

            return expanded;
        }

        /// Applies ExpandPath to every path-bearing setting so the rest of the
        /// app only ever sees fully-resolved absolute paths.
        public void NormalizePaths()
        {
            ImageDownloadBaseFolder = ExpandPath(ImageDownloadBaseFolder);
            LogFilePath = ExpandPath(LogFilePath);
            GenerationArchiveDbPath = ExpandPath(GenerationArchiveDbPath);
            FlatImageMirrorPath = ExpandPath(FlatImageMirrorPath);
            TypedPromptsAppendFile = ExpandPath(TypedPromptsAppendFile);
            LoadPromptsFrom = ExpandPath(LoadPromptsFrom);
            GoogleServiceAccountKeyPath = ExpandPath(GoogleServiceAccountKeyPath);
            ComfyUIWorkflowPath = ExpandPath(ComfyUIWorkflowPath);
            ComfyUIFlux2KleinWorkflowPath = ExpandPath(ComfyUIFlux2KleinWorkflowPath);
            ComfyUIZImageWorkflowPath = ExpandPath(ComfyUIZImageWorkflowPath);
            GrokWebCookiePath = ExpandPath(GrokWebCookiePath);
            GrokWebBrowserExecutablePath = ExpandPath(GrokWebBrowserExecutablePath);
            MetaWebCookiePath = ExpandPath(MetaWebCookiePath);
            MetaWebBrowserProfilePath = ExpandPath(MetaWebBrowserProfilePath);
            MetaWebBrowserExecutablePath = ExpandPath(MetaWebBrowserExecutablePath);

            if (PromptFiles != null)
            {
                for (int i = 0; i < PromptFiles.Count; i++)
                {
                    PromptFiles[i] = ExpandPath(PromptFiles[i]);
                }
            }
        }

        /// Only validates things that EVERY run needs: the log file path and the
        /// image download folder. Per-generator requirements (Google Cloud fields,
        /// individual API keys, etc.) live in the generator that needs them.
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(LogFilePath))
            {
                throw new InvalidOperationException(
                    "settings.json: LogFilePath is required. Set it to a writable file path, e.g. \"C:\\\\proj\\\\multiImageClient\\\\ideogram.log\".");
            }
            var logDirectory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDirectory))
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"settings.json: LogFilePath='{LogFilePath}' — could not create directory '{logDirectory}': {ex.Message}. Fix the path in settings.json.");
                }
            }

            if (string.IsNullOrWhiteSpace(ImageDownloadBaseFolder))
            {
                throw new InvalidOperationException(
                    "settings.json: ImageDownloadBaseFolder is required. Set it to a writable folder, e.g. \"C:\\\\proj\\\\multiImageClient\\\\saves\".");
            }
            try
            {
                Directory.CreateDirectory(ImageDownloadBaseFolder);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"settings.json: ImageDownloadBaseFolder='{ImageDownloadBaseFolder}' — could not create: {ex.Message}. Fix the path in settings.json.");
            }

            if (EnableB2ImageHosting)
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(B2KeyId)) missing.Add(nameof(B2KeyId));
                if (string.IsNullOrWhiteSpace(B2ApplicationKey)) missing.Add(nameof(B2ApplicationKey));
                if (string.IsNullOrWhiteSpace(B2BucketId)) missing.Add(nameof(B2BucketId));
                if (string.IsNullOrWhiteSpace(B2BucketName)) missing.Add(nameof(B2BucketName));
                if (string.IsNullOrWhiteSpace(B2DownloadBaseUrl)) missing.Add(nameof(B2DownloadBaseUrl));
                if (missing.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"settings.json: EnableB2ImageHosting is true but required B2 settings are blank: {string.Join(", ", missing)}. Fill them all in or set EnableB2ImageHosting to false. See docs/b2-image-hosting-plan.md.");
                }
                if (!B2DownloadBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || B2DownloadBaseUrl.EndsWith("/"))
                {
                    throw new InvalidOperationException(
                        $"settings.json: B2DownloadBaseUrl='{B2DownloadBaseUrl}' must be an https URL without a trailing slash, e.g. \"https://f004.backblazeb2.com/file/my-bucket\".");
                }
            }
            else if (!B2KeepLocalRawImages)
            {
                throw new InvalidOperationException(
                    "settings.json: B2KeepLocalRawImages=false requires EnableB2ImageHosting=true — evicting local raw images without an upload destination would discard data.");
            }
        }
    }
}
