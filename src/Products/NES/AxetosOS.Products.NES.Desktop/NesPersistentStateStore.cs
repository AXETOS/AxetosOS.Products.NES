using AxetosOS.Products.NES.VirtualHardware.Loading;
using System.Security.Cryptography;
using System.Text;

namespace AxetosOS.Products.NES.Desktop;

internal static class NesPersistentStateStore
{
    public const string FileExtension = ".axnesstate";
    private const int FormatVersion = 2;
    private const int LegacyFormatVersion = 1;
    private const int MaximumStateFileBytes = 128 * 1024 * 1024;
    private const int MaximumPayloadBytes = 96 * 1024 * 1024;
    private static readonly byte[] Magic = "AXNESF01"u8.ToArray();

    public static string DefaultSaveDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var root = string.IsNullOrWhiteSpace(documents)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : documents;
            return Path.Combine(root, "AxetosOS", "NES", "Save States");
        }
    }

    public static string CreateDefaultFileName(string romName, DateTimeOffset savedAt)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(romName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "NES Game";
        return $"{safeName} - {savedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}{FileExtension}";
    }

    public static NesPersistentStateFile Create(NesDesktopSession session, DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = session.CapturePersistentState();
        return new NesPersistentStateFile(
            savedAt.ToUniversalTime(),
            Path.GetFileName(session.RomPath),
            session.RomPath,
            (byte[])session.RomSha256.Clone(),
            session.Image.MapperNumber,
            session.Image.SubmapperNumber,
            session.RegionSelection,
            state.MachineState,
            state.FramebufferState,
            state.AudioState);
    }

    public static void Save(string path, NesPersistentStateFile state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The save-state path has no parent directory.");
        Directory.CreateDirectory(directory);

        var body = BuildBody(state);
        if (body.Length > MaximumPayloadBytes)
            throw new InvalidDataException("NES save state is too large to persist safely.");
        var checksum = SHA256.HashData(body);

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(body.Length);
                writer.Write(body);
                writer.Write(checksum.Length);
                writer.Write(checksum);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static NesPersistentStateFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("NES save-state file was not found.", fullPath);
        if (info.Length <= 0 || info.Length > MaximumStateFileBytes)
            throw new InvalidDataException("NES save-state file has an invalid size.");

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("This file is not an AxetosOS NES save state.");

        var version = reader.ReadInt32();
        if (version is not (LegacyFormatVersion or FormatVersion))
            throw new NotSupportedException(
                $"NES save-state format {version} is not supported (supported: {LegacyFormatVersion}-{FormatVersion}).");

        var bodyLength = reader.ReadInt32();
        if (bodyLength < 0 || bodyLength > MaximumPayloadBytes || bodyLength > stream.Length - stream.Position)
            throw new InvalidDataException("NES save-state file contains an invalid payload length.");
        var body = reader.ReadBytes(bodyLength);
        if (body.Length != bodyLength) throw new EndOfStreamException("NES save-state file ended unexpectedly.");

        var checksumLength = reader.ReadInt32();
        if (checksumLength != 32 || checksumLength > stream.Length - stream.Position)
            throw new InvalidDataException("NES save-state file contains an invalid checksum.");
        var expectedChecksum = reader.ReadBytes(checksumLength);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("NES save-state file contains trailing data.");

        var actualChecksum = SHA256.HashData(body);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
            throw new InvalidDataException("NES save-state file is corrupt or incomplete (checksum mismatch).");

        return ParseBody(body, version);
    }

    public static bool HashMatches(string romPath, ReadOnlySpan<byte> expectedSha256)
    {
        if (!File.Exists(romPath) || expectedSha256.Length != 32) return false;
        using var stream = File.OpenRead(romPath);
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, expectedSha256);
    }

    private static byte[] BuildBody(NesPersistentStateFile state)
    {
        if (state.RomSha256.Length != 32)
            throw new InvalidDataException("NES save state requires a SHA-256 ROM identity.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(state.SavedAtUtc.UtcDateTime.Ticks);
        writer.Write(state.RomFileName);
        writer.Write(state.OriginalRomPath);
        writer.Write(state.RomSha256.Length);
        writer.Write(state.RomSha256);
        writer.Write(state.MapperNumber);
        writer.Write(state.SubmapperNumber ?? -1);
        writer.Write((int)state.RegionSelection);

        WriteBlob(writer, state.MachineState);

        writer.Write(state.FramebufferState.CompletedFrame);
        WritePixels(writer, state.FramebufferState.RenderPixels);
        WritePixels(writer, state.FramebufferState.CompletedPixels);

        writer.Write(state.AudioState.NextSampleCycle);
        writer.Write(state.AudioState.CurrentDacLevel);
        writer.Flush();
        return stream.ToArray();
    }

    private static NesPersistentStateFile ParseBody(byte[] body, int formatVersion)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var ticks = reader.ReadInt64();
        DateTimeOffset savedAt;
        try
        {
            savedAt = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("NES save-state timestamp is invalid.", exception);
        }

        var romFileName = reader.ReadString();
        var originalRomPath = reader.ReadString();
        if (string.IsNullOrWhiteSpace(romFileName))
            throw new InvalidDataException("NES save state does not identify its ROM file.");

        var hashLength = reader.ReadInt32();
        if (hashLength != 32 || hashLength > stream.Length - stream.Position)
            throw new InvalidDataException("NES save state contains an invalid ROM hash.");
        var romSha256 = reader.ReadBytes(hashLength);
        var mapperNumber = reader.ReadInt32();
        var submapperValue = reader.ReadInt32();
        int? submapperNumber = submapperValue < 0 ? null : submapperValue;

        // Version 1 save states were all created by the original desktop shell,
        // which hard-wired every ROM to the Famicom/NTSC-J motherboard. Preserve
        // those development states by reopening them on that same physical board.
        var regionSelection = NesRegionSelection.NtscJapan;
        if (formatVersion >= FormatVersion)
        {
            var regionValue = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(NesRegionSelection), regionValue))
                throw new InvalidDataException("NES save state contains an invalid motherboard region.");
            regionSelection = (NesRegionSelection)regionValue;
            if (regionSelection == NesRegionSelection.Auto)
                throw new InvalidDataException("NES save state must identify the physical motherboard it was captured from.");
        }

        var machineState = ReadBlob(reader, stream, "machine state");
        var completedFrame = reader.ReadUInt64();
        var renderPixels = ReadPixels(reader, stream, "render framebuffer");
        var completedPixels = ReadPixels(reader, stream, "completed framebuffer");
        if (renderPixels.Length != 256 * 240 || completedPixels.Length != 256 * 240)
            throw new InvalidDataException("NES save-state framebuffer geometry is incompatible.");

        var nextSampleCycle = reader.ReadDouble();
        var currentDacLevel = reader.ReadByte();
        if (stream.Position != stream.Length)
            throw new InvalidDataException("NES save-state payload contains trailing data.");

        return new NesPersistentStateFile(
            savedAt,
            romFileName,
            originalRomPath,
            romSha256,
            mapperNumber,
            submapperNumber,
            regionSelection,
            machineState,
            new NesFramebufferState(renderPixels, completedPixels, completedFrame),
            new NesPcmState(nextSampleCycle, currentDacLevel));
    }

    private static void WriteBlob(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadBlob(BinaryReader reader, Stream stream, string name)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadBytes || length > stream.Length - stream.Position)
            throw new InvalidDataException($"NES save state contains an invalid {name} length.");
        var value = reader.ReadBytes(length);
        if (value.Length != length) throw new EndOfStreamException($"NES save-state {name} ended unexpectedly.");
        return value;
    }

    private static void WritePixels(BinaryWriter writer, uint[] pixels)
    {
        writer.Write(pixels.Length);
        for (var index = 0; index < pixels.Length; index++) writer.Write(pixels[index]);
    }

    private static uint[] ReadPixels(BinaryReader reader, Stream stream, string name)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 256 * 240 || ((long)count * sizeof(uint)) > stream.Length - stream.Position)
            throw new InvalidDataException($"NES save state contains an invalid {name} length.");
        var pixels = new uint[count];
        for (var index = 0; index < count; index++) pixels[index] = reader.ReadUInt32();
        return pixels;
    }
}

internal sealed record NesPersistentStateFile(
    DateTimeOffset SavedAtUtc,
    string RomFileName,
    string OriginalRomPath,
    byte[] RomSha256,
    int MapperNumber,
    int? SubmapperNumber,
    NesRegionSelection RegionSelection,
    byte[] MachineState,
    NesFramebufferState FramebufferState,
    NesPcmState AudioState);
