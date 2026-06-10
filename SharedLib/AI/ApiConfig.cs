using SharedLib.Config;

namespace SharedLib.AI
{
    public static class ApiConfig
    {
        public static string HuggingFaceToken
        {
            get
            {
                string token = EnvLoader.Get("HF_TOKEN", "");
                return string.IsNullOrWhiteSpace(token)
                    ? EnvLoader.Get("HUGGINGFACE_API_TOKEN", "")
                    : token;
            }
        }
        public static string HuggingFaceImageModel => EnvLoader.Get("HF_IMAGE_MODEL", "stabilityai/stable-diffusion-xl-base-1.0");

        public static string RemoveBgApiKey => EnvLoader.Get("REMOVE_BG_API_KEY", "");
        public const string RemoveBgUrl = "https://api.remove.bg/v1.0/removebg";
        public const string HuggingFaceImageGenerationUrl = "https://router.huggingface.co/nscale/v1/images/generations";

        // ── Nhan dien net ve tay -> line art qua mot Hugging Face Space (Gradio 4, vd sketch2lineart) ──
        // HF_SKETCH_SPACE: URL goc cua Space (de trong = Space mac dinh ben duoi).
        // HF_SKETCH_API:   api_name cua Space (sketch2lineart = /predict).
        public const string DefaultSketchSpace = "https://tori29umai-sketch2lineart.hf.space";
        public static string SketchSpaceBaseUrl => EnvLoader.Get("HF_SKETCH_SPACE", DefaultSketchSpace).TrimEnd('/');
        public static string SketchApiName
        {
            get
            {
                string api = EnvLoader.Get("HF_SKETCH_API", "/predict");
                if (string.IsNullOrWhiteSpace(api)) api = "/predict";
                return api.StartsWith("/") ? api : "/" + api;
            }
        }
        // Prompt mo ta (tuy chon). De trong cung duoc — Space tu them tien to "monochrome, lineart, white background...".
        public static string SketchDefaultPrompt => EnvLoader.Get("HF_SKETCH_PROMPT", "");
        public static string SketchNegativePrompt => EnvLoader.Get("HF_SKETCH_NEGATIVE",
            "lowres, error, extra digit, fewer digits, cropped, worst quality, low quality, normal quality, jpeg artifacts, blurry");
        // controlnet_scale: do bam sat net ve goc (0.5 - 1.25). Thap (~0.6) = AI tu do VE LAI cho dep;
        // cao (~1.0+) = bam sat/trace net ve tho. Mac dinh 0.6 de "ve lai cho dep" thay vi chi lam sach net.
        public static double SketchLineartFidelity
        {
            get
            {
                string raw = EnvLoader.Get("HF_SKETCH_FIDELITY", "");
                return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.6;
            }
        }

        public static bool IsSketchToImageConfigured()
            => !string.IsNullOrWhiteSpace(SketchSpaceBaseUrl);

        public const int TextToImageWidth = 512;
        public const int TextToImageHeight = 512;
        public const int TextToImageSteps = 30;

        public static bool IsHuggingFaceConfigured()
            => !string.IsNullOrWhiteSpace(HuggingFaceToken);

        public static bool IsRemoveBgConfigured()
            => !string.IsNullOrWhiteSpace(RemoveBgApiKey);
    }
}
