using Amazon.S3;
using Amazon.S3.Model;

namespace Aiagent.Services.Storage;

public class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3StorageService(IConfiguration config)
    {
        _bucket = config.GetValue<string>("Storage:S3:Bucket") ?? "aiagent-data";
        var region = config.GetValue<string>("Storage:S3:Region") ?? "us-east-1";
        var accessKey = config.GetValue<string>("Storage:S3:AccessKey");
        var secretKey = config.GetValue<string>("Storage:S3:SecretKey");
        var serviceUrl = config.GetValue<string>("Storage:S3:ServiceUrl");

        var cfg = new AmazonS3Config();
        if (!string.IsNullOrEmpty(serviceUrl))
        {
            cfg.ServiceURL = serviceUrl;
            cfg.ForcePathStyle = true;  // برای MinIO
        }
        else
        {
            cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }

        _s3 = !string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey)
            ? new AmazonS3Client(accessKey, secretKey, cfg)
            : new AmazonS3Client(cfg);
    }

    public async Task WriteTextAsync(string rel, string content)
        => await WriteBytesAsync(rel, System.Text.Encoding.UTF8.GetBytes(content));

    public async Task<string> ReadTextAsync(string rel)
    {
        var bytes = await ReadBytesAsync(rel);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async Task<bool> ExistsAsync(string rel)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, rel);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task WriteBytesAsync(string rel, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = rel,
            InputStream = ms
        });
    }

    public async Task<byte[]> ReadBytesAsync(string rel)
    {
        using var resp = await _s3.GetObjectAsync(_bucket, rel);
        using var ms = new MemoryStream();
        await resp.ResponseStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix)
    {
        var result = new List<string>();
        string? token = null;
        do
        {
            var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token
            });
            result.AddRange(resp.S3Objects.Select(o => o.Key));
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token != null);
        return result;
    }

    public async Task DeleteAsync(string rel)
        => await _s3.DeleteObjectAsync(_bucket, rel);
}