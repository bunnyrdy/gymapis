namespace GymApis.Services.Storage;

public record PhotoSaveResult(bool Succeeded, string? Url, string? Error);

/// <summary>
/// Which subject a photo belongs to, and therefore which folder under wwwroot
/// it lands in.
///
/// SECURITY: this is an enum rather than a string parameter on purpose. The
/// folder name is chosen from a closed set inside PhotoStorage, so no
/// caller-supplied text can ever reach a filesystem path — the same property
/// the generated filename gives us for the other half of the path.
/// </summary>
public enum PhotoFolder
{
    Staff,
    Member,

    /// <summary>Hero, website logo and login artwork — the site_settings row.</summary>
    Site,

    /// <summary>The About collage and the Gallery (`site_media`).</summary>
    Gallery,

    /// <summary>Before/after pairs. See the DPDP note on SiteTransformation.</summary>
    Transformation,

    /// <summary>Event cover images.</summary>
    Event,
}

public interface IPhotoStorage
{
    Task<PhotoSaveResult> SaveAsync(IFormFile file, PhotoFolder folder, CancellationToken ct);

    /// <summary>
    /// Stores a video for the gallery or the hero.
    ///
    /// Deliberately a separate method rather than a wider <see cref="SaveAsync"/>:
    /// videos have their own magic-byte table and their own (much larger) size
    /// ceiling, and every existing caller of SaveAsync means "a photo, and
    /// nothing but a photo". Widening the shared method would silently let a
    /// 32 MB MP4 through the staff avatar endpoint.
    /// </summary>
    Task<PhotoSaveResult> SaveVideoAsync(IFormFile file, PhotoFolder folder, CancellationToken ct);

    /// <summary>Best-effort removal of a previously stored photo. Never throws.</summary>
    void Delete(string? url, PhotoFolder folder);
}
