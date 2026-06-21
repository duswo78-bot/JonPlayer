using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xabe.FFmpeg.Downloader;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace JonPlayer
{
    public class YouTubeStreamInfo
    {
        public string Title { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public string? StreamUrl { get; set; }
    }

    public class YouTubeDownloadInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Extension { get; set; } = "mp4";
        public string Quality { get; set; } = string.Empty;
    }

    public class YouTubeStreamingService
    {
        private readonly YoutubeClient _youtube;

        /// <summary>
        /// 마지막으로 추출된 오디오 전용 스트림 URL (adaptive 스트리밍 시 비디오와 별도로 디코더에 전달)
        /// </summary>
        public string? LastAudioUrl { get; private set; }

        public YouTubeStreamingService()
        {
            var handler = new System.Net.Http.HttpClientHandler();
            handler.UseCookies = false;
            var httpClient = new System.Net.Http.HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _youtube = new YoutubeClient(httpClient);
        }

        public async Task<YouTubeStreamInfo> GetVideoInfoAsync(string url)
        {
            try
            {
                var video = await _youtube.Videos.GetAsync(url);
                var info = new YouTubeStreamInfo { Title = video.Title };

                var thumbnailUrl = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url;
                if (thumbnailUrl != null)
                {
                    info.ThumbnailUrl = thumbnailUrl.Replace(".webp", ".jpg").Replace("vi_webp", "vi");
                }
                
                return info;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get YouTube video info for {url}", ex);
                throw;
            }
        }

        public async Task<string?> GetStreamUrlAsync(string url)
        {
            try
            {
                LastAudioUrl = null;
                var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(url);
                
                // Prefer muxed streams for live playback stability. Adaptive video-only URLs can fail independently,
                // leaving the separate audio stream playing with no rendered video.
                var muxedStreamInfo = streamManifest.GetMuxedStreams()
                    .Where(s => s.VideoQuality.MaxHeight <= 1080)
                    .OrderByDescending(s => s.VideoQuality)
                    .FirstOrDefault() ??
                    streamManifest.GetMuxedStreams()
                        .OrderByDescending(s => s.VideoQuality)
                        .FirstOrDefault();

                if (muxedStreamInfo != null)
                {
                    Logger.Info($"YouTube muxed playback: video={muxedStreamInfo.VideoQuality} ({muxedStreamInfo.Container.Name}), bitrate={muxedStreamInfo.Bitrate}");
                    return muxedStreamInfo.Url;
                }

                // Fallback to adaptive only when no muxed stream is available.
                var videoStreamInfo = streamManifest.GetVideoOnlyStreams()
                    .Where(s => s.VideoQuality.MaxHeight <= 1080)
                    .OrderByDescending(s => s.VideoQuality)
                    .FirstOrDefault();
                var audioStreamInfo = streamManifest.GetAudioOnlyStreams()
                    .OrderByDescending(s => s.Bitrate)
                    .FirstOrDefault();

                if (videoStreamInfo != null && audioStreamInfo != null)
                {
                    // 비디오 URL을 직접 반환, 오디오 URL은 별도 프로퍼티로 저장
                    LastAudioUrl = audioStreamInfo.Url;
                    Logger.Info($"YouTube adaptive: video={videoStreamInfo.VideoQuality} ({videoStreamInfo.Container.Name}), audio={audioStreamInfo.Bitrate} ({audioStreamInfo.Container.Name})");
                    return videoStreamInfo.Url;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get YouTube stream URL for {url}", ex);
                throw;
            }
        }

        public async Task<string?> GetMuxedStreamUrlAsync(string url)
        {
            try
            {
                LastAudioUrl = null;
                var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(url);

                var muxedStreamInfo = streamManifest.GetMuxedStreams()
                    .Where(s => s.VideoQuality.MaxHeight <= 480)
                    .OrderByDescending(s => s.VideoQuality)
                    .FirstOrDefault() ??
                    streamManifest.GetMuxedStreams()
                        .OrderBy(s => s.VideoQuality)
                        .FirstOrDefault();

                if (muxedStreamInfo != null)
                {
                    Logger.Info($"YouTube muxed seek fallback: video={muxedStreamInfo.VideoQuality} ({muxedStreamInfo.Container.Name}), bitrate={muxedStreamInfo.Bitrate}");
                    return muxedStreamInfo.Url;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get YouTube muxed stream URL for {url}", ex);
                throw;
            }
        }

        public async Task<YouTubeDownloadInfo?> GetBestDownloadInfoAsync(string url)
        {
            try
            {
                var video = await _youtube.Videos.GetAsync(url);
                var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(url);

                var videoStreamInfo = streamManifest.GetVideoOnlyStreams()
                    .Where(s => string.Equals(s.Container.Name, "mp4", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.VideoQuality)
                    .FirstOrDefault() ??
                    streamManifest.GetVideoOnlyStreams()
                        .OrderByDescending(s => s.VideoQuality)
                        .FirstOrDefault();
                var muxedStreamInfo = videoStreamInfo == null
                    ? streamManifest.GetMuxedStreams()
                        .OrderByDescending(s => s.VideoQuality)
                        .FirstOrDefault()
                    : null;
                string? quality = videoStreamInfo?.VideoQuality.ToString() ?? muxedStreamInfo?.VideoQuality.ToString();

                if (quality == null) return null;

                return new YouTubeDownloadInfo
                {
                    Title = SanitizeFileName(video.Title),
                    Extension = "mp4",
                    Quality = quality
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get YouTube download info for {url}", ex);
                throw;
            }
        }

        public async Task DownloadBestVideoAsync(string url, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            string ffmpegPath = await EnsureFFmpegCliAsync(cancellationToken);
            await _youtube.Videos.DownloadAsync(
                VideoId.Parse(url),
                outputPath,
                builder => builder
                    .SetFFmpegPath(ffmpegPath)
                    .SetContainer(Container.Mp4)
                    .SetPreset(ConversionPreset.UltraFast),
                progress,
                cancellationToken);
        }

        public static async Task<string> EnsureFFmpegCliAsync(CancellationToken cancellationToken = default)
        {
            string ffmpegDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JonPlayer",
                "ffmpeg");
            string ffmpegPath = Path.Combine(ffmpegDirectory, "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                Directory.CreateDirectory(ffmpegDirectory);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDirectory);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException("FFmpeg CLI was not installed correctly.", ffmpegPath);
            }

            return ffmpegPath;
        }

        private static string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(fileName) ? "youtube_video" : fileName.Trim();
        }

        public async Task<byte[]> DownloadThumbnailAsync(string thumbnailUrl)
        {
            try
            {
                using var httpClient = new HttpClient();
                return await httpClient.GetByteArrayAsync(thumbnailUrl);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to download thumbnail from {thumbnailUrl}", ex);
                throw;
            }
        }

        public async Task FetchSubtitlesAsync(string url, SubtitleManager subtitleManager, CancellationToken ct)
        {
            try
            {
                var trackManifest = await _youtube.Videos.ClosedCaptions.GetManifestAsync(url);
                var trackInfo = trackManifest.Tracks.FirstOrDefault(t => t.Language.Code == "ko") ?? 
                                trackManifest.Tracks.FirstOrDefault(t => t.Language.Code == "en") ?? 
                                trackManifest.Tracks.FirstOrDefault();
                
                if (trackInfo != null)
                {
                    var track = await _youtube.Videos.ClosedCaptions.GetAsync(trackInfo, ct);
                    
                    // 스트리밍 시 자막이 2초 정도 늦게 나오는 현상 보정 (비디오 PTS 오프셋 등)
                    TimeSpan syncOffset = TimeSpan.FromSeconds(-2.0);
                    foreach (var caption in track.Captions)
                    {
                        var start = caption.Offset + syncOffset;
                        var end = caption.Offset + caption.Duration + syncOffset;
                        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
                        if (end < TimeSpan.Zero) end = TimeSpan.Zero;
                        subtitleManager.AddSubtitle(start, end, caption.Text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to fetch subtitles for {url}", ex);
                throw;
            }
        }
    }
}
