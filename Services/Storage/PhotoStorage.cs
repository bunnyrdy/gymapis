namespace GymApis.Services.Storage;

/// <summary>
/// Stores staff and member photos under wwwroot/uploads/{staff,members}.
///
/// SECURITY — every rule here exists because of a specific attack:
///
///  * MAGIC BYTES, not Content-Type or the file extension. Both of those are
///    attacker-controlled strings. The first bytes of the file are the only
///    evidence of what it actually is.
///  * SVG IS NOT ALLOWED. An SVG is an XML document that can carry &lt;script&gt;,
///    so an "image" upload becomes stored XSS the moment a browser renders it
///    from our origin. Raster formats only.
///  * GENERATED FILENAME. The client's filename never touches the disk path.
///    "../../appsettings.json" and "shell.aspx" both stop being interesting
///    when the name is a GUID and the extension comes from the sniffed type.
///  * SIZE CAP, enforced before reading the stream into anything.
///  * X-Content-Type-Options: nosniff is set on the static-file pipeline in
///    Program.cs, so even a polyglot file cannot be re-interpreted as HTML.
///
/// The SPA now standardises every upload to JPEG before it leaves the browser
/// (gym_frontend/src/utils/image.ts: resize to 1024px, quality ~0.82). That is a
/// bandwidth and storage decision and NOTHING here may lean on it. Anything
/// holding a token can POST multipart straight at this endpoint without going
/// near that code, so the magic-byte table below stays exactly as strict as it
/// was, and still accepts PNG and WebP.
///
/// ponytail: no re-encode. Decoding and re-writing the pixels would neutralise
/// polyglots outright, but the .NET options are ImageSharp (Six Labors split
/// licence — not free for commercial use, and this ships to a paying client) or
/// SkiaSharp (large native dependency). Magic-byte sniffing plus nosniff plus a
/// generated name closes the practical paths. Revisit if photos are ever served
/// from the same origin as authenticated HTML without nosniff.
/// </summary>
public class PhotoStorage : IPhotoStorage
{
    private const long MaxBytes = 2 * 1024 * 1024;   // 2 MB

    /// <summary>
    /// Videos get their own ceiling. A gym walkthrough is not a 2 MB file, and
    /// the alternative — raising MaxBytes for everything — would let a 32 MB
    /// "avatar" through the staff endpoint.
    /// </summary>
    private const long MaxVideoBytes = 32 * 1024 * 1024;   // 32 MB

