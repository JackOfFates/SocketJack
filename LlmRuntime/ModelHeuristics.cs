namespace LlmRuntime;

public static class ModelHeuristics
{
    private static readonly string[] OrderedModelTags = ["chat", "instruct", "code", "embedding", "vision", "image", "audio", "video", "tool-use"];

    public static string DetectQuantType(string fileName)
    {
        string upper = fileName.ToUpperInvariant();
        string[] patterns =
        [
            "IQ4_XS", "IQ4_NL", "IQ3_XXS", "IQ3_XS", "IQ3_S", "IQ3_M",
            "IQ2_XXS", "IQ2_XS", "IQ2_S", "IQ2_M", "IQ1_S", "IQ1_M",
            "Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q5_0", "Q5_1",
            "Q4_K_M", "Q4_K_S", "Q4_0", "Q4_1",
            "Q3_K_M", "Q3_K_S", "Q3_K_L", "Q3_K_XS",
            "Q2_K", "Q2_K_S", "F16", "F32", "BF16"
        ];

        foreach (string pattern in patterns)
        {
            if (upper.Contains(pattern, StringComparison.Ordinal))
                return pattern;
        }

        return "";
    }

    public static double? EstimateBitsPerWeight(string? quantizationName)
    {
        if (string.IsNullOrWhiteSpace(quantizationName))
            return null;

        string quant = quantizationName.ToUpperInvariant();
        if (quant.Contains("F32", StringComparison.Ordinal))
            return 32;
        if (quant.Contains("BF16", StringComparison.Ordinal) || quant.Contains("F16", StringComparison.Ordinal))
            return 16;
        if (quant.StartsWith("Q8", StringComparison.Ordinal))
            return 8;
        if (quant.StartsWith("Q6", StringComparison.Ordinal))
            return 6;
        if (quant.StartsWith("Q5", StringComparison.Ordinal))
            return 5;
        if (quant.StartsWith("Q4", StringComparison.Ordinal) || quant.StartsWith("IQ4", StringComparison.Ordinal))
            return 4;
        if (quant.StartsWith("Q3", StringComparison.Ordinal) || quant.StartsWith("IQ3", StringComparison.Ordinal))
            return 3;
        if (quant.StartsWith("Q2", StringComparison.Ordinal) || quant.StartsWith("IQ2", StringComparison.Ordinal))
            return 2;
        if (quant.StartsWith("IQ1", StringComparison.Ordinal))
            return 1;
        return null;
    }

    public static string DetectModelType(GgufMetadataReader? metadata, string filePath)
    {
        string lower = Path.GetFileName(filePath).ToLowerInvariant();
        string full = (filePath ?? "").Replace('\\', '/').ToLowerInvariant();

        if (metadata != null)
        {
            string? generalType = metadata.GetString("general.type");
            if (!string.IsNullOrWhiteSpace(generalType))
            {
                string gt = generalType.Trim().ToLowerInvariant();
                if (gt.Contains("embed", StringComparison.Ordinal))
                    return "embedding";
                if (LooksLikeVideoModel(gt))
                    return "video";
                if (LooksLikeAudioModel(gt))
                    return "audio";
                if (LooksLikeImageGenerationModel(gt))
                    return "image";
                if (gt.Contains("image", StringComparison.Ordinal) || gt.Contains("vision", StringComparison.Ordinal))
                    return "vlm";
            }

            string? arch = metadata.GetString("general.architecture");
            if (!string.IsNullOrWhiteSpace(arch))
            {
                string al = arch.ToLowerInvariant();
                if (LooksLikeVideoModel(al))
                    return "video";
                if (LooksLikeAudioModel(al))
                    return "audio";
                if (LooksLikeImageGenerationModel(al))
                    return "image";
                if (al.Contains("clip", StringComparison.Ordinal) || al.Contains("vit", StringComparison.Ordinal) ||
                    al.Contains("mmproj", StringComparison.Ordinal) || al.Contains("llava", StringComparison.Ordinal) ||
                    al.Contains("minicpm", StringComparison.Ordinal) || al.Contains("moondream", StringComparison.Ordinal))
                    return "vlm";
                if (al.Contains("bert", StringComparison.Ordinal) || al.Contains("nomic", StringComparison.Ordinal))
                    return "embedding";
            }
        }

        if (lower.Contains("embed", StringComparison.Ordinal) || lower.Contains("bge-", StringComparison.Ordinal) ||
            lower.Contains("gte-", StringComparison.Ordinal) || lower.Contains("e5-", StringComparison.Ordinal) ||
            lower.Contains("nomic-embed", StringComparison.Ordinal))
            return "embedding";

        if (LooksLikeVideoModel(full))
            return "video";

        if (LooksLikeAudioModel(full))
            return "audio";

        if (LooksLikeImageGenerationModel(full))
            return "image";

        if (lower.Contains("llava", StringComparison.Ordinal) || lower.Contains("mmproj", StringComparison.Ordinal) ||
            lower.Contains("clip", StringComparison.Ordinal) || lower.Contains("vision", StringComparison.Ordinal))
            return "vlm";

        return "llm";
    }

