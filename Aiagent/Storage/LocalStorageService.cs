namespace Aiagent.Services.Storage;

public class LocalStorageService : IStorageService
{
    private readonly string _root;

    public LocalStorageService(IConfiguration config)
    {
        _root = config.GetValue<string>("Storage:LocalRoot")
                ?? Path.Combine(AppContext.BaseDirectory, "_data");
        Directory.CreateDirectory(_root);
    }

    private string FullPath(string rel) =>
        Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    public async Task WriteTextAsync(string rel, string content)
    {
        var p = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, content);
    }

    public async Task<string> ReadTextAsync(string rel)
        => await File.ReadAllTextAsync(FullPath(rel));

    public Task<bool> ExistsAsync(string rel)
        => Task.FromResult(File.Exists(FullPath(rel)));

    public async Task WriteBytesAsync(string rel, byte[] bytes)
    {
        var p = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, bytes);
    }

    public async Task<byte[]> ReadBytesAsync(string rel)
        => await File.ReadAllBytesAsync(FullPath(rel));

    public Task<IReadOnlyList<string>> ListAsync(string prefix)
    {
        var dir = FullPath(prefix);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task DeleteAsync(string rel)
    {
        var p = FullPath(rel);
        if (File.Exists(p)) File.Delete(p);
        return Task.CompletedTask;
    }
}