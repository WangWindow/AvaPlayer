using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AvaPlayer.Models;

namespace AvaPlayer.ViewModels;

public partial class LyricLineViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    public string Text { get; init; } = string.Empty;

    public TimeSpan Time { get; init; }
}

public partial class LyricsViewModel : ViewModelBase
{
    private int _currentLineIndex = -1;

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showEmptyState = true;

    public event EventHandler<int>? ScrollToLineRequested;

    public void BeginLoading()
    {
        Lines.Clear();
        _currentLineIndex = -1;
        HasLyrics = false;
        IsLoading = true;
        ShowEmptyState = false;
    }

    public void LoadLyrics(IReadOnlyList<LyricLine> lines)
    {
        Lines.Clear();
        _currentLineIndex = -1;

        foreach (var line in lines)
        {
            Lines.Add(new LyricLineViewModel
            {
                Text = line.Text,
                Time = line.Time
            });
        }

        HasLyrics = Lines.Count > 0;
        IsLoading = false;
        ShowEmptyState = !HasLyrics;
    }

    public void ClearLyrics()
    {
        Lines.Clear();
        _currentLineIndex = -1;
        HasLyrics = false;
        IsLoading = false;
        ShowEmptyState = true;
    }

    public void UpdatePosition(double positionSeconds)
    {
        if (Lines.Count == 0)
        {
            return;
        }

        var playbackTime = TimeSpan.FromSeconds(positionSeconds);
        var newIndex = -1;

        for (var i = Lines.Count - 1; i >= 0; i--)
        {
            if (Lines[i].Time <= playbackTime)
            {
                newIndex = i;
                break;
            }
        }

        if (newIndex == _currentLineIndex)
        {
            return;
        }

        if (_currentLineIndex >= 0 && _currentLineIndex < Lines.Count)
        {
            Lines[_currentLineIndex].IsCurrent = false;
        }

        _currentLineIndex = newIndex;
        if (newIndex >= 0 && newIndex < Lines.Count)
        {
            Lines[newIndex].IsCurrent = true;
            ScrollToLineRequested?.Invoke(this, newIndex);
        }
    }
}
