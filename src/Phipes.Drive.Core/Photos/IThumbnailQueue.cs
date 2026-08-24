namespace Phipes.Drive.Core.Photos;

/// <summary>Hand-off point between the indexer and the background thumbnail worker.</summary>
public interface IThumbnailQueue
{
    void Enqueue(string hash);
}

/// <summary>Queue that drops everything; used when thumbnail generation is not wired up (tests).</summary>
public sealed class NullThumbnailQueue : IThumbnailQueue
{
    public static readonly NullThumbnailQueue Instance = new();

    public void Enqueue(string hash) { }
}
