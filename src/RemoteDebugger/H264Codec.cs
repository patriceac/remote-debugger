using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using RemoteDebugger.Core;

namespace RemoteDebugger;

internal static unsafe class FfmpegRuntime
{
    private static readonly object Sync = new();
    private static bool initialized;
    private static Exception? failure;

    public static void EnsureAvailable()
    {
        lock (Sync)
        {
            if (initialized) return;
            if (failure != null) throw new InvalidOperationException("H.264 codec runtime is unavailable.", failure);
            try
            {
                DynamicallyLoadedBindings.LibrariesPath = FindLibrariesPath();
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                DynamicallyLoadedBindings.Initialize();
                if (ffmpeg.avcodec_find_encoder_by_name("libx264") == null || ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) == null)
                    throw new InvalidOperationException("The bundled FFmpeg runtime does not expose libx264 and H.264.");
                initialized = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                throw new InvalidOperationException("H.264 codec runtime is unavailable.", ex);
            }
        }
    }

    public static bool IsAvailable()
    {
        try { EnsureAvailable(); return true; }
        catch { return false; }
    }

    private static string FindLibrariesPath()
    {
        var candidates = new List<string>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeSearch)
            candidates.AddRange(nativeSearch.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        candidates.Add(AppContext.BaseDirectory);
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "avcodec-*.dll").Any())
                    return candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return AppContext.BaseDirectory;
    }
}

internal sealed unsafe class H264Encoder : IDisposable
{
    private readonly int sourceWidth, sourceHeight, width, height, fps, quality;
    private AVCodecContext* context;
    private AVFrame* yuv;
    private AVPacket* packet;
    private SwsContext* scaler;
    private bool disposed;
    private readonly MemoryStream encoded = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> unavailable = new();
    internal string Name { get; private set; } = "libx264";
    internal string? FallbackReason { get; private set; }

    public H264Encoder(int sourceWidth, int sourceHeight, int width, int height, int fps, int quality, bool preferHardware = true)
    {
        FfmpegRuntime.EnsureAvailable();
        this.sourceWidth = sourceWidth; this.sourceHeight = sourceHeight;
        this.width = width; this.height = height; this.fps = fps; this.quality = quality;
        if (preferHardware)
            foreach (string candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf" })
            {
                if (unavailable.ContainsKey(candidate)) continue;
                try { Initialize(candidate); return; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { unavailable.TryAdd(candidate, 0); ReleaseCodec(); }
            }
        try { Initialize("libx264"); }
        catch { ReleaseCodec(); encoded.Dispose(); throw; }
    }

    private void Initialize(string name)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null) throw new InvalidOperationException(name + " encoder not found.");
        Name = name;
        bool hardware = name != "libx264";
        var format = hardware ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
        context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null) throw new InvalidOperationException("Could not allocate the H.264 encoder.");
        context->width = width; context->height = height;
        context->time_base = new AVRational { num = 1, den = fps };
        context->framerate = new AVRational { num = fps, den = 1 };
        context->pix_fmt = format; context->max_b_frames = 0; context->gop_size = Math.Max(1, fps * 2);
        context->bit_rate = Math.Clamp((long)width * height * fps / 8, 2_000_000, 20_000_000);
        string qp = QualityToCrf(quality).ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (name)
        {
            case "h264_nvenc":
                SetOption("preset", "p2"); SetOption("tune", "ull"); SetOption("zerolatency", "1");
                SetOption("delay", "0"); SetOption("rc", "constqp"); SetOption("qp", qp); break;
            case "h264_qsv":
                SetOption("preset", "veryfast"); SetOption("async_depth", "1"); SetOption("look_ahead", "0"); break;
            case "h264_amf":
                SetOption("usage", "ultralowlatency"); SetOption("quality", "speed");
                SetOption("rc", "cqp"); SetOption("qp_i", qp); SetOption("qp_p", qp); break;
            case "h264_mf":
                SetOption("hw_encoding", "1"); SetOption("scenario", "display_remoting"); break;
            default:
                SetOption("preset", "veryfast"); SetOption("tune", "zerolatency"); SetOption("crf", qp); break;
        }
        Check(ffmpeg.avcodec_open2(context, codec, null), "Open " + name);
        yuv = ffmpeg.av_frame_alloc();
        if (yuv == null) throw new InvalidOperationException("Could not allocate the H.264 frame.");
        yuv->format = (int)format; yuv->width = width; yuv->height = height;
        Check(ffmpeg.av_frame_get_buffer(yuv, 32), "Allocate H.264 frame buffers");
        packet = ffmpeg.av_packet_alloc();
        if (packet == null) throw new InvalidOperationException("Could not allocate the H.264 packet.");
        scaler = ffmpeg.sws_getContext(sourceWidth, sourceHeight, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, format,
            (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (scaler == null) throw new InvalidOperationException("Could not allocate the H.264 pixel converter.");
    }

    public EncodedVideoFrame Encode(Bitmap bitmap, long sequence)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (bitmap.Width != sourceWidth || bitmap.Height != sourceHeight) throw new ArgumentException("The captured bitmap dimensions changed during the H.264 stream.", nameof(bitmap));
        var elapsed = Stopwatch.StartNew();
        try { var result = EncodeFrame(bitmap, sequence); return result with { EncodeMs = elapsed.Elapsed.TotalMilliseconds }; }
        catch (Exception ex) when (Name != "libx264" && ex is not OutOfMemoryException)
        {
            // A device lost or an encoder that buffers frames cannot stall interactive input.
            FallbackReason = Name + ": " + ex.Message; unavailable.TryAdd(Name, 0);
            ReleaseCodec(); Initialize("libx264");
            var result = EncodeFrame(bitmap, sequence); // New encoder emits SPS/PPS and an IDR frame.
            return result with { EncodeMs = elapsed.Elapsed.TotalMilliseconds };
        }
    }

