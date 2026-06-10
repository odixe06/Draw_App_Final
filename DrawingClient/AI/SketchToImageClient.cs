using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharedLib.AI;
using SixLabors.ImageSharp;

namespace DrawingClient.AI
{
    /// <summary>
    /// Nhan dien net ve tay -> line art: gui vung net ve toi mot Hugging Face Space (Gradio 4) chay
    /// ControlNet-lineart (mac dinh tori29umai/sketch2lineart) -> nhan ve mot anh line art sach.
    /// Env: HF_SKETCH_SPACE (URL Space, de trong = Space mac dinh), HF_SKETCH_API (api_name, mac dinh /predict),
    /// HF_SKETCH_PROMPT (mo ta, tuy chon), HF_SKETCH_NEGATIVE, HF_SKETCH_FIDELITY, HF_TOKEN (quota ZeroGPU).
    /// Space tra anh .webp -> client transcode sang PNG (ImageSharp) vi GDI+ .NET Framework khong doc WebP.
    /// Doi Space chi can sua .env; neu Space dung Gradio 5 thi sua duong dan API trong code (/call -> /gradio_api/call).
    /// </summary>
    public static class SketchToImageClient
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task<byte[]> GenerateFromSketchAsync(byte[] sketchPng, string prompt, CancellationToken ct = default)
        {
            string baseUrl = ApiConfig.SketchSpaceBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Chua cau hinh HF_SKETCH_SPACE trong .env (URL Hugging Face Space ControlNet-lineart).");
            if (sketchPng == null || sketchPng.Length == 0)
                throw new InvalidOperationException("Khong co du lieu net ve de gui.");

            string apiName = ApiConfig.SketchApiName;
            await Gate.WaitAsync(ct);
            try
            {
                // ZeroGPU co the cold-start + xep hang -> cho lau hon text-to-image thuong.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
                if (!string.IsNullOrWhiteSpace(ApiConfig.HuggingFaceToken))
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiConfig.HuggingFaceToken);

                // 1) Upload sketch -> Space tra ve duong dan file tren server.
                JObject fileData = await UploadSketchAsync(http, baseUrl, sketchPng, ct);

                // 2) Goi api_name (Gradio 4 /call) -> event_id -> doc SSE ket qua.
                //    sketch2lineart nhan [anh, prompt, negative_prompt, controlnet_scale (slider 0.5-1.25)].
                var dataArr = new JArray { fileData, prompt ?? "", ApiConfig.SketchNegativePrompt, ApiConfig.SketchLineartFidelity };
                string callUrl = baseUrl + "/call" + apiName;
                var callBody = new JObject { ["data"] = dataArr };

                // ZeroGPU hay tra ve "event: error" tam thoi (GPU ban / het luot / qua tai) -> thu lai vai lan.
                const int maxAttempts = 3;
                string lastTrouble = "Space khong tra ve anh.";
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    string eventId;
                    using (var callResp = await http.PostAsync(callUrl,
                        new StringContent(callBody.ToString(), Encoding.UTF8, "application/json"), ct))
                    {
                        string callText = await callResp.Content.ReadAsStringAsync();
                        if (!callResp.IsSuccessStatusCode)
                            throw new InvalidOperationException($"Gradio call loi HTTP {(int)callResp.StatusCode}: {Trunc(callText)}");

                        eventId = JObject.Parse(callText)["event_id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(eventId))
                            throw new InvalidOperationException("Space khong tra ve event_id. Kiem tra lai HF_SKETCH_API (api_name).");
                    }

                    // 3) Doc stream SSE: phan biet event ket qua (complete) voi event loi.
                    string streamText = await http.GetStringAsync(callUrl + "/" + eventId);
                    SseResult sse = ParseSse(streamText);

                    if (sse.Data != null)
                    {
                        byte[] raw = await ExtractImageAsync(http, baseUrl, sse.Data, ct);
                        // Space tra .webp; GDI+ .NET Framework khong doc WebP -> transcode sang PNG.
                        return EnsureGdiReadablePng(raw);
                    }

                    lastTrouble = sse.HadError
                        ? "Space bao loi xu ly (ZeroGPU GPU dang ban / het luot / qua tai)." +
                          (string.IsNullOrEmpty(sse.ErrorText) ? "" : " Chi tiet: " + Trunc(sse.ErrorText))
                        : "Space khong tra ve anh. Phan hoi: " + Trunc(streamText);

                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }

                throw new InvalidOperationException(lastTrouble +
                    $" (da thu {maxAttempts} lan). Hay thu lai sau it phut; dat HF_TOKEN co quota ZeroGPU, " +
                    "hoac doi Space khac qua HF_SKETCH_SPACE (vi du duplicate Space ve tai khoan cua ban de co quota rieng).");
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Hugging Face Space phan hoi qua lau. Hay thu lai sau.", ex);
            }
            finally
            {
                Gate.Release();
            }
        }

        // Endpoint tagger cua Space sketch2lineart (Gradio 4). Doi Space khac co the khong co -> AnalyzeSketchAsync tra rong.
        private const string TaggerApiName = "/process_prompt_analysis";

        /// <summary>
        /// "Nhan dien" noi dung vung net ve: goi tagger cua Space (process_prompt_analysis) -> tra ve chuoi tag mo ta
        /// (vi du "flower, plant"). Tagger la TUY CHON: neu Space khong co endpoint nay hoac loi -> tra ve chuoi rong
        /// de nguoi dung tu go mo ta. Khong nem loi ra ngoai.
        /// </summary>
        public static async Task<string> AnalyzeSketchAsync(byte[] sketchPng, CancellationToken ct = default)
        {
            string baseUrl = ApiConfig.SketchSpaceBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl) || sketchPng == null || sketchPng.Length == 0)
                return "";

            await Gate.WaitAsync(ct);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                if (!string.IsNullOrWhiteSpace(ApiConfig.HuggingFaceToken))
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + ApiConfig.HuggingFaceToken);

                JObject fileData = await UploadSketchAsync(http, baseUrl, sketchPng, ct);
                string callUrl = baseUrl + "/call" + TaggerApiName;
                var callBody = new JObject { ["data"] = new JArray { fileData } };
                using (var callResp = await http.PostAsync(callUrl,
                    new StringContent(callBody.ToString(), Encoding.UTF8, "application/json"), ct))
                {
                    if (!callResp.IsSuccessStatusCode) return "";
                    string eventId = JObject.Parse(await callResp.Content.ReadAsStringAsync())["event_id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(eventId)) return "";

                    string streamText = await http.GetStringAsync(callUrl + "/" + eventId);
                    return ExtractText(ParseSseData(streamText));
                }
            }
            catch
            {
                return ""; // tagger tuy chon -> bo qua loi, nguoi dung tu go mo ta
            }
            finally
            {
                Gate.Release();
            }
        }

        private static string ExtractText(JToken data)
        {
            JToken node = data is JArray arr && arr.Count > 0 ? arr[0] : data;
            if (node is JArray inner)
            {
                var sb = new StringBuilder();
                foreach (JToken t in inner)
                {
                    string s = t?.ToString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(s);
                }
                return sb.ToString();
            }
            return node?.ToString() ?? "";
        }

        private static async Task<JObject> UploadSketchAsync(HttpClient http, string baseUrl, byte[] png, CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(png);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "files", "sketch.png");

            using var resp = await http.PostAsync(baseUrl + "/upload", form, ct);
            string text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Upload net ve len Space loi HTTP {(int)resp.StatusCode}: {Trunc(text)}");

            var paths = JsonConvert.DeserializeObject<List<string>>(text);
            if (paths == null || paths.Count == 0)
                throw new InvalidOperationException("Space khong nhan duoc file net ve.");

            return new JObject
            {
                ["path"] = paths[0],
                ["orig_name"] = "sketch.png",
                ["meta"] = new JObject { ["_type"] = "gradio.FileData" }
            };
        }

        private struct SseResult { public JToken Data; public string ErrorText; public bool HadError; }

        // Phan tich SSE cua Gradio 4: theo doi dong "event:" de phan biet ket qua (generating/complete)
        // voi loi (error). Tra ve data token cuoi cung KHONG thuoc event error; ghi nhan neu gap event error.
        private static SseResult ParseSse(string sse)
        {
            var result = new SseResult();
            if (string.IsNullOrWhiteSpace(sse)) return result;
            string currentEvent = null;
            foreach (string rawLine in sse.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("event:")) { currentEvent = line.Substring("event:".Length).Trim(); continue; }
                if (!line.StartsWith("data:")) continue;
                string json = line.Substring("data:".Length).Trim();
                if (currentEvent == "error")
                {
                    result.HadError = true;
                    if (json.Length > 0 && json != "null") result.ErrorText = json;
                    continue;
                }
                if (json.Length == 0 || json == "null") continue;
                try { result.Data = JToken.Parse(json); } catch { /* bo qua dong khong phai JSON */ }
            }
            return result;
        }

        private static JToken ParseSseData(string sse) => ParseSse(sse).Data;

        private static async Task<byte[]> ExtractImageAsync(HttpClient http, string baseUrl, JToken data, CancellationToken ct)
        {
            JToken node = data is JArray arr && arr.Count > 0 ? arr[0] : data;

            // Output co the la: { url }, { path }, { image: {...} }, hoac chuoi base64/url.
            if (node is JObject obj)
            {
                if (obj["image"] != null) node = obj["image"];
            }

            if (node is JObject fobj)
            {
                // Gradio 4: tai file qua /file=<path>. Uu tien "path" vi truong "url" co the kem root_path sai (vd /c/file=).
                string path = fobj["path"]?.ToString();
                string url = fobj["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(url))
                    return await DownloadFileAsync(http, baseUrl, path, url, ct);
            }

            string s = node?.ToString() ?? "";
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return await http.GetByteArrayAsync(AbsoluteUrl(baseUrl, s));
            if (s.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                int comma = s.IndexOf(',');
                if (comma > 0) return Convert.FromBase64String(s.Substring(comma + 1));
            }
            if (s.Length > 100)
            {
                try { return Convert.FromBase64String(s); } catch { /* khong phai base64 */ }
            }

            throw new InvalidOperationException("Khong doc duoc anh ket qua tu Space. Kiem tra dinh dang dau ra cua Space.");
        }

        // Dam bao bytes ket qua doc duoc bang GDI+ (Image.FromStream). GDI+ tren .NET Framework
        // KHONG giai ma WebP (Space hay tra .webp), va neu Space loi (HTML/JSON quota/cold-start) thi
        // bytes khong phai anh -> GDI+ nem "Parameter is not valid" mu mit. Xu ly:
        //  - PNG/JPEG/GIF/BMP/TIFF: GDI+ doc duoc -> giu nguyen.
        //  - Con lai (WebP, AVIF...): transcode sang PNG bang ImageSharp (managed).
        //  - Neu ImageSharp cung khong giai ma duoc -> day KHONG phai anh -> nem loi ro rang kem
        //    trich doan phan hoi (de biet Space tra ve gi: dang khoi dong / qua tai / doi API).
        private static byte[] EnsureGdiReadablePng(byte[] data)
        {
            if (data == null || data.Length < 12)
                throw new InvalidOperationException("Space tra ve du lieu rong/khong hop le.");

            if (IsPng(data) || IsJpeg(data) || IsGif(data) || IsBmp(data) || IsTiff(data))
                return data; // GDI+ doc truc tiep duoc

            try
            {
                using (var image = SixLabors.ImageSharp.Image.Load(data))
                using (var ms = new MemoryStream())
                {
                    image.SaveAsPng(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Space khong tra ve anh hop le (co the ZeroGPU dang khoi dong/qua tai, het quota, hoac da doi API). " +
                    "Phan hoi: " + SafeTextPreview(data), ex);
            }
        }

        private static bool IsPng(byte[] d) => d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
        private static bool IsJpeg(byte[] d) => d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;
        private static bool IsGif(byte[] d) => d.Length >= 3 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F';
        private static bool IsBmp(byte[] d) => d.Length >= 2 && d[0] == (byte)'B' && d[1] == (byte)'M';
        private static bool IsTiff(byte[] d) => d.Length >= 4 && ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00)
                                                              || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A));

        // Giai ma ~200 byte dau thanh text (loc ky tu dieu khien) de hien thi loi khi phan hoi khong phai anh.
        private static string SafeTextPreview(byte[] data)
        {
            int n = Math.Min(200, data.Length);
            var sb = new StringBuilder(n);
            foreach (char c in Encoding.UTF8.GetString(data, 0, n))
                sb.Append(c < 32 && c != '\n' && c != '\t' ? ' ' : c);
            string s = sb.ToString().Trim();
            return s.Length == 0 ? $"({data.Length} byte nhi phan khong doc duoc)" : s;
        }

        // Tai file ket qua thu lan luot cac route: /file=<path> (Gradio 4), /gradio_api/file=<path> (Gradio 5),
        // roi truong url (co the kem root_path). Tra ve bytes cua route dau tien thanh cong.
        private static async Task<byte[]> DownloadFileAsync(HttpClient http, string baseUrl, string path, string url, CancellationToken ct)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(baseUrl + "/file=" + path);
                candidates.Add(baseUrl + "/gradio_api/file=" + path);
            }
            if (!string.IsNullOrWhiteSpace(url))
                candidates.Add(AbsoluteUrl(baseUrl, url));

            Exception lastEx = null;
            foreach (string c in candidates)
            {
                try
                {
                    using (var resp = await http.GetAsync(c, ct))
                    {
                        if (resp.IsSuccessStatusCode)
                            return await resp.Content.ReadAsByteArrayAsync();
                        lastEx = new InvalidOperationException($"Tai file ket qua loi HTTP {(int)resp.StatusCode} tu {c}");
                    }
                }
                catch (Exception ex) { lastEx = ex; }
            }
            throw lastEx ?? new InvalidOperationException("Khong co duong dan file ket qua de tai.");
        }

        private static string AbsoluteUrl(string baseUrl, string url)
            => url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : baseUrl + (url.StartsWith("/") ? "" : "/") + url;

        private static string Trunc(string s)
            => string.IsNullOrEmpty(s) ? "" : (s.Length > 400 ? s.Substring(0, 400) : s);
    }
}
