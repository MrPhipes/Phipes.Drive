using Phipes.Drive.Core.Storage;

namespace Phipes.Drive.Tests;

public sealed class FileSystemStorageTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemStorage _storage;

    public FileSystemStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "phipes-drive-tests", Guid.NewGuid().ToString("N"));
        _storage = new FileSystemStorage(new StorageOptions { RootPath = _root });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_then_list_and_read_roundtrip()
    {
        await _storage.SaveAsync("docs/hola.txt", Bytes("hola phipes"));

        var rootItems = await _storage.ListAsync("");
        var folder = Assert.Single(rootItems);
        Assert.True(folder.IsFolder);
        Assert.Equal("docs", folder.Name);

        var docs = await _storage.ListAsync("docs");
        var file = Assert.Single(docs);
        Assert.False(file.IsFolder);
        Assert.Equal("docs/hola.txt", file.RelativePath);

        await using var stream = await _storage.OpenReadAsync("docs/hola.txt");
        using var reader = new StreamReader(stream);
        Assert.Equal("hola phipes", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("docs/../../fuera.txt")]
    [InlineData("..\\fuera.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\system.ini")]
    public async Task Paths_escaping_the_root_are_rejected(string malicious)
    {
        await Assert.ThrowsAsync<StoragePathException>(() => _storage.SaveAsync(malicious, Bytes("x")));
        await Assert.ThrowsAsync<StoragePathException>(() => _storage.OpenReadAsync(malicious));
        await Assert.ThrowsAsync<StoragePathException>(() => _storage.MoveToTrashAsync(malicious));
    }

    [Fact]
    public async Task Trash_folder_is_hidden_and_unreachable()
    {
        await _storage.SaveAsync("a.txt", Bytes("x"));
        var items = await _storage.ListAsync("");
        Assert.DoesNotContain(items, i => i.Name == ".trash");
        await Assert.ThrowsAsync<StoragePathException>(() => _storage.ListAsync(".trash"));
    }

    [Fact]
    public async Task Move_to_trash_and_restore_roundtrip()
    {
        await _storage.SaveAsync("carpeta/informe.pdf", Bytes("contenido"));

        var id = await _storage.MoveToTrashAsync("carpeta/informe.pdf");
        Assert.Empty(await _storage.ListAsync("carpeta"));

        var entry = Assert.Single(await _storage.ListTrashAsync());
        Assert.Equal(id, entry.Id);
        Assert.Equal("carpeta/informe.pdf", entry.OriginalPath);

        await _storage.RestoreAsync(id);
        var restored = Assert.Single(await _storage.ListAsync("carpeta"));
        Assert.Equal("informe.pdf", restored.Name);
        Assert.Empty(await _storage.ListTrashAsync());
    }

    [Fact]
    public async Task Quota_blocks_uploads_that_exceed_it()
    {
        var limited = new FileSystemStorage(new StorageOptions
        {
            RootPath = Path.Combine(_root, "limited"),
            QuotaBytes = 10,
        });

        await limited.SaveAsync("ok.txt", Bytes("12345"));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(
            () => limited.SaveAsync("big.txt", Bytes("123456789012345")));
    }

    [Fact]
    public async Task Usage_reports_stored_bytes()
    {
        await _storage.SaveAsync("uno.bin", Bytes("12345"));
        await _storage.SaveAsync("sub/dos.bin", Bytes("1234567890"));
        var usage = await _storage.GetUsageAsync();
        Assert.Equal(15, usage.UsedBytes);
        Assert.Null(usage.QuotaBytes);
    }
}
