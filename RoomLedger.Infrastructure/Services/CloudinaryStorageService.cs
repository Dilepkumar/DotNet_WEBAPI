using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoomLedger.Application.Common.Interfaces;
using RoomLedger.Infrastructure.Configuration;

namespace RoomLedger.Infrastructure.Services;

public class CloudinaryStorageService : ICloudStorageService
{
    private readonly Cloudinary _cloudinary;
    private readonly ILogger<CloudinaryStorageService> _logger;

    public CloudinaryStorageService(IOptions<CloudinarySettings> opts, ILogger<CloudinaryStorageService> logger)
    {
        _logger = logger;
        var s = opts.Value;
        var account = new Account(s.CloudName, s.ApiKey, s.ApiSecret);
        _cloudinary = new Cloudinary(account) { Api = { Secure = true } };
    }

    public async Task<string> UploadAsync(IFormFile file, string folder)
    {
        await using var stream = file.OpenReadStream();

        var uploadParams = new ImageUploadParams
        {
            File           = new FileDescription(file.FileName, stream),
            Folder         = folder,
            Transformation = new Transformation()
                .Quality("auto")       // auto-compress
                .FetchFormat("auto"),  // serve webp/avif where supported
            Overwrite      = false
        };

        var result = await _cloudinary.UploadAsync(uploadParams);

        if (result.Error != null)
            throw new Exception($"Cloudinary upload failed: {result.Error.Message}");

        _logger.LogInformation("Uploaded to Cloudinary: {Url}", result.SecureUrl);
        return result.SecureUrl.ToString();
    }

    public async Task DeleteAsync(string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId)) return;

        var result = await _cloudinary.DestroyAsync(new DeletionParams(publicId));

        if (result.Result == "ok")
            _logger.LogInformation("Deleted from Cloudinary: {PublicId}", publicId);
        else
            _logger.LogWarning("Cloudinary delete result: {Result} for {PublicId}", result.Result, publicId);
    }

    public string? ExtractPublicId(string? url)
    {
        // Example URL:
        // https://res.cloudinary.com/{cloud}/image/upload/v12345/roomledger/profiles/avatar_1.jpg
        // Public ID = roomledger/profiles/avatar_1  (no extension)

        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.Contains("res.cloudinary.com")) return null;

        var marker = "/upload/";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var afterUpload = url[(idx + marker.Length)..];

        // Strip version segment like "v1234567890/"
        if (afterUpload.StartsWith("v") && afterUpload.Length > 1 && char.IsDigit(afterUpload[1]))
        {
            var slash = afterUpload.IndexOf('/');
            if (slash >= 0) afterUpload = afterUpload[(slash + 1)..];
        }

        // Strip file extension
        var dot = afterUpload.LastIndexOf('.');
        return dot >= 0 ? afterUpload[..dot] : afterUpload;
    }
}
