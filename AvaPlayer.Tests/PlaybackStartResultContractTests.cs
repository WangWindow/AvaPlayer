using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Contract tests for <see cref="PlaybackStartResult"/> typed-result pattern.
/// </summary>
public sealed class PlaybackStartResultContractTests
{
    [Fact]
    public void Started_result_IsSuccess_true_IsFailure_false()
    {
        var result = new PlaybackStartResult.Started();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void Failed_result_IsSuccess_false_IsFailure_true()
    {
        var result = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.FileNotFound, "/missing/file.mp3");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Failed_result_contains_kind_and_message()
    {
        var result = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.EngineUnavailable, "engine not ready");

        Assert.Equal(PlaybackStartFailureKind.EngineUnavailable, result.Kind);
        Assert.Equal("engine not ready", result.Message);
    }

    [Fact]
    public void Failed_result_FileNotFound_kind_is_distinct()
    {
        var fileNotFound = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.FileNotFound, "path");
        var loadFailed = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.LoadFailed, "path");

        Assert.NotEqual(fileNotFound.Kind, loadFailed.Kind);
    }

    [Fact]
    public void Failed_result_LoadFailed_kind_is_distinct()
    {
        var loadFailed = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.LoadFailed, "corrupt file");
        var engineUnavailable = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.EngineUnavailable, "engine");

        Assert.NotEqual(loadFailed.Kind, engineUnavailable.Kind);
    }

    [Fact]
    public void Started_and_Failed_are_different_types()
    {
        var started = new PlaybackStartResult.Started();
        var failed = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.LoadFailed, "err");

        Assert.IsType<PlaybackStartResult.Started>(started);
        Assert.IsType<PlaybackStartResult.Failed>(failed);
    }
}
