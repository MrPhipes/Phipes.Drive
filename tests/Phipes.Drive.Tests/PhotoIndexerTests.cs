using System.Text;
using Phipes.Drive.Core.Photos;
using Phipes.Drive.Core.Storage;

namespace Phipes.Drive.Tests;

public sealed class PhotoIndexerTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemStorage _storage;
    private readonly FakePhotoStore _store = new();
    private readonly CountingMetadataReader _metadata = new();
    private readonly PhotoIndexer _indexer;

    public PhotoIndexerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "phipes-photo-tests", Guid.NewGuid().ToString("N"));
        _storage = new FileSystemStorage(new StorageOptions { RootPath = _root });
        _indexer = new PhotoIndexer(_storage, _store, _metadata, NullThumbnailQueue.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Task WriteAsync(string relativePath, string content) =>
        _storage.SaveAsync(relativePath, new MemoryStream(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task Only_media_extensions_are_indexed()
    {
        await WriteAsync("fotos/a.jpg", "imagen");
        await WriteAsync("fotos/clip.MP4", "video");
        await WriteAsync("docs/informe.pdf", "no es media");

        var result = await _indexer.ScanAsync();

        Assert.Equal(2, result.FilesSeen);
        Assert.Equal(2, _store.Items.Count);
        Assert.Contains(_store.Items.Values, i => i is { Name: "clip.MP4", IsVideo: true });
        Assert.DoesNotContain(_store.Items.Values, i => i.Name.EndsWith(".pdf"));
    }

    [Fact]
    public async Task Identical_content_under_two_paths_collapses_into_one_entry()
    {
        await WriteAsync("2026/enero/foto.jpg", "los mismos bytes");
        await WriteAsync("respaldo/copia-de-foto.jpg", "los mismos bytes");
        await WriteAsync("2026/enero/otra.jpg", "bytes distintos");

        var result = await _indexer.ScanAsync();

        Assert.Equal(3, result.FilesSeen);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, _store.Items.Count);
        // Exactly one of the two identical copies made it into the index.
        Assert.Single(_store.Items.Values, i => i.Name is "foto.jpg" or "copia-de-foto.jpg");
    }

    [Fact]
    public async Task Duplicate_is_promoted_when_the_original_path_disappears()
    {
        await WriteAsync("original.jpg", "contenido compartido");
        await WriteAsync("copia.jpg", "contenido compartido");
        await _indexer.ScanAsync();

        var indexedPath = Assert.Single(_store.Items.Values).Path;
        var survivor = indexedPath == "original.jpg" ? "copia.jpg" : "original.jpg";
        File.Delete(Path.Combine(_root, indexedPath));

        await _indexer.ScanAsync();

        var item = Assert.Single(_store.Items.Values);
        Assert.Equal(survivor, item.Path);
    }

    [Fact]
    public async Task Unchanged_files_are_not_reprocessed_on_the_next_scan()
    {
        await WriteAsync("fotos/a.jpg", "uno");
        await WriteAsync("fotos/b.png", "dos");

        var first = await _indexer.ScanAsync();
        Assert.Equal(2, first.Indexed);
        Assert.Equal(2, _metadata.Calls);

        var second = await _indexer.ScanAsync();

        Assert.Equal(2, second.FilesSeen);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, _metadata.Calls); // no file was hashed or parsed again
        Assert.Equal(2, _store.Items.Count);
    }

    [Fact]
    public async Task A_modified_file_replaces_its_previous_entry()
    {
        await WriteAsync("fotos/a.jpg", "version uno");
        await _indexer.ScanAsync();
        var originalHash = Assert.Single(_store.Items.Values).Hash;

        var physical = Path.Combine(_root, "fotos", "a.jpg");
        await File.WriteAllTextAsync(physical, "version dos, mas larga");
        File.SetLastWriteTimeUtc(physical, DateTime.UtcNow.AddMinutes(5));

        var result = await _indexer.ScanAsync();

        Assert.Equal(1, result.Indexed);
        var item = Assert.Single(_store.Items.Values);
        Assert.NotEqual(originalHash, item.Hash);
        Assert.Equal("fotos/a.jpg", item.Path);
    }

    [Fact]
    public async Task Deleted_files_drop_out_of_the_index()
    {
        await WriteAsync("fotos/a.jpg", "uno");
        await WriteAsync("fotos/b.jpg", "dos");
        await _indexer.ScanAsync();

        File.Delete(Path.Combine(_root, "fotos", "b.jpg"));
        var result = await _indexer.ScanAsync();

        Assert.Equal(1, result.Removed);
        Assert.Equal("fotos/a.jpg", Assert.Single(_store.Items.Values).Path);
    }

    [Fact]
    public async Task The_scan_records_its_own_bookkeeping()
    {
        await WriteAsync("fotos/a.jpg", "uno");

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _indexer.ScanAsync();

        Assert.NotNull(_store.State.LastScanUtc);
        Assert.True(_store.State.LastScanUtc >= before);
        Assert.Equal(1, _store.State.LastScanFileCount);
        Assert.Equal(1, _store.State.LastScanIndexedCount);
    }
}
