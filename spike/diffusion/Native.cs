using System.Runtime.InteropServices;

namespace Revelei.Spike.Diffusion;

// stable-diffusion.cpp's C API as of release master-1024-77b72a2 (include/stable-diffusion.h).
// Every struct is laid out field for field, bools as bytes and strings as UTF-8 pointers, so the
// sequential layout matches the C one. The header changes from build to build, which is itself
// one of the spike's findings: these structs are only valid for that one release.

[StructLayout(LayoutKind.Sequential)]
internal struct SdImage
{
    public uint Width;
    public uint Height;
    public uint Channel;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdCtxParams
{
    public nint ModelPath, ClipLPath, ClipGPath, ClipVisionPath, T5xxlPath, LlmPath, LlmVisionPath,
        DiffusionModelPath, HighNoiseDiffusionModelPath, UncondDiffusionModelPath, EmbeddingsConnectorsPath,
        VaePath, AudioVaePath, AudioEncoderPath, TaesdPath, ControlNetPath, IpAdapterPath, MotionModulePath;
    public nint Embeddings;
    public uint EmbeddingCount;
    public nint PhotoMakerPath, PulidWeightsPath, TensorTypeRules;
    public int NThreads, Wtype, RngType, SamplerRngType, Prediction, LoraApplyMode;
    public byte EnableMmap, FlashAttn, DiffusionFlashAttn, TaePreviewOnly, DiffusionConvDirect, VaeConvDirect, ForceSdxlVaeConvScale;
    public int VaeFormat;
    public nint MaxVram;
    public byte DisablePrefetch, EagerLoad;
    public nint Backend, ParamsBackend, SplitMode;
    public byte AutoFit;
    public nint RpcServers, ModelArgs;
    public byte DisableSegmentedCompute, BatchedCfg;
    public float LinearScale, AttnScale;
    public nint Tokenizer;
    public byte SageAttn;
    public int ConditioningCacheSize;
    public nint FlashAttnMode, ConvDirectMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdSlgParams
{
    public nint Layers;
    public nuint LayerCount;
    public float LayerStart, LayerEnd, Scale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdGuidanceParams
{
    public float TxtCfg, ImgCfg, DistilledGuidance;
    public SdSlgParams Slg;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdSampleParams
{
    public SdGuidanceParams Guidance;
    public int Scheduler, SampleMethod, SampleSteps;
    public float Eta;
    public int ShiftedTimestep;
    public nint CustomSigmas;
    public int CustomSigmasCount;
    public float FlowShift;
    public nint ExtraSampleArgs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdPmParams
{
    public nint IdImages;
    public int IdImagesCount;
    public nint IdEmbedPath;
    public float StyleStrength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdPulidParams
{
    public nint IdEmbeddingPath;
    public float IdWeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdTilingParams
{
    public byte Enabled, TemporalTiling;
    public int TileSizeW, TileSizeH;
    public float TargetOverlap, RelSizeW, RelSizeH;
    public nint ExtraTilingArgs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdCacheParams
{
    public int Mode;
    public float ReuseThreshold, StartPercent, EndPercent, ErrorDecayRate;
    public byte UseRelativeThreshold, ResetErrorOnCompute;
    public int FnComputeBlocks, BnComputeBlocks;
    public float ResidualDiffThreshold;
    public int MaxWarmupSteps, MaxCachedSteps, MaxContinuousCachedSteps, TaylorseerNDerivatives, TaylorseerSkipInterval;
    public nint ScmMask;
    public byte ScmPolicyDynamic;
    public float SpectrumW;
    public int SpectrumM;
    public float SpectrumLam;
    public int SpectrumWindowSize;
    public float SpectrumFlexWindow;
    public int SpectrumWarmupSteps;
    public float SpectrumStopPercent;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdHiresParams
{
    public byte Enabled;
    public int Upscaler;
    public nint ModelPath;
    public float Scale;
    public int TargetWidth, TargetHeight, Steps;
    public float DenoisingStrength;
    public int UpscaleTileSize;
    public nint CustomSigmas;
    public int CustomSigmasCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdImgGenParams
{
    public nint Loras;
    public uint LoraCount;
    public nint Prompt, NegativePrompt;
    public int ClipSkip;
    public SdImage InitImage;
    public nint RefImages;
    public int RefImagesCount;
    public nint RefImageArgs;
    public SdImage MaskImage;
    public int Width, Height;
    public SdSampleParams SampleParams;
    public float Strength;
    public long Seed;
    public int BatchCount;
    public SdImage ControlImage;
    public float ControlStrength;
    public SdImage IpAdapterImage;
    public float IpAdapterStrength;
    public SdPmParams PmParams;
    public SdPulidParams PulidParams;
    public SdTilingParams VaeTilingParams;
    public SdCacheParams Cache;
    public SdHiresParams Hires;
    public int QwenImageLayers;
    public byte CircularX, CircularY;
    public nint ImagePreprocessRules;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdImageResult
{
    public SdImage Image;
    public nint GenerationInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdImageGenerationResult
{
    public nint Items;
    public int Count;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ProgressCallback(int step, int steps, float time, nint data);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LogCallback(int level, nint text, nint data);

internal static partial class Native
{
    private const string Lib = "stable-diffusion.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sd_ctx_params_init(ref SdCtxParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint sd_ctx_params_to_str(ref SdCtxParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint new_sd_ctx(ref SdCtxParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_sd_ctx(nint ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sd_img_gen_params_init(ref SdImgGenParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint sd_img_gen_params_to_str(ref SdImgGenParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool generate_image(nint ctx, ref SdImgGenParams p, out nint result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_sd_image_generation_result(nint result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sd_cancel_generation(nint ctx, int mode);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sd_set_progress_callback(ProgressCallback cb, nint data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sd_set_log_callback(LogCallback cb, nint data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint sd_get_system_info();

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetDllDirectory(string path);
}
