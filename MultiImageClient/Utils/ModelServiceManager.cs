using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MultiImageClient
{
    // Manages local vision model services (InternVL Flask server and Ollama).
    // Checks if services are running and starts them if needed before using describe mode.
    public class ModelServiceManager
    {
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        
        public static async Task<bool> EnsureInternVLServiceIsRunningAsync(string baseUrl = "http://127.0.0.1:11415")
        {
            if (await IsServiceHealthyAsync(baseUrl, "/health"))
            {
                Logger.Log("InternVL service is already running.");
                return true;
            }

            Logger.Log("InternVL service not detected. Starting Flask server...");
            return await StartInternVLServiceAsync(baseUrl);
        }

		public static async Task<bool> EnsureOllamaServiceIsRunningAsync(string baseUrl = "http://127.0.0.1:11434", string modelName = "qwen2-vl:latest")
        {
            if (!await IsOllamaRunningAsync(baseUrl))
            {
                Logger.Log("Ollama service not detected. Starting Ollama...");
                var started = await StartOllamaServiceAsync(baseUrl);
                if (!started)
                {
                    return false;
                }
            }
            else
            {
                Logger.Log("Ollama service is already running.");
            }

            return await EnsureOllamaModelIsLoadedAsync(baseUrl, modelName);
        }

        private static async Task<bool> IsServiceHealthyAsync(string baseUrl, string healthEndpoint)
        {
            try
            {
                var response = await httpClient.GetAsync($"{baseUrl}{healthEndpoint}");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> IsOllamaRunningAsync(string baseUrl)
        {
            try
            {
                var response = await httpClient.GetAsync($"{baseUrl}/api/version");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

		private static async Task<bool> EnsureOllamaModelIsLoadedAsync(string baseUrl, string modelName, int timeoutSeconds = 120)
        {
            try
            {
				Logger.Log($"Checking if Ollama model '{modelName}' is loaded (chat)...");

				var requestBody = System.Text.Json.JsonSerializer.Serialize(new
				{
					model = modelName,
					messages = new[]
					{
						new { role = "user", content = "ping" }
					},
					stream = false
				});

				var content = new System.Net.Http.StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

				httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
				var response = await httpClient.PostAsync($"{baseUrl}/api/chat", content);
				httpClient.Timeout = TimeSpan.FromSeconds(5);

                if (response.IsSuccessStatusCode)
                {
                    Logger.Log($"Ollama model '{modelName}' is loaded and ready.");
                    return true;
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Logger.Log($"WARNING: Ollama model '{modelName}' check failed: {response.StatusCode} - {errorContent}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"WARNING: Could not verify Ollama model '{modelName}': {ex.Message}");
                return false;
            }
        }

		private static async Task<bool> StartInternVLServiceAsync(string baseUrl, int maxWaitSeconds = 600)
        {
            try
            {
                var flaskScriptPath = @"D:\proj\multiImageClient\do_flask_intern.py";
                if (!File.Exists(flaskScriptPath))
                {
                    Logger.Log($"ERROR: Could not find Flask script at: {flaskScriptPath}");
                    return false;
                }

                var pythonPath = @"d:\ai\qwen_project\venv\Scripts\python.exe";
                if (!File.Exists(pythonPath))
                {
                    Logger.Log($"ERROR: Could not find Python executable at: {pythonPath}");
                    return false;
                }

				Logger.Log($"Starting InternVL Flask server from: {flaskScriptPath}");
				Logger.Log($"Using Python: {pythonPath}");

				var pythonExecutable = Path.Combine(Path.GetDirectoryName(pythonPath) ?? string.Empty, "pythonw.exe");
				if (!File.Exists(pythonExecutable))
				{
					pythonExecutable = pythonPath; // fallback to console python if pythonw not present
				}

                var startInfo = new ProcessStartInfo
                {
					FileName = pythonExecutable,
                    Arguments = $"\"{flaskScriptPath}\"",
					UseShellExecute = false,
					CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(flaskScriptPath)
                };

                Process.Start(startInfo);

                Logger.Log("Waiting for InternVL service to become ready...");
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < maxWaitSeconds)
                {
                    await Task.Delay(2000);
                    if (await IsServiceHealthyAsync(baseUrl, "/health"))
                    {
                        Logger.Log($"InternVL service is ready! (took {stopwatch.Elapsed.TotalSeconds:F1}s)");
                        return true;
                    }
                    Logger.Log($"Still waiting... ({stopwatch.Elapsed.TotalSeconds:F0}s elapsed)");
                }

                Logger.Log($"WARNING: InternVL service did not become ready within {maxWaitSeconds} seconds.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"ERROR starting InternVL service: {ex.Message}");
                return false;
            }
        }

		private static async Task<bool> StartOllamaServiceAsync(string baseUrl, int maxWaitSeconds = 120)
        {
            try
            {
				Logger.Log("Attempting to start Ollama service...");

				Uri uri;
				if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri))
				{
					Logger.Log($"WARNING: Invalid Ollama base URL: {baseUrl}");
					return false;
				}

				var hostPort = ($"{uri.Host}:{uri.Port}");
				var startInfo = new ProcessStartInfo
				{
					FileName = "ollama",
					Arguments = "serve",
					UseShellExecute = false,
					CreateNoWindow = true
				};
				startInfo.EnvironmentVariables["OLLAMA_HOST"] = hostPort;

				var process = Process.Start(startInfo);
                if (process == null)
                {
                    Logger.Log("WARNING: Could not start Ollama process. It may already be running.");
                }
				else
				{
					Logger.Log($"Started 'ollama serve' with OLLAMA_HOST={hostPort}");
				}

                Logger.Log("Waiting for Ollama service to become ready...");
                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed.TotalSeconds < maxWaitSeconds)
                {
                    await Task.Delay(2000);
                    if (await IsOllamaRunningAsync(baseUrl))
                    {
                        Logger.Log($"Ollama service is ready! (took {stopwatch.Elapsed.TotalSeconds:F1}s)");
                        return true;
                    }
                    Logger.Log($"  Still waiting... ({stopwatch.Elapsed.TotalSeconds:F0}s elapsed)");
                }

                Logger.Log($"WARNING: Ollama service did not become ready within {maxWaitSeconds} seconds.");
                Logger.Log("If Ollama is already running elsewhere, you can ignore this warning.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"ERROR starting Ollama service: {ex.Message}");
                Logger.Log("If Ollama is already running, you can ignore this error.");
                return false;
            }
        }

    }
}