    public static IReadOnlyList<string> DetectModelTags(string fileNameOrPath, GgufMetadataReader? metadata = null)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string lower = Path.GetFileNameWithoutExtension(fileNameOrPath ?? "").ToLowerInvariant();
        string full = (fileNameOrPath ?? "").ToLowerInvariant();

        string type = DetectModelType(metadata, fileNameOrPath ?? "");
        if (type == "embedding")
            tags.Add("embedding");
        if (type == "vlm")
            tags.Add("vision");
        if (type == "image")
            tags.Add("image");
        if (type == "audio")
            tags.Add("audio");
        if (type == "video")
            tags.Add("video");

        AddTagIfAny(tags, "instruct", lower, "instruct", "instruction", "it-", "-it", "chatml");
        AddTagIfAny(tags, "chat", lower, "chat", "assistant", "dialog", "conversation", "sft");
        AddTagIfAny(tags, "code", lower, "code", "coder", "codestral", "deepseek-coder", "starcoder", "qwen2.5-coder", "qwen3-coder", "devstral", "sqlcoder");
        AddTagIfAny(tags, "embedding", lower, "embed", "embedding", "bge-", "gte-", "e5-", "nomic-embed", "minilm");
        AddTagIfAny(tags, "vision", lower, "vision", "visual", "vlm", "llava", "minicpm-v", "moondream", "pixtral", "qwen-vl", "mmproj");
        AddTagIfAny(tags, "image", lower, "text-to-image", "image-to-image", "img2img", "i2i", "controlnet", "inpaint", "t2i", "stable-diffusion", "sdxl", "flux", "qwen-image", "imagegen", "diffusion", "unet");
        AddTagIfAny(tags, "audio", lower, "audio", "audio-to-audio", "voice-conversion", "voice-clone", "speech", "whisper", "wav2vec", "bark", "musicgen");
        AddTagIfAny(tags, "video", lower, "text-to-video", "image-to-video", "video-to-video", "img2vid", "vid2vid", "t2v", "i2v", "v2v", "wan2", "wan-", "hunyuanvideo", "ltx-video", "ltxv", "mochi", "motifv", "highnoise", "lownoise");
        AddTagIfAny(tags, "tool-use", lower, "tool", "function", "agent", "hermes", "firefunction");

        if (metadata != null)
        {
            AddMetadataTags(tags, metadata, "general.name");
            AddMetadataTags(tags, metadata, "general.description");
            AddMetadataTags(tags, metadata, "general.basename");
            AddMetadataTags(tags, metadata, "general.architecture");
        }

        if (!tags.Contains("embedding") && !tags.Contains("vision") && !tags.Contains("image") && !tags.Contains("audio") && !tags.Contains("video") &&
            (tags.Contains("instruct") || tags.Contains("tool-use") || full.Contains("/chat/", StringComparison.Ordinal)))
            tags.Add("chat");

