using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SubtitlesParser.Classes;
using SubtitlesParser.Classes.Parsers;

namespace JonPlayer
{
    public class SubtitleManager
    {
        private List<SubtitleItem> _subtitles = new List<SubtitleItem>();
        private int _lastIndex = 0;

        private bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                var utf8 = new UTF8Encoding(false, true);
                utf8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<SubtitleItem> ParseSmi(byte[] bytes, Encoding encoding)
        {
            var subs = new List<SubtitleItem>();
            string content = encoding.GetString(bytes);
            
            var matches = Regex.Matches(content, @"(?i)<SYNC\s+Start\s*=\s*([0-9]+)\s*>(.*?)((?=<SYNC)|$)", RegexOptions.Singleline);
            
            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    if (int.TryParse(match.Groups[1].Value, out int startMs))
                    {
                        string text = match.Groups[2].Value;
                        text = Regex.Replace(text, @"(?i)<br\s*/?>", Environment.NewLine);
                        text = Regex.Replace(text, @"<[^>]+>", "");
                        text = text.Replace("&nbsp;", " ").Trim();
                        
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            subs.Add(new SubtitleItem
                            {
                                StartTime = startMs,
                                EndTime = startMs + 5000,
                                Lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                            });
                        }
                    }
                }
            }
            
            for (int i = 0; i < subs.Count - 1; i++)
            {
                subs[i].EndTime = subs[i + 1].StartTime;
            }

            return subs;
        }

        public bool LoadSubtitle(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                byte[] bytes = File.ReadAllBytes(filePath);
                Encoding encoding = Encoding.UTF8;
                
                // BOM check
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    encoding = Encoding.UTF8;
                }
                else if (!IsValidUtf8(bytes))
                {
                    encoding = Encoding.GetEncoding("euc-kr");
                }

                if (filePath.EndsWith(".smi", StringComparison.OrdinalIgnoreCase))
                {
                    _subtitles = ParseSmi(bytes, encoding);
                }
                else
                {
                    var parser = new SubParser();
                    using (var stream = new MemoryStream(bytes))
                    {
                        _subtitles = parser.ParseStream(stream, encoding);
                    }
                }
                
                // Sort by start time just in case
                if (_subtitles != null)
                {
                    _subtitles = _subtitles.OrderBy(s => s.StartTime).ToList();
                }
                else
                {
                    _subtitles = new List<SubtitleItem>();
                }
                
                _lastIndex = 0;
                return _subtitles.Count > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading subtitle: {ex.Message}");
                return false;
            }
        }

        public void Clear()
        {
            _subtitles.Clear();
            _lastIndex = 0;
        }

        public void AddSubtitle(TimeSpan start, TimeSpan end, string text)
        {
            var item = new SubtitleItem
            {
                StartTime = (int)start.TotalMilliseconds,
                EndTime = (int)end.TotalMilliseconds,
                Lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList()
            };
            
            _subtitles.Add(item);
        }

        public bool HasSubtitles => _subtitles != null && _subtitles.Count > 0;

        public string DetectLanguage()
        {
            if (!HasSubtitles) return "KR";

            int checkCount = Math.Min(50, _subtitles.Count);
            for (int i = 0; i < checkCount; i++)
            {
                if (_subtitles[i].Lines == null) continue;
                foreach (var line in _subtitles[i].Lines)
                {
                    if (line.Any(ch => ch >= '\uac00' && ch <= '\ud7a3'))
                    {
                        return "KR";
                    }
                }
            }
            return "EN";
        }

        public string GetSubtitleText(int timeMs)
        {
            if (!HasSubtitles) return string.Empty;

            if (_lastIndex >= _subtitles.Count) _lastIndex = 0;

            if (_lastIndex > 0 && timeMs < _subtitles[_lastIndex - 1].StartTime)
            {
                _lastIndex = 0;
            }

            for (int i = _lastIndex; i < _subtitles.Count; i++)
            {
                var item = _subtitles[i];
                if (timeMs >= item.StartTime && timeMs <= item.EndTime)
                {
                    _lastIndex = i;
                    return string.Join(Environment.NewLine, item.Lines);
                }
                
                if (item.StartTime > timeMs)
                {
                    _lastIndex = i;
                    break;
                }
            }

            return string.Empty;
        }
    }
}
