using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Voice for the assistant: speech-to-text for the microphone button and text-to-speech for
    /// reading answers aloud. Both go to the same OpenAI account as the chat, using the
    /// "openai" HTTP client.
    /// </summary>
    public sealed class OpenAISpeechService
    {
        /// <summary>OpenAI rejects longer speech input.</summary>
        public const int MaxSpeechChars = 4096;

        private readonly AssistantOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;

        public OpenAISpeechService(IOptions<AssistantOptions> options, IHttpClientFactory httpClientFactory)
        {
            _options = options.Value;
            _httpClientFactory = httpClientFactory;
        }

        public bool IsConfigured => _options.IsConfigured && _options.VoiceEnabled;

        public async Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(audio);
            // Bare media type only: browsers add "; codecs=opus", which OpenAI does not need.
            file.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) && type.MediaType is not null
                ? new MediaTypeHeaderValue(type.MediaType)
                : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);
            form.Add(new StringContent(_options.TranscriptionModel), "model");

            using var request = NewRequest("audio/transcriptions", form);
            using var response = await _httpClientFactory.CreateClient(OpenAIChatAssistantService.HttpClientName).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, body);
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("text", out var text) ? (text.GetString() ?? "").Trim() : "";
        }

        /// <summary>Returns the spoken answer as MP3.</summary>
        public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
        {
            var payload = new JsonObject
            {
                ["model"] = _options.SpeechModel,
                ["voice"] = _options.SpeechVoice,
                ["input"] = text.Length > MaxSpeechChars ? text[..MaxSpeechChars] : text,
                ["response_format"] = "mp3"
            };

            using var request = NewRequest("audio/speech",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
            using var response = await _httpClientFactory.CreateClient(OpenAIChatAssistantService.HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        /// <summary>
        /// Wraps an OpenAI error as "403: message". OpenAI echoes part of a wrong key back
        /// ("Incorrect API key provided: sk-ab***yz"), so anything key-shaped is masked because
        /// admins see this text in the widget.
        /// </summary>
        private static HttpRequestException Failure(HttpStatusCode status, string body)
        {
            var message = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error)
                    && error.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.String)
                {
                    message = m.GetString() ?? body;
                }
            }
            catch (JsonException)
            {
                // Not JSON (e.g. a proxy error page); keep the raw text.
            }

            message = Regex.Replace(message, @"sk-[A-Za-z0-9_\-\*]+", "sk-***");
            if (message.Length > 300) message = message[..300];
            return new HttpRequestException($"{(int)status}: {message}", null, status);
        }

        private HttpRequestMessage NewRequest(string path, HttpContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/{path}") { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            return request;
        }
    }
}