        return OrderedModelTags.Where(tags.Contains).ToArray();
    }

    public static string? FormatParamCount(ulong? count)
    {
        if (!count.HasValue || count.Value == 0)
            return null;

        ulong value = count.Value;
        if (value >= 1_000_000_000_000)
            return $"{value / 1e12:F1}T";
        if (value >= 1_000_000_000)
            return $"{value / 1e9:F1}B";
        if (value >= 1_000_000)
            return $"{value / 1e6:F0}M";
        if (value >= 1_000)
            return $"{value / 1e3:F0}K";
        return value.ToString();
    }

    private static bool LooksLikeVideoModel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("text-to-video", StringComparison.Ordinal) ||
               text.Contains("image-to-video", StringComparison.Ordinal) ||
               text.Contains("video-to-video", StringComparison.Ordinal) ||
               text.Contains("video", StringComparison.Ordinal) ||
               text.Contains("img2vid", StringComparison.Ordinal) ||
               text.Contains("vid2vid", StringComparison.Ordinal) ||
               text.Contains("t2v", StringComparison.Ordinal) ||
               text.Contains("i2v", StringComparison.Ordinal) ||
               text.Contains("v2v", StringComparison.Ordinal) ||
               text.Contains("wan2", StringComparison.Ordinal) ||
               text.Contains("wan-", StringComparison.Ordinal) ||
               text.Contains("hunyuanvideo", StringComparison.Ordinal) ||
               text.Contains("ltx-video", StringComparison.Ordinal) ||
               text.Contains("ltxv", StringComparison.Ordinal) ||
               text.Contains("mochi", StringComparison.Ordinal) ||
               text.Contains("motifv", StringComparison.Ordinal) ||
               text.Contains("highnoise", StringComparison.Ordinal) ||
               text.Contains("lownoise", StringComparison.Ordinal);
    }

    private static bool LooksLikeImageGenerationModel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("text-to-image", StringComparison.Ordinal) ||
               text.Contains("image-to-image", StringComparison.Ordinal) ||
               text.Contains("img2img", StringComparison.Ordinal) ||
               text.Contains("controlnet", StringComparison.Ordinal) ||
               text.Contains("inpaint", StringComparison.Ordinal) ||
               text.Contains("t2i", StringComparison.Ordinal) ||
               text.Contains("stable-diffusion", StringComparison.Ordinal) ||
               text.Contains("sdxl", StringComparison.Ordinal) ||
               text.Contains("flux", StringComparison.Ordinal) ||
               text.Contains("qwen-image", StringComparison.Ordinal) ||
               text.Contains("imagegen", StringComparison.Ordinal) ||
               text.Contains("diffusion", StringComparison.Ordinal) ||
               text.Contains("unet", StringComparison.Ordinal);
    }

    private static bool LooksLikeAudioModel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("audio", StringComparison.Ordinal) ||
               text.Contains("audio-to-audio", StringComparison.Ordinal) ||
               text.Contains("voice-conversion", StringComparison.Ordinal) ||
               text.Contains("voice-clone", StringComparison.Ordinal) ||
               text.Contains("speech", StringComparison.Ordinal) ||
               text.Contains("whisper", StringComparison.Ordinal) ||
               text.Contains("wav2vec", StringComparison.Ordinal) ||
               text.Contains("bark", StringComparison.Ordinal) ||
               text.Contains("musicgen", StringComparison.Ordinal);
    }

    private static void AddMetadataTags(HashSet<string> tags, GgufMetadataReader metadata, string key)
    {
        string value = metadata.GetString(key) ?? "";
        if (string.IsNullOrWhiteSpace(value))
            return;

        string lower = value.ToLowerInvariant();
        AddTagIfAny(tags, "instruct", lower, "instruct", "instruction", "chatml");
        AddTagIfAny(tags, "chat", lower, "chat", "assistant", "dialog", "conversation");
        AddTagIfAny(tags, "code", lower, "code", "coder", "programming", "software");
        AddTagIfAny(tags, "embedding", lower, "embed", "embedding", "retrieval");
        AddTagIfAny(tags, "vision", lower, "vision", "visual", "image", "multimodal", "clip", "llava");
        AddTagIfAny(tags, "image", lower, "text-to-image", "t2i", "stable-diffusion", "sdxl", "flux", "qwen-image", "imagegen", "diffusion", "unet");
        AddTagIfAny(tags, "audio", lower, "audio", "speech", "whisper", "wav2vec", "bark", "musicgen");
        AddTagIfAny(tags, "video", lower, "text-to-video", "image-to-video", "t2v", "i2v", "v2v", "wan2", "wan-", "hunyuanvideo", "ltx-video", "ltxv", "mochi", "motifv", "highnoise", "lownoise");
        AddTagIfAny(tags, "tool-use", lower, "tool", "function", "agent");
    }

    private static void AddTagIfAny(HashSet<string> tags, string tag, string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(tag);
                return;
            }
        }
    }
}
