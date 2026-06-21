using System;
using System.ComponentModel;

namespace JonPlayer
{
    public class PlaylistItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? AudioPath { get; set; } = null;
        public string? YoutubeUrl { get; set; } = null;

        private bool _isCurrentlyPlaying;
        public bool IsCurrentlyPlaying
        {
            get => _isCurrentlyPlaying;
            set
            {
                if (_isCurrentlyPlaying != value)
                {
                    _isCurrentlyPlaying = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentlyPlaying)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
