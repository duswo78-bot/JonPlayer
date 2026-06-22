using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SubtitlesParser.Classes;
using SubtitlesParser.Classes.Parsers;

namespace JonPlayer
{
    public class SubtitleManager
    {
        private List<SubtitleItem> _subtitles = new List<SubtitleItem>();
        private int _lastIndex = 0;

        public bool LoadSubtitle(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;

                var parser = new SubParser();
                using (var stream = File.OpenRead(filePath))
                {
                    _subtitles = parser.ParseStream(stream, Encoding.UTF8);
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
            // In a strict streaming scenario, it's mostly sequential, but just in case:
            // Actually, we don't need to sort every time if it's appended sequentially.
            // But doing a small insertion sort logic or just letting it be is fine.
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

            // Sequential search optimized for linear playback
            if (_lastIndex >= _subtitles.Count) _lastIndex = 0;

            // If time went backwards or skipped way ahead, reset search
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
                    // Join multiple lines and return
                    return string.Join(Environment.NewLine, item.Lines);
                }
                
                if (item.StartTime > timeMs)
                {
                    // We passed the current time without finding a match, 
                    // which means there is no subtitle right now.
                    _lastIndex = i;
                    break;
                }
            }

            return string.Empty;
        }
    }
}