    /// <summary>
    /// The closed set of destinations. Callers pass a PhotoFolder, never a
    /// string, so nothing an attacker controls can influence the directory.
    /// </summary>
    private static string DirectoryFor(PhotoFolder folder) => folder switch
    {
        PhotoFolder.Staff          => "uploads/staff",
        PhotoFolder.Member         => "uploads/members",
        PhotoFolder.Site           => "uploads/site",
        PhotoFolder.Gallery        => "uploads/gallery",
        PhotoFolder.Transformation => "uploads/transformations",
        PhotoFolder.Event          => "uploads/events",
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    // Signature → canonical extension. Nothing outside this table is accepted.
    private static readonly (byte[] Magic, string Extension, string ContentType)[] Allowed =
    [
        ([0xFF, 0xD8, 0xFF],                            ".jpg",  "image/jpeg"),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], ".png",  "image/png"),
        ([0x52, 0x49, 0x46, 0x46],                      ".webp", "image/webp"),  // RIFF....WEBP
    ];

    /// <summary>
    /// Video signatures. Two containers, and that is the whole list:
    ///
    ///  * MP4 — the magic is not at offset 0. An MP4 begins with a 4-byte box
    ///    length followed by the fourcc 'ftyp' at offset 4, so this entry is
    ///    matched at that offset rather than the start of the file.
    ///  * WebM — an EBML header, 1A 45 DF A3, at offset 0.
    ///
    /// No MOV, no AVI, no MKV: the browser would not play half of them, and
    /// every extra container is another parser we are asking a phone to open.
    /// </summary>
    private static readonly (byte[] Magic, int Offset, string Extension)[] AllowedVideo =
    [
        ([0x66, 0x74, 0x79, 0x70],       4, ".mp4"),   // 'ftyp'
        ([0x1A, 0x45, 0xDF, 0xA3],       0, ".webm"),  // EBML
    ];

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PhotoStorage> _log;

    public PhotoStorage(IWebHostEnvironment env, ILogger<PhotoStorage> log)
    {
        _env = env;
        _log = log;
    }

    public async Task<PhotoSaveResult> SaveAsync(IFormFile file, PhotoFolder folder, CancellationToken ct)
    {
        if (file.Length == 0)
            return new(false, null, "The uploaded file is empty.");
        if (file.Length > MaxBytes)
            return new(false, null, "Photo must be 2 MB or smaller.");

        await using var upload = file.OpenReadStream();

        var header = new byte[12];
        var read = await upload.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

        var match = Allowed.FirstOrDefault(a =>
            read >= a.Magic.Length && header.Take(a.Magic.Length).SequenceEqual(a.Magic));

        if (match.Extension is null)
            return new(false, null, "Photo must be a JPEG, PNG or WebP image.");

        // RIFF is also AVI and WAV — confirm the WEBP fourcc at offset 8.
        if (match.Extension == ".webp" &&
            (read < 12 || header[8] != 'W' || header[9] != 'E' || header[10] != 'B' || header[11] != 'P'))
            return new(false, null, "Photo must be a JPEG, PNG or WebP image.");

        var relativeDir = DirectoryFor(folder);
        var directory = Path.Combine(_env.WebRootPath, relativeDir);
        Directory.CreateDirectory(directory);

        // The client's filename is never used — no traversal, no double extension.
        var fileName = $"{Guid.NewGuid():N}{match.Extension}";

        await using (var target = File.Create(Path.Combine(directory, fileName)))
        {
            upload.Position = 0;
            await upload.CopyToAsync(target, ct);
        }

        return new(true, $"/{relativeDir}/{fileName}", null);
    }

    public async Task<PhotoSaveResult> SaveVideoAsync(IFormFile file, PhotoFolder folder, CancellationToken ct)
    {
        if (file.Length == 0)
            return new(false, null, "The uploaded file is empty.");
        if (file.Length > MaxVideoBytes)
            return new(false, null, "Video must be 32 MB or smaller.");

        await using var upload = file.OpenReadStream();

        var header = new byte[12];
        var read = await upload.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);

        var match = AllowedVideo.FirstOrDefault(a =>
            read >= a.Offset + a.Magic.Length &&
            header.Skip(a.Offset).Take(a.Magic.Length).SequenceEqual(a.Magic));

        if (match.Extension is null)
            return new(false, null, "Video must be an MP4 or WebM file.");

        var relativeDir = DirectoryFor(folder);
        var directory = Path.Combine(_env.WebRootPath, relativeDir);
        Directory.CreateDirectory(directory);

        var fileName = $"{Guid.NewGuid():N}{match.Extension}";

        await using (var target = File.Create(Path.Combine(directory, fileName)))
        {
            upload.Position = 0;
            await upload.CopyToAsync(target, ct);
        }

        return new(true, $"/{relativeDir}/{fileName}", null);
    }

    public void Delete(string? url, PhotoFolder folder)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var name = Path.GetFileName(url);
            // Re-derive the path from the bare filename so a stored value like
            // "/uploads/staff/../../appsettings.json" cannot escape the folder.
            var path = Path.Combine(_env.WebRootPath, DirectoryFor(folder), name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // An orphaned file is a housekeeping problem, not a request failure.
            _log.LogWarning(ex, "Could not delete {Folder} photo {Url}.", folder, url);
        }
    }
}
