using System.Text;
using SmartPrint.Api.Domain;
namespace SmartPrint.Api.Storage;
public sealed class PlatformAdCreativeStorage
{
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    public PlatformAdCreativeStorage(IConfiguration configuration, IWebHostEnvironment environment)
    { this.configuration = configuration; this.environment = environment; }

    public async Task<StoredAdCreative> StoreAsync(Guid id, string name, string contentType, Stream input, long size, CancellationToken ct)
    {
        if (id == Guid.Empty || size is <= 0 or > PlatformAdvertisingRules.MaximumCreativeBytes)
            throw new InvalidDataException("Choose an image no larger than 3 MB.");
        var type = contentType.Trim().ToLowerInvariant();
        var ext = type switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => throw new InvalidDataException("Use a JPG, PNG or WebP image.") };
        await using var memory = new MemoryStream(checked((int)size));
        await input.CopyToAsync(memory, ct);
        if (memory.Length != size || memory.Length > PlatformAdvertisingRules.MaximumCreativeBytes)
            throw new InvalidDataException("The image upload is incomplete or exceeds 3 MB.");
        var bytes = memory.ToArray();
        var valid = type switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/webp" => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes,0,4) == "RIFF" && Encoding.ASCII.GetString(bytes,8,4) == "WEBP",
            _ => false
        };
        if (!valid) { Array.Clear(bytes); throw new InvalidDataException("The file contents do not match the selected image type."); }
        var key = $"ad-creatives/{id:N}{ext}";
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(bytes, ct); await output.FlushAsync(ct);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        finally { Array.Clear(bytes); }
        var safeName = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "creative" + ext;
        if (safeName.Length > 255) safeName = safeName[^255..];
        return new StoredAdCreative(id, safeName, type, key, size);
    }
    public FileStream OpenRead(PlatformAdCreative item)
    {
        if (!item.StorageKey.StartsWith("ad-creatives/", StringComparison.Ordinal) || item.StorageKey.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid ad creative storage key.");
        return new FileStream(Resolve(item.StorageKey), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public Task DeleteAsync(PlatformAdCreative item)
    { var p=Resolve(item.StorageKey); if (File.Exists(p)) File.Delete(p); return Task.CompletedTask; }
    private string Resolve(string key)
    {
        var configured=configuration["Documents:StorageRoot"];
        var root=Path.GetFullPath(string.IsNullOrWhiteSpace(configured)?Path.Combine(environment.ContentRootPath,"App_Data","documents"):configured);
        var path=Path.GetFullPath(Path.Combine(root,key.Replace('/',Path.DirectorySeparatorChar)));
        var prefix=root.EndsWith(Path.DirectorySeparatorChar)?root:root+Path.DirectorySeparatorChar;
        if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid ad creative storage path.");
        return path;
    }
}
public sealed record StoredAdCreative(Guid Id,string FileName,string ContentType,string StorageKey,long SizeBytes);