    private EncodedVideoFrame EncodeFrame(Bitmap bitmap, long sequence)
    {
        var watch = Stopwatch.StartNew();
        Check(ffmpeg.av_frame_make_writable(yuv), "Prepare H.264 frame");
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte*[] source = [(byte*)data.Scan0, null, null, null];
            int[] sourceStride = [data.Stride, 0, 0, 0];
            byte*[] destination = [yuv->data[0], yuv->data[1], yuv->data[2], null, null, null, null, null];
            int[] destinationStride = [yuv->linesize[0], yuv->linesize[1], yuv->linesize[2], 0, 0, 0, 0, 0];
            Check(ffmpeg.sws_scale(scaler, source, sourceStride, 0, bitmap.Height, destination, destinationStride) == height ? 0 : -1, "Convert desktop pixels to H.264");
        }
        finally { bitmap.UnlockBits(data); }
        yuv->pts = sequence;
        Check(ffmpeg.avcodec_send_frame(context, yuv), "Encode H.264 frame");
        encoded.SetLength(0);
        while (true)
        {
            ffmpeg.av_packet_unref(packet);
            int result = ffmpeg.avcodec_receive_packet(context, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) break;
            Check(result, "Read H.264 packet");
            encoded.Write(new ReadOnlySpan<byte>(packet->data, packet->size));
        }
        if (encoded.Length == 0) throw new InvalidOperationException("H.264 encoder buffered the frame.");
        return new EncodedVideoFrame(encoded.ToArray(), watch.Elapsed.TotalMilliseconds);
    }

    private void SetOption(string name, string value) => Check(ffmpeg.av_opt_set(context->priv_data, name, value, 0), "Set H.264 option " + name);
    private static int QualityToCrf(int quality) => Math.Clamp(39 - (int)Math.Round(Math.Clamp(quality, 30, 95) * 0.18), 20, 34);
    private void ReleaseCodec()
    {
        var p = packet; ffmpeg.av_packet_free(&p); packet = null;
        var f = yuv; ffmpeg.av_frame_free(&f); yuv = null;
        var c = context; ffmpeg.avcodec_free_context(&c); context = null;
        ffmpeg.sws_freeContext(scaler); scaler = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; ReleaseCodec(); encoded.Dispose(); }
    private static void Check(int result, string operation)
    { if (result < 0) throw new InvalidOperationException($"{operation} failed ({result})."); }
}
internal sealed unsafe class H264Decoder : IDisposable
{
    private readonly BitmapPool? bitmapPool;
    private readonly AVCodecContext* context;
    private readonly AVPacket* packet;
    private readonly AVFrame* frame;
    private SwsContext* scaler;
    private int sourceWidth;
    private int sourceHeight;
    private AVPixelFormat sourceFormat;
    private bool disposed;

