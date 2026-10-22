using AvaPlayer.Models;
using AvaPlayer.Tests.Fakes;
using AvaPlayer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

public sealed class PlaylistViewModelTests
{
    [Fact]
    public void Rescanned_track_with_new_identity_keeps_current_marker_by_path()
    {
        var playlist = new FakePlaylistService();
        playlist.AddTrack(new Track { Id = "new-id", FilePath = "/Music/song.wav" });
        using var viewModel = new PlaylistViewModel(playlist, NullLogger<PlaylistViewModel>.Instance);
        viewModel.Activate();
        viewModel.MarkCurrentTrack(new Track { Id = "legacy-id", FilePath = "/Music/song.wav" });
        Assert.True(Assert.Single(viewModel.Tracks).IsCurrent);
    }
}
