using Microsoft.AspNetCore.Http;

namespace RoomLedger.Application.Common.Interfaces;

public interface ICloudStorageService
{
    /// <summary>Upload file to Cloudinary. Returns full CDN URL.</summary>
    Task<string> UploadAsync(IFormFile file, string folder);

    /// <summary>Delete a file from Cloudinary by its public ID.</summary>
    Task DeleteAsync(string publicId);

    /// <summary>
    /// Extract Cloudinary public ID from a full URL.
    /// E.g. "https://res.cloudinary.com/xyz/image/upload/roomledger/profiles/avatar_1"
    ///   → returns "roomledger/profiles/avatar_1"
    /// Returns null if URL is not a Cloudinary URL.
    /// </summary>
    string? ExtractPublicId(string? url);
}
