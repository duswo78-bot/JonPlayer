using System;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace JonPlayer
{
    public static class WhisperExtractor
    {
        public static async Task ExtractSubtitlesAsync(string videoPath, string tempWavPath, Action<string, double> onProgress, Action<TimeSpan, TimeSpan, string> onSubtitleGenerated, Action<string?, string?> onComplete, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Convert video audio to 16kHz 16-bit Mono WAV using NAudio
                onProgress("🪄 AI 가사 추출 중...", 10);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() => ExtractAudio(videoPath, tempWavPath, cancellationToken), cancellationToken);

                // 2. Check if Whisper Model exists (Bundled)
                string modelName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ggml-small.bin");
                if (!File.Exists(modelName))
                {
                    onProgress("🪄 AI 가사 모델 다운로드 중...", 30);
                    cancellationToken.ThrowIfCancellationRequested();
                    await DownloadModel(modelName);
                }

                // 3. Process with Whisper
                onProgress("🪄 AI 가사 추출 중...", 50);
                string srtPath = Path.ChangeExtension(videoPath, ".srt");

                await Task.Run(async () => 
                {
                    using var whisperFactory = WhisperFactory.FromPath(modelName);
                    await using var processor = whisperFactory.CreateBuilder()
                        .WithLanguage("auto")
                        .Build();

                    using var fileStream = File.OpenRead(tempWavPath);
                    using var srtWriter = new StreamWriter(srtPath, false, System.Text.Encoding.UTF8);

                    int index = 1;
                    int totalSeconds = GetWavDurationSeconds(tempWavPath);

                    await foreach (var result in processor.ProcessAsync(fileStream, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Write SRT format
                        srtWriter.WriteLine(index++);
                        srtWriter.WriteLine($"{FormatTime(result.Start)} --> {FormatTime(result.End)}");
                        srtWriter.WriteLine(result.Text.Trim());
                        srtWriter.WriteLine();
                        await srtWriter.FlushAsync();

                        onSubtitleGenerated?.Invoke(result.Start, result.End, result.Text.Trim());

                        double progress = 50 + (result.End.TotalSeconds / (totalSeconds == 0 ? 1 : totalSeconds)) * 50;
                        onProgress("🪄 AI 가사 추출 중...", Math.Min(99, progress));
                    }
                }, cancellationToken);

                // Cleanup temp wav
                try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); } catch { }

                onProgress("🪄 AI 가사 추출 중...", 100);
                onComplete(srtPath, null);
            }
            catch (OperationCanceledException)
            {
                // Cleanup partial files
                try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); } catch { }
                string srtPath = Path.ChangeExtension(videoPath, ".srt");
                try { if (File.Exists(srtPath)) File.Delete(srtPath); } catch { }

                onComplete(null, "작업이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                // Cleanup partial files on error
                try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); } catch { }
                string srtPath = Path.ChangeExtension(videoPath, ".srt");
                try { if (File.Exists(srtPath)) File.Delete(srtPath); } catch { }

                Console.WriteLine($"Error during Whisper extraction: {ex.Message}");
                string errorMsg = ex.ToString();
                if (errorMsg.Contains("503"))
                {
                    errorMsg += " (사내 방화벽/보안 정책으로 모델 다운로드가 차단되었습니다. 수동으로 ggml-small.bin을 다운로드하세요.)";
                }
                onComplete(null, errorMsg);
            }
        }

        private static void ExtractAudio(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            // Use MediaFoundationReader which can read the audio stream from video files on Windows
            using var reader = new MediaFoundationReader(inputPath);
            var outFormat = new WaveFormat(16000, 16, 1); // Whisper needs 16kHz, 16-bit, Mono
            using var resampler = new MediaFoundationResampler(reader, outFormat);
            resampler.ResamplerQuality = 60; // good quality
            
            using var writer = new WaveFileWriter(outputPath, outFormat);
            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, bytesRead);
            }
        }

        private static async Task DownloadModel(string fileName)
        {
            var handler = new HttpClientHandler
            {
                UseDefaultCredentials = true,
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => { return true; }
            };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var stream = await client.GetStreamAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin");
            
            string tempPath = fileName + ".tmp";
            try
            {
                using var fileWriter = File.Create(tempPath);
                await stream.CopyToAsync(fileWriter);
                fileWriter.Close();

                // Atomic replacement: only move if download completed successfully
                File.Move(tempPath, fileName, true);
            }
            catch
            {
                // Clean up partial download
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }


        private static string FormatTime(TimeSpan time)
        {
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
        }

        private static int GetWavDurationSeconds(string filePath)
        {
            try
            {
                using var reader = new WaveFileReader(filePath);
                return (int)reader.TotalTime.TotalSeconds;
            }
            catch { return 1; }
        }
    }
}
