namespace Aiagent.Services.Storage;

public interface IStorageService
{
    Task WriteTextAsync(string relativePath, string content);
    Task<string> ReadTextAsync(string relativePath);
    Task<bool> ExistsAsync(string relativePath);
    Task WriteBytesAsync(string relativePath, byte[] bytes);
    Task<byte[]> ReadBytesAsync(string relativePath);
    Task<IReadOnlyList<string>> ListAsync(string prefix);
    Task DeleteAsync(string relativePath);
}