using AvaPlayer.Services.Settings;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="IPlaybackPositionMemoryService"/> for unit testing.
/// </summary>
public sealed class FakePlaybackPositionMemoryService : IPlaybackPositionMemoryService
{
    public bool IsEnabled { get; set; } = true;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
