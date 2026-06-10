using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharedLib.AI;
using SharedLib.Logging;

namespace DrawingClient.AI
{
    public static class HuggingFaceClient
    {
        // XU LY DA LUONG: gioi han toi da 2 request AI dong thoi de tranh qua tai API/UI.
        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(2, 2);

        public static async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (!ApiConfig.IsHuggingFaceConfigured())
                throw new InvalidOperationException("Chua cau hinh HF_TOKEN trong .env.");

            string model = ApiConfig.HuggingFaceImageModel;
            var body = new
            {
                response_format = "b64_json",
                prompt = prompt,
                model = model
            };

            string json = JsonConvert.SerializeObject(body);
            // XU LY BAT DONG BO: cho slot request bang SemaphoreSlim async, khong block thread UI.
            await RequestGate.WaitAsync(cancellationToken);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
                using var request = new HttpRequestMessage(HttpMethod.Post, ApiConfig.HuggingFaceImageGenerationUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiConfig.HuggingFaceToken);

                // KET NOI DU LIEU/NETWORK: goi HTTP API Hugging Face bang SendAsync.
                using var response = await client.SendAsync(request, cancellationToken);
                byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                if (!response.IsSuccessStatusCode)
                {
                    string responseText = Encoding.UTF8.GetString(responseBytes);
                    string shortBody = responseText.Length > 600 ? responseText.Substring(0, 600) : responseText;
                    Logger.Error("HuggingFace", $"HTTP {(int)response.StatusCode}: {shortBody}");
                    throw BuildHuggingFaceException(response.StatusCode, responseText);
                }

                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return responseBytes;

                string text = Encoding.UTF8.GetString(responseBytes);
                string base64 = FindImageData(JObject.Parse(text));
                if (string.IsNullOrWhiteSpace(base64))
                    throw new InvalidOperationException("Hugging Face khong tra ve du lieu anh.");

                return Convert.FromBase64String(base64);
            }
            catch (JsonException ex)
            {
                Logger.Exception("HuggingFace", ex);
                throw new InvalidOperationException("Phan hoi Hugging Face khong dung dinh dang anh mong doi.", ex);
            }
            catch (FormatException ex)
            {
                Logger.Exception("HuggingFace", ex);
                throw new InvalidOperationException("Du lieu anh tu Hugging Face khong hop le.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Hugging Face phan hoi qua lau. Hay thu lai sau.", ex);
            }
            finally
            {
                RequestGate.Release();
            }
        }

        private static Exception BuildHuggingFaceException(System.Net.HttpStatusCode statusCode, string responseText)
        {
            int code = (int)statusCode;
            string message = TryReadErrorMessage(responseText);

            if (code == 401 || code == 403)
                return new InvalidOperationException("Hugging Face token khong hop le, het han, hoac khong co quyen dung model nay.");

            if (code == 402)
                return new InvalidOperationException("Hugging Face account/project khong du credit hoac chua bat billing cho Inference Providers.");

            if (code == 429)
                return new InvalidOperationException("Hugging Face dang bi gioi han toc do hoac het quota. Hay doi mot lat roi thu lai.");

            if (code >= 500)
                return new InvalidOperationException("Hugging Face dang loi tam thoi. Hay thu lai sau.");

            if (!string.IsNullOrWhiteSpace(message))
                return new InvalidOperationException("Hugging Face API tra ve loi: " + message);

            return new InvalidOperationException("Hugging Face API tra ve loi. Kiem tra token, model, billing va quota.");
        }

        private static string FindImageData(JToken root)
        {
            var data = root?["data"] as JArray;
            if (data != null)
            {
                foreach (JToken item in data)
                {
                    string base64 = item["b64_json"]?.ToString() ?? item["base64"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(base64))
                        return base64;
                }
            }

            return root?["b64_json"]?.ToString() ?? root?["image"]?.ToString();
        }

        private static string TryReadErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
                return string.Empty;

            try
            {
                JObject root = JObject.Parse(responseText);
                return root["error"]?["message"]?.ToString()
                    ?? root["error"]?.ToString()
                    ?? root["message"]?.ToString()
                    ?? string.Empty;
            }
            catch
            {
                return responseText.Trim();
            }
        }
    }
}
