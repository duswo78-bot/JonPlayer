using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace JonPlayer
{
    public class PlaylistManager
    {
        public ObservableCollection<PlaylistItem> Items { get; } = new ObservableCollection<PlaylistItem>();
        
        public int CurrentIndex { get; private set; } = -1;

        public event EventHandler<PlaylistItem>? PlaybackRequested;

        public void AddFile(string path, string? youtubeTitle = null, string? youtubeUrl = null)
        {
            Items.Add(new PlaylistItem 
            { 
                Path = path, 
                Name = youtubeTitle ?? Path.GetFileName(path),
                YoutubeUrl = youtubeUrl
            });
            Logger.Info($"Added to playlist: {path}");
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                AddFile(path);
            }
        }

        public void RemoveAt(int index)
        {
            if (index >= 0 && index < Items.Count)
            {
                var removed = Items[index];
                Items.RemoveAt(index);
                Logger.Info($"Removed from playlist: {removed.Name}");

                if (index < CurrentIndex)
                {
                    CurrentIndex--;
                }
                else if (index == CurrentIndex)
                {
                    // Currently playing item removed.
                    // Depending on the design, we might want to stop playback or play next.
                }
            }
        }

        public void Clear()
        {
            Items.Clear();
            CurrentIndex = -1;
            Logger.Info("Playlist cleared.");
        }

        public PlaylistItem? GetNext()
        {
            if (Items.Count == 0) return null;
            if (CurrentIndex >= Items.Count - 1) return null;
            
            CurrentIndex++;
            return Items[CurrentIndex];
        }

        public PlaylistItem? GetPrevious()
        {
            if (Items.Count == 0) return null;
            if (CurrentIndex <= 0) return null;

            CurrentIndex--;
            return Items[CurrentIndex];
        }

        public bool SetCurrentIndex(int index)
        {
            if (index >= 0 && index < Items.Count)
            {
                CurrentIndex = index;
                return true;
            }
            return false;
        }
        
        public PlaylistItem? GetCurrent()
        {
            if (CurrentIndex >= 0 && CurrentIndex < Items.Count)
                return Items[CurrentIndex];
            return null;
        }

        public void RequestPlay(int index)
        {
            if (SetCurrentIndex(index))
            {
                PlaybackRequested?.Invoke(this, Items[index]);
            }
        }
        
        public void MoveItem(int oldIndex, int newIndex)
        {
            if (oldIndex >= 0 && oldIndex < Items.Count && newIndex >= 0 && newIndex < Items.Count)
            {
                var item = Items[oldIndex];
                Items.RemoveAt(oldIndex);
                Items.Insert(newIndex, item);
                
                // Adjust current index
                if (CurrentIndex == oldIndex)
                {
                    CurrentIndex = newIndex;
                }
                else if (oldIndex < CurrentIndex && newIndex >= CurrentIndex)
                {
                    CurrentIndex--;
                }
                else if (oldIndex > CurrentIndex && newIndex <= CurrentIndex)
                {
                    CurrentIndex++;
                }
            }
        }
    }
}
