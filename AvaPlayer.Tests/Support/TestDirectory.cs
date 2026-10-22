namespace AvaPlayer.Tests.Support;

public sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"avaplayer-{Guid.NewGuid():N}");

    public TestDirectory() => Directory.CreateDirectory(Root);

    public string FilePath(string name) => Path.Combine(Root, name);

    public string CreateWave(string name = "track.wav", string? lyrics = null)
    {
        var path = FilePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int dataLength = 16000;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataLength);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(8000);
            writer.Write(16000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
        }
        if (lyrics is not null)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Lyrics = lyrics;
            file.Save();
        }
        return path;
    }

    public void Dispose()
    {
        // Pooled native SQLite handles can prevent fixture deletion on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
