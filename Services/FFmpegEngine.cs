using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FFmpegCutStudio.Models;

namespace FFmpegCutStudio.Services;

public enum MergeStrategy
{
    Auto,
    DemuxerCopy,
    TsRelay,
    UnifiedReencode
}

public class FFmpegEngine
{
    public string FFmpegPath { get; set; } = "ffmpeg";
    public string FFprobePath { get; set; } = "ffprobe";

    private static string Inv(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    public async Task<MediaProbeInfo> ProbeMediaAsync(string filePath, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_streams",
            "-show_format",
            filePath
        };
        string json = await RunCaptureAsync(FFprobePath, args, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new MediaProbeInfo { FilePath = filePath };
        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var durEl) &&
            double.TryParse(durEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dur))
        {
            info.DurationSeconds = dur;
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            int subIdx = 0;
            foreach (var s in streams.EnumerateArray())
            {
                string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                string codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                int globalIndex = s.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                if (type == "video" && !info.HasVideo)
                {
                    info.HasVideo = true;
                    info.VideoCodec = codec;
                    info.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    info.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    info.PixelFormat = s.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "";
                    string fpsStr = s.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(fpsStr) || fpsStr == "0/0")
                        fpsStr = s.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() ?? "" : "";
                    info.Fps = ParseFps(fpsStr);
                }
                else if (type == "audio" && !info.HasAudio)
                {
                    info.HasAudio = true;
                    info.AudioCodec = codec;
                    if (s.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out int srVal))
                        info.SampleRate = srVal;
                    if (s.TryGetProperty("channels", out var ch))
                        info.Channels = ch.GetInt32();
                }
                else if (type == "subtitle")
                {
                    string lang = "und";
                    string title = "";
                    if (s.TryGetProperty("tags", out var tags))
                    {
                        if (tags.TryGetProperty("language", out var l)) lang = l.GetString() ?? "und";
                        if (tags.TryGetProperty("title", out var ti)) title = ti.GetString() ?? "";
                    }
                    info.SubtitleStreams.Add(new SubtitleStreamInfo
                    {
                        RelativeIndex = subIdx++,
                        GlobalIndex = globalIndex,
                        CodecName = codec,
                        Language = lang,
                        Title = title
                    });
                }
            }
        }
        return info;
    }

    public async Task<List<double>> ProbeKeyframesAsync(string filePath, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
            filePath
        };
        string output = await RunCaptureAsync(FFprobePath, args, ct);
        var list = new List<double>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains('K'))
            {
                int comma = line.IndexOf(',');
                if (comma > 0 &&
                    double.TryParse(line[..comma], NumberStyles.Any, CultureInfo.InvariantCulture, out double pts))
                {
                    list.Add(pts);
                }
            }
        }
        list.Sort();
        return list;
    }

    // =========================================================================
    // 1. 音视频分离 / 合并
    // =========================================================================
    public async Task DemuxAsync(
        string inputPath, string outputDir, bool extractVideo, bool extractAudio,
        Action<string> log, CancellationToken ct)
    {
        var info = await ProbeMediaAsync(inputPath, ct);
        Directory.CreateDirectory(outputDir);
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string ext = Path.GetExtension(inputPath);

        if (extractVideo && info.HasVideo)
        {
            string outVid = Path.Combine(outputDir, $"{stem}_video_only{ext}");
            await RunFFmpegAsync(new[] { "-y", "-i", inputPath, "-an", "-sn", "-c:v", "copy", outVid }, null, log, ct);
            log($"[完成] 纯视频已保存: {outVid}");
        }

        if (extractAudio && info.HasAudio)
        {
            string aExt = AudioExtForCodec(info.AudioCodec);
            string outAud = Path.Combine(outputDir, $"{stem}_audio_only{aExt}");
            log($"[检测] 音频编码为 {info.AudioCodec}，自动采用 {aExt} 无损封装。");
            await RunFFmpegAsync(new[] { "-y", "-i", inputPath, "-vn", "-sn", "-c:a", "copy", outAud }, null, log, ct);
            log($"[完成] 纯音频已保存: {outAud}");
        }
    }

    public async Task MuxAvAsync(
        string videoPath, string audioPath, string outputPath,
        bool shortest, bool reencodeAudioToAac,
        Action<string> log, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-y", "-i", videoPath, "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy"
        };
        if (reencodeAudioToAac)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        else
            args.AddRange(new[] { "-c:a", "copy" });

        if (shortest) args.Add("-shortest");
        args.Add(outputPath);

        await RunFFmpegAsync(args, null, log, ct);
        log($"[完成] 音视频合并完成: {outputPath}");
    }

    // =========================================================================
    // 2. 单视频多段裁剪（单段无损直出零临时文件 + -ss/-t 精准时长）
    // =========================================================================
    public async Task ExportSegmentsAsync(
        string inputPath,
        IReadOnlyList<ClipSegment> segments,
        string outputPathOrDir,
        bool mergeIntoSingleFile,
        bool losslessKeyframeMode,
        bool useNvenc,
        int quality,
        Action<string> log,
        CancellationToken ct)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("请至少添加一个有效分段。");

        var info = await ProbeMediaAsync(inputPath, ct);

        if (mergeIntoSingleFile)
        {
            if (losslessKeyframeMode)
            {
                // 优化：单段极速无损直接输出到目标文件，彻底省去临时文件二次读写！
                if (segments.Count == 1)
                {
                    var seg = segments[0];
                    double dur = Math.Max(0.1, seg.EndSeconds - seg.StartSeconds);
                    log("[模式] 单段极速无损直出（跳过临时文件中转，减少一半磁盘读写）...");
                    var cutArgs = new[]
                    {
                        "-y", "-ss", Inv(seg.StartSeconds),
                        "-i", inputPath,
                        "-t", Inv(dur),
                        "-c", "copy", "-avoid_negative_ts", "make_zero",
                        outputPathOrDir
                    };
                    await RunFFmpegAsync(cutArgs, null, log, ct);
                }
                else
                {
                    log($"[模式] 多段极速无损裁剪并拼接（共 {segments.Count} 段，按关键帧对齐）...");
                    string tmpDir = CreateTempDir("ff_cut_");
                    try
                    {
                        var partFiles = new List<string>();
                        string ext = Path.GetExtension(inputPath);
                        for (int i = 0; i < segments.Count; i++)
                        {
                            var seg = segments[i];
                            double dur = Math.Max(0.1, seg.EndSeconds - seg.StartSeconds);
                            string partPath = Path.Combine(tmpDir, $"seg_{i:D3}{ext}");
                            var cutArgs = new[]
                            {
                                "-y", "-ss", Inv(seg.StartSeconds),
                                "-i", inputPath,
                                "-t", Inv(dur),
                                "-c", "copy", "-avoid_negative_ts", "make_zero", partPath
                            };
                            await RunFFmpegAsync(cutArgs, null, log, ct);
                            partFiles.Add(partPath);
                        }
                        await MergeDemuxerAsync(partFiles, outputPathOrDir, log, ct);
                    }
                    finally { TryDeleteDir(tmpDir); }
                }
            }
            else
            {
                log($"[模式] 单条滤镜链精准裁剪合并（共 {segments.Count} 段，{(useNvenc ? "NVIDIA NVENC" : "CPU libx264")}）...");
                var filterParts = new List<string>();
                var concatIn = new StringBuilder();
                for (int i = 0; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    filterParts.Add($"[0:v]trim=start={Inv(seg.StartSeconds)}:end={Inv(seg.EndSeconds)},setpts=PTS-STARTPTS[v{i}]");
                    if (info.HasAudio)
                    {
                        filterParts.Add($"[0:a]atrim=start={Inv(seg.StartSeconds)}:end={Inv(seg.EndSeconds)},asetpts=PTS-STARTPTS[a{i}]");
                        concatIn.Append($"[v{i}][a{i}]");
                    }
                    else
                    {
                        concatIn.Append($"[v{i}]");
                    }
                }

                var args = new List<string> { "-y", "-i", inputPath };
                if (info.HasAudio)
                {
                    filterParts.Add($"{concatIn}concat=n={segments.Count}:v=1:a=1[outv][outa]");
                    args.AddRange(new[] { "-filter_complex", string.Join(";", filterParts), "-map", "[outv]", "-map", "[outa]" });
                }
                else
                {
                    filterParts.Add($"{concatIn}concat=n={segments.Count}:v=1:a=0[outv]");
                    args.AddRange(new[] { "-filter_complex", string.Join(";", filterParts), "-map", "[outv]" });
                }

                AppendVideoEncoderArgs(args, useNvenc, quality);
                if (info.HasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                args.AddRange(new[] { "-movflags", "+faststart", outputPathOrDir });
                await RunFFmpegAsync(args, null, log, ct);
            }
            log($"[完成] 裁剪导出完成: {outputPathOrDir}");
        }
        else
        {
            Directory.CreateDirectory(outputPathOrDir);
            string stem = Path.GetFileNameWithoutExtension(inputPath);
            string ext = losslessKeyframeMode ? Path.GetExtension(inputPath) : ".mp4";

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                double dur = Math.Max(0.1, seg.EndSeconds - seg.StartSeconds);
                string outPath = Path.Combine(outputPathOrDir, $"{stem}_seg{i + 1:D2}{ext}");
                var args = new List<string>
                {
                    "-y", "-ss", Inv(seg.StartSeconds), "-i", inputPath, "-t", Inv(dur)
                };

                if (losslessKeyframeMode)
                {
                    args.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
                }
                else
                {
                    AppendVideoEncoderArgs(args, useNvenc, quality);
                    if (info.HasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                    args.AddRange(new[] { "-movflags", "+faststart" });
                }
                args.Add(outPath);
                await RunFFmpegAsync(args, null, log, ct);
                log($"[完成] 分段 {i + 1}/{segments.Count} 已保存: {outPath}");
            }
        }
    }

    // =========================================================================
    // 3. 多视频智能合并（ffprobe 预检 + 三档策略 + NVENC）
    // =========================================================================
    public async Task MergeVideosAsync(
        IReadOnlyList<string> files,
        string outputPath,
        MergeStrategy strategy,
        bool useNvenc,
        int quality,
        Action<string> log,
        CancellationToken ct)
    {
        if (files.Count < 2)
            throw new InvalidOperationException("请至少添加 2 个视频文件。");

        log($"[预检] 正在扫描 {files.Count} 个视频的编码与容器参数...");
        var infos = new List<MediaProbeInfo>();
        for (int i = 0; i < files.Count; i++)
        {
            var p = await ProbeMediaAsync(files[i], ct);
            infos.Add(p);
            log($"  #{i + 1} {Path.GetFileName(p.FilePath)}: {p.VideoCodec} {p.Width}x{p.Height} {p.Fps:F2}fps {p.PixelFormat} | 音频: {(p.HasAudio ? $"{p.AudioCodec} {p.SampleRate}Hz {p.Channels}ch" : "无")}");
        }

        MergeStrategy chosen = strategy;
        if (strategy == MergeStrategy.Auto)
        {
            var b = infos[0];
            bool sameVideoStrict = infos.All(x =>
                x.VideoCodec == b.VideoCodec && x.Width == b.Width && x.Height == b.Height &&
                x.PixelFormat == b.PixelFormat && Math.Abs(x.Fps - b.Fps) < 0.05);
            bool sameAudioStrict = infos.All(x =>
                x.HasAudio == b.HasAudio && x.AudioCodec == b.AudioCodec &&
                x.SampleRate == b.SampleRate && x.Channels == b.Channels);
            bool sameH26xRes = infos.All(x =>
                x.VideoCodec == b.VideoCodec && (x.VideoCodec is "h264" or "hevc") &&
                x.Width == b.Width && x.Height == b.Height);

            if (sameVideoStrict && sameAudioStrict)
            {
                chosen = MergeStrategy.DemuxerCopy;
                log("[决策] 参数完全一致 -> 采用【无损直连 (concat demuxer)】，无需转 TS。");
            }
            else if (sameH26xRes && sameAudioStrict)
            {
                chosen = MergeStrategy.TsRelay;
                log("[决策] 同为 H.264/H.265 且分辨率/音频一致 -> 自动走【TS 中转无损合并】。");
            }
            else
            {
                chosen = MergeStrategy.UnifiedReencode;
                log($"[决策] 编码/分辨率/帧率/音频不匹配 -> 自动切换【统一规格重编码 ({(useNvenc ? "NVENC" : "CPU")})】。");
            }
        }

        switch (chosen)
        {
            case MergeStrategy.DemuxerCopy:
                await MergeDemuxerAsync(files, outputPath, log, ct);
                break;
            case MergeStrategy.TsRelay:
                await MergeViaTsAsync(infos, outputPath, log, ct);
                break;
            default:
                await MergeUnifiedReencodeAsync(infos, outputPath, useNvenc, quality, log, ct);
                break;
        }
        log($"[完成] 多视频合并完成: {outputPath}");
    }

    private async Task MergeDemuxerAsync(IReadOnlyList<string> files, string outputPath, Action<string> log, CancellationToken ct)
    {
        string tmpDir = CreateTempDir("ff_concat_");
        try
        {
            string listFile = Path.Combine(tmpDir, "list.txt");
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                string safe = Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''");
                sb.AppendLine($"file '{safe}'");
            }
            await File.WriteAllTextAsync(listFile, sb.ToString(), new UTF8Encoding(false), ct);

            await RunFFmpegAsync(new[]
            {
                "-y", "-f", "concat", "-safe", "0", "-i", listFile,
                "-c", "copy", outputPath
            }, null, log, ct);
        }
        finally { TryDeleteDir(tmpDir); }
    }

    private async Task MergeViaTsAsync(IReadOnlyList<MediaProbeInfo> infos, string outputPath, Action<string> log, CancellationToken ct)
    {
        string tmpDir = CreateTempDir("ff_ts_");
        try
        {
            var tsFiles = new List<string>();
            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string tsPath = Path.Combine(tmpDir, $"part_{i:D3}.ts");
                var args = new List<string> { "-y", "-i", info.FilePath, "-c", "copy" };
                if (info.VideoCodec == "h264") args.AddRange(new[] { "-bsf:v", "h264_mp4toannexb" });
                else if (info.VideoCodec == "hevc") args.AddRange(new[] { "-bsf:v", "hevc_mp4toannexb" });
                args.AddRange(new[] { "-f", "mpegts", tsPath });
                await RunFFmpegAsync(args, null, log, ct);
                tsFiles.Add(tsPath);
            }

            string listFile = Path.Combine(tmpDir, "ts_list.txt");
            var sb = new StringBuilder();
            foreach (var ts in tsFiles)
                sb.AppendLine($"file '{ts.Replace('\\', '/')}'");
            await File.WriteAllTextAsync(listFile, sb.ToString(), new UTF8Encoding(false), ct);

            var mergeArgs = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy" };
            if (infos[0].HasAudio && infos[0].AudioCodec == "aac")
                mergeArgs.AddRange(new[] { "-bsf:a", "aac_adtstoasc" });
            mergeArgs.Add(outputPath);
            await RunFFmpegAsync(mergeArgs, null, log, ct);
        }
        finally { TryDeleteDir(tmpDir); }
    }

    private async Task MergeUnifiedReencodeAsync(
        IReadOnlyList<MediaProbeInfo> infos, string outputPath, bool useNvenc, int quality,
        Action<string> log, CancellationToken ct)
    {
        int targetW = infos[0].Width > 0 ? infos[0].Width : 1920;
        int targetH = infos[0].Height > 0 ? infos[0].Height : 1080;
        targetW += targetW % 2;
        targetH += targetH % 2;
        double targetFps = infos[0].Fps > 0 ? infos[0].Fps : 30.0;
        bool anyAudio = infos.Any(x => x.HasAudio);

        var args = new List<string> { "-y" };
        foreach (var info in infos) args.AddRange(new[] { "-i", info.FilePath });

        var filters = new List<string>();
        var concatIn = new StringBuilder();
        for (int i = 0; i < infos.Count; i++)
        {
            filters.Add(
                $"[{i}:v]scale={targetW}:{targetH}:force_original_aspect_ratio=decrease," +
                $"pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Inv(targetFps)},format=yuv420p[v{i}]");
            if (anyAudio)
            {
                if (infos[i].HasAudio)
                    filters.Add($"[{i}:a]aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo[a{i}]");
                else
                    filters.Add($"anullsrc=channel_layout=stereo:sample_rate=48000[a{i}]");
                concatIn.Append($"[v{i}][a{i}]");
            }
            else
            {
                concatIn.Append($"[v{i}]");
            }
        }

        if (anyAudio)
        {
            filters.Add($"{concatIn}concat=n={infos.Count}:v=1:a=1[outv][outa]");
            args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[outv]", "-map", "[outa]" });
        }
        else
        {
            filters.Add($"{concatIn}concat=n={infos.Count}:v=1:a=0[outv]");
            args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", "[outv]" });
        }

        AppendVideoEncoderArgs(args, useNvenc, quality);
        if (anyAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-movflags", "+faststart", outputPath });
        await RunFFmpegAsync(args, null, log, ct);
    }

    // =========================================================================
    // 4. 字幕分离 / 软封装 / 硬写入
    // =========================================================================
    public async Task ExtractSubtitleAsync(
        string videoPath, int subRelativeIndex, string outputPath,
        Action<string> log, CancellationToken ct)
    {
        await RunFFmpegAsync(new[] { "-y", "-i", videoPath, "-map", $"0:s:{subRelativeIndex}", outputPath }, null, log, ct);
        log($"[完成] 字幕已提取: {outputPath}");
    }

    public async Task SoftMuxSubtitleAsync(
        string videoPath, string subtitlePath, string outputPath, string language,
        Action<string> log, CancellationToken ct)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        string subCodec = ext is ".mp4" or ".m4v" or ".mov" ? "mov_text" : "copy";
        log($"[容器适配] 输出容器 {ext} -> 字幕编码采用 -c:s {subCodec}");

        var args = new[]
        {
            "-y", "-i", videoPath, "-i", subtitlePath,
            "-map", "0:v", "-map", "0:a?", "-map", "1:0",
            "-c:v", "copy", "-c:a", "copy", "-c:s", subCodec,
            "-metadata:s:s:0", $"language={(string.IsNullOrWhiteSpace(language) ? "chi" : language.Trim())}",
            outputPath
        };
        await RunFFmpegAsync(args, null, log, ct);
        log($"[完成] 软字幕封装完成: {outputPath}");
    }

    public async Task BurnSubtitleAsync(
        string videoPath, string subtitlePath, string outputPath,
        bool useNvenc, int quality, bool copyAudio,
        Action<string> log, CancellationToken ct)
    {
        string tmpDir = CreateTempDir("ff_hardsub_");
        try
        {
            string subExt = Path.GetExtension(subtitlePath).ToLowerInvariant();
            string safeName = "input_sub" + (subExt is ".ass" or ".ssa" or ".srt" or ".vtt" ? subExt : ".srt");
            File.Copy(subtitlePath, Path.Combine(tmpDir, safeName), true);

            string vf = subExt is ".ass" or ".ssa" ? $"ass={safeName}" : $"subtitles={safeName}";
            var args = new List<string>
            {
                "-y", "-i", Path.GetFullPath(videoPath),
                "-vf", vf
            };
            AppendVideoEncoderArgs(args, useNvenc, quality);
            if (copyAudio) args.AddRange(new[] { "-c:a", "copy" });
            else args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(new[] { "-movflags", "+faststart", Path.GetFullPath(outputPath) });

            await RunFFmpegAsync(args, tmpDir, log, ct);
            log($"[完成] 字幕硬写入完成: {outputPath}");
        }
        finally { TryDeleteDir(tmpDir); }
    }

    private static void AppendVideoEncoderArgs(List<string> args, bool useNvenc, int quality)
    {
        int q = Math.Clamp(quality, 10, 35);
        if (useNvenc)
        {
            args.AddRange(new[]
            {
                "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr",
                "-cq", q.ToString(), "-b:v", "0", "-pix_fmt", "yuv420p"
            });
        }
        else
        {
            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast",
                "-crf", q.ToString(), "-pix_fmt", "yuv420p"
            });
        }
    }

    private async Task RunFFmpegAsync(IEnumerable<string> args, string? workingDir, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpegPath,
            WorkingDirectory = workingDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        log("$ ffmpeg " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        var errTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) log(line);
            }
        });
        var outTask = proc.StandardOutput.ReadToEndAsync();

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(errTask, outTask);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException("任务已被用户中止。");
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 执行失败，退出码: {proc.ExitCode}");
    }

    private static async Task<string> RunCaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{exe} 执行失败: {stderr}");
        return stdout;
    }

    public static string AudioExtForCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" or "alac" => ".m4a",
        "mp3" => ".mp3",
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "flac" => ".flac",
        "ac3" => ".ac3",
        "eac3" => ".eac3",
        "dts" => ".dts",
        "pcm_s16le" or "pcm_s24le" => ".wav",
        _ => ".mka"
    };

    public static string SubExtForCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "ass" or "ssa" => ".ass",
        "webvtt" or "vtt" => ".vtt",
        _ => ".srt"
    };

    private static double ParseFps(string fpsStr)
    {
        if (string.IsNullOrWhiteSpace(fpsStr) || fpsStr is "0/0" or "N/A") return 0;
        if (fpsStr.Contains('/'))
        {
            var parts = fpsStr.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double n) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double d) && d > 0)
                return n / d;
            return 0;
        }
        return double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static string CreateTempDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