    public H264Decoder(BitmapPool? bitmapPool = null)
    {
        this.bitmapPool = bitmapPool;
        FfmpegRuntime.EnsureAvailable();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new InvalidOperationException("H.264 decoder not found.");
        context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null) throw new InvalidOperationException("Could not allocate the H.264 decoder.");
        Check(ffmpeg.avcodec_open2(context, codec, null), "Open H.264 decoder");
        packet = ffmpeg.av_packet_alloc();
        if (packet == null) throw new InvalidOperationException("Could not allocate the H.264 input packet.");
        frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new InvalidOperationException("Could not allocate the H.264 decoded frame.");
    }

    public Bitmap? Decode(byte[] data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ffmpeg.av_packet_unref(packet);
        Check(ffmpeg.av_new_packet(packet, data.Length), "Allocate H.264 input packet");
        Marshal.Copy(data, 0, (IntPtr)packet->data, data.Length);
        Check(ffmpeg.avcodec_send_packet(context, packet), "Send H.264 packet");
        while (true)
        {
            int result = ffmpeg.avcodec_receive_frame(context, frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return null;
            Check(result, "Read H.264 decoded frame");
            return ToBitmap(frame);
        }
    }

    private Bitmap ToBitmap(AVFrame* source)
    {
        int width = source->width, height = source->height; var format = (AVPixelFormat)source->format;
        if (scaler == null || width != sourceWidth || height != sourceHeight || format != sourceFormat)
        {
            if (scaler != null) ffmpeg.sws_freeContext(scaler);
            scaler = ffmpeg.sws_getContext(width, height, format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (scaler == null) throw new InvalidOperationException("Could not allocate the H.264 display converter.");
            sourceWidth = width; sourceHeight = height; sourceFormat = format;
        }

        var bitmap = bitmapPool?.Rent(width, height) ?? new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        bool converted = false;
        try
        {
            byte*[] sourceData = [source->data[0], source->data[1], source->data[2], source->data[3], null, null, null, null];
            int[] sourceStride = [source->linesize[0], source->linesize[1], source->linesize[2], source->linesize[3], 0, 0, 0, 0];
            byte*[] destinationData = [(byte*)data.Scan0, null, null, null];
            int[] destinationStride = [data.Stride, 0, 0, 0];
            Check(ffmpeg.sws_scale(scaler, sourceData, sourceStride, 0, height, destinationData, destinationStride) == height ? 0 : -1, "Convert H.264 frame for display");
            converted = true;
        }
        finally { bitmap.UnlockBits(data); if (!converted) { if (bitmapPool == null) bitmap.Dispose(); else bitmapPool.Return(bitmap); } }
        return bitmap;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (scaler != null) ffmpeg.sws_freeContext(scaler);
        var frameToFree = frame; ffmpeg.av_frame_free(&frameToFree);
        var packetToFree = packet; ffmpeg.av_packet_free(&packetToFree);
        var contextToFree = context; ffmpeg.avcodec_free_context(&contextToFree);
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation} failed ({result}).");
    }
}

internal sealed record EncodedVideoFrame(byte[] Data, double EncodeMs);

internal sealed class DecodedStreamFrame : IDisposable
{
    private BitmapLease? image;

    public DecodedStreamFrame(long sequence, DateTimeOffset capturedUtc, DesktopGeometry geometry, string codec, Bitmap image, double captureEncodeMs, int bytes, BitmapPool? pool = null)
    {
        Sequence = sequence; CapturedUtc = capturedUtc; Geometry = geometry; Codec = codec; this.image = new BitmapLease(image, pool); CaptureEncodeMs = captureEncodeMs; Bytes = bytes;
    }

    public long Sequence { get; }
    public DateTimeOffset CapturedUtc { get; }
    public DesktopGeometry Geometry { get; }
    public string Codec { get; }
    public double CaptureEncodeMs { get; }
    public int Bytes { get; }
    public BitmapLease TakeImage()
    {
        var result = image ?? throw new ObjectDisposedException(nameof(DecodedStreamFrame));
        image = null;
        return result;
    }
    public void Dispose() { image?.Dispose(); image = null; }
}
