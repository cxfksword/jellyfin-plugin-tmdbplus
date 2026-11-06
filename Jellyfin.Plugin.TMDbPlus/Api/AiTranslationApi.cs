using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TMDbPlus.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TMDbPlus.Api
{
    /// <summary>
    /// Client for OpenAI-compatible /chat/completions API to translate character names.
    /// </summary>
    public class AiTranslationApi
    {
        private readonly ILogger<AiTranslationApi> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiTranslationApi"/> class.
        /// </summary>
        public AiTranslationApi(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
        {
            _logger = loggerFactory.CreateLogger<AiTranslationApi>();
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Translates a list of English character names to Chinese using AI.
        /// Returns a dictionary mapping English name -> Chinese name.
        /// Names without a translation are omitted from the result.
        /// </summary>
        public async Task<Dictionary<string, string>> TranslateCharacterNamesAsync(
            string title,
            int? year,
            IEnumerable<string> characterNames,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            if (!config.EnableAiTranslateCharacter)
            {
                return result;
            }

            if (string.IsNullOrWhiteSpace(config.AiBaseUrl))
            {
                _logger.LogWarning("[TMDbPlus] AI translation enabled but AiBaseUrl is not configured.");
                return result;
            }

            var names = characterNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
            {
                return result;
            }

            var prompt = string.IsNullOrWhiteSpace(config.AiTranslatePrompt)
                ? PluginConfiguration.DEFAULT_AI_TRANSLATE_PROMPT
                : config.AiTranslatePrompt;

            prompt = prompt
                .Replace("{{title}}", title ?? string.Empty, StringComparison.Ordinal)
                .Replace("{{year}}", year?.ToString() ?? string.Empty, StringComparison.Ordinal);

            var userContent = string.Join("\n", names);

            var requestBody = new
            {
                model = string.IsNullOrWhiteSpace(config.AiModel) ? "gpt-3.5-turbo" : config.AiModel,
                messages = new[]
                {
                    new { role = "system", content = prompt },
                    new { role = "user", content = userContent },
                },
            };

            var json = JsonSerializer.Serialize(requestBody);
            var baseUrl = config.AiBaseUrl.TrimEnd('/');
            var requestUrl = $"{baseUrl}/chat/completions";

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(60);

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                if (!string.IsNullOrWhiteSpace(config.AiApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AiApiKey);
                }

                _logger.LogInformation("[TMDbPlus] Requesting AI translation for {Count} character names of [{Title}]", names.Count, title);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[TMDbPlus] AI translation request failed: {Status} {Body}", response.StatusCode, responseBody);
                    return result;
                }

                result = ParseTranslationResponse(responseBody);
                _logger.LogInformation("[TMDbPlus] AI translation returned {Count} character name mappings", result.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDbPlus] AI translation request error");
            }

            return result;
        }

        private Dictionary<string, string> ParseTranslationResponse(string responseBody)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    return result;
                }

                foreach (var line in content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var idx = line.IndexOf(':', StringComparison.Ordinal);
                    if (idx <= 0)
                    {
                        continue;
                    }

                    var english = line[..idx].Trim();
                    var chinese = line[(idx + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(english) && !string.IsNullOrWhiteSpace(chinese))
                    {
                        result[english] = chinese;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TMDbPlus] Failed to parse AI translation response");
            }

            return result;
        }
    }
}
