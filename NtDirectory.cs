using System.Runtime.InteropServices;

namespace Fd;

/// <summary>
/// Minimal directory reader on ntdll: one handle per directory, entries returned in large
/// batches. Entries are <c>FILE_FULL_DIR_INFORMATION</c> records, exposed through <see cref="DirectoryRecord"/>.
/// </summary>
static unsafe partial class NtDirectory
{
    const uint ListDirectoryAccess = 0x1 | 0x100000;           // FILE_LIST_DIRECTORY | SYNCHRONIZE
    const uint ShareAll = 0x7;
    const uint DirectoryFileSynchronous = 0x1 | 0x20;           // FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT
    const uint CaseInsensitive = 0x40;
    const int FileIdInformation = 59;
    const int FileFullDirectoryInformation = 2;
    const int FileFsVolumeInformation = 1;
    const int FileFsDeviceInformation = 4;
    const int FileFsAttributeInformation = 5;
    const uint RemoteDevice = 0x10;

    public const int BufferSize = 8192;
    const int RecordHeaderSize = 68;

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString { public ushort Length, MaximumLength; public char* Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQuality;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public nint Status; public nuint Information; }

    [LibraryImport("ntdll.dll")]
    private static partial int NtOpenFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatusBlock* status, uint share, uint options);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryDirectoryFile(nint handle, nint evt, nint apcRoutine, nint apcContext, IoStatusBlock* status,
        byte* buffer, uint length, int infoClass, byte returnSingleEntry, nint fileName, byte restartScan);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryVolumeInformationFile(nint handle, IoStatusBlock* status, byte* information, uint length, int infoClass);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationFile(nint handle, IoStatusBlock* status, byte* information, uint length, int infoClass);

    [LibraryImport("ntdll.dll")]
    public static partial int NtClose(nint handle);

    /// <summary>Opens a directory by its Win32 path. Returns 0 if it cannot be opened.</summary>
    public static nint Open(string path)
    {
        const string DevicePrefix = @"\??\";
        const string UncPrefix = @"\??\UNC\";
        bool verbatim = path.StartsWith(@"\\?\", StringComparison.Ordinal);
        bool unc = !verbatim && path.StartsWith(@"\\", StringComparison.Ordinal);
        ReadOnlySpan<char> prefix = unc ? UncPrefix : DevicePrefix;
        ReadOnlySpan<char> rest = unc ? path.AsSpan(2) : verbatim ? path.AsSpan(4) : path;

        int length = prefix.Length + rest.Length;
        char[]? rented = length > 512 ? new char[length] : null;
        Span<char> name = rented ?? stackalloc char[512];
        prefix.CopyTo(name);
        rest.CopyTo(name[prefix.Length..]);

        fixed (char* buffer = name)
        {
            var unicode = new UnicodeString { Length = (ushort)(length * 2), MaximumLength = (ushort)(length * 2), Buffer = buffer };
            var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes), ObjectName = &unicode, Attributes = CaseInsensitive };
            IoStatusBlock status;
            nint handle;
            return NtOpenFile(&handle, ListDirectoryAccess, &attributes, &status, ShareAll, DirectoryFileSynchronous) >= 0 ? handle : 0;
        }
    }

    /// <summary>
    /// Reads the next batch of entries into <paramref name="buffer"/> (which must hold <see cref="BufferSize"/> bytes).
    /// Returns its size in bytes, or -1 when the directory is exhausted.
    /// </summary>
    public static int ReadBatch(nint handle, byte* buffer, bool restart)
    {
        IoStatusBlock status;
        int result = NtQueryDirectoryFile(handle, 0, 0, 0, &status, buffer, BufferSize, FileFullDirectoryInformation, 0, 0, restart ? (byte)1 : (byte)0);
        return result == 0 && status.Information > 0 ? (int)status.Information : -1;
    }

    /// <summary>Volume serial number and 128-bit file id, which together identify a directory.</summary>
    public static bool TryGetFileId(nint handle, out FileId id)
    {
        IoStatusBlock status;
        ulong* information = stackalloc ulong[3];
        bool ok = NtQueryInformationFile(handle, &status, (byte*)information, 24, FileIdInformation) == 0;
        id = ok ? new FileId(information[0], information[1], information[2]) : default;
        return ok;
    }

    public static uint GetVolumeSerial(nint handle)
    {
        IoStatusBlock status;
        byte* information = stackalloc byte[256];
        return NtQueryVolumeInformationFile(handle, &status, information, 256, FileFsVolumeInformation) >= 0 ? *(uint*)(information + 8) : 0;
    }

    /// <summary>
    /// Local ReFS fills every batch until the next record no longer fits, so a batch that still has room
    /// for the largest possible record was the last one and the final "no more files" query can be skipped.
    /// Returns that record size, or 0 for any other volume: NTFS returns short batches in the middle of a
    /// directory (up to 2 KB of an 8 KB buffer left free), and remote file systems are not known to behave.
    /// </summary>
    public static int LastBatchHeadroom(nint directory)
    {
        IoStatusBlock status;
        uint* device = stackalloc uint[2];
        if (NtQueryVolumeInformationFile(directory, &status, (byte*)device, 8, FileFsDeviceInformation) != 0) return 0;
        if ((device[1] & RemoteDevice) != 0) return 0;

        byte* attributes = stackalloc byte[64];
        if (NtQueryVolumeInformationFile(directory, &status, attributes, 64, FileFsAttributeInformation) != 0) return 0;
        int maxNameLength = *(int*)(attributes + 4);
        var fileSystem = new ReadOnlySpan<char>(attributes + 12, (int)(*(uint*)(attributes + 8) / 2));
        if (!fileSystem.SequenceEqual("ReFS")) return 0;

        return (RecordHeaderSize + 2 * maxNameLength + 7) & ~7;
    }
}

readonly record struct FileId(ulong Volume, ulong Low, ulong High);

/// <summary>View over one <c>FILE_FULL_DIR_INFORMATION</c> record.</summary>
readonly unsafe struct DirectoryRecord(byte* record)
{
    const uint ReparseTagNameSurrogate = 0x20000000;

    public uint NextEntryOffset => *(uint*)record;
    public long LastWriteTime => *(long*)(record + 24);
    public long Length => *(long*)(record + 40);
    public FileAttributes Attributes => (FileAttributes)(*(uint*)(record + 56));
    public ReadOnlySpan<char> Name => new(record + 68, (int)(*(uint*)(record + 60) / 2));
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>
    /// Symbolic links and junctions. Other reparse points (OneDrive placeholders, deduplicated files) are
    /// ordinary files and directories. For a reparse point the <c>EaSize</c> field holds the reparse tag.
    /// </summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0 && (*(uint*)(record + 64) & ReparseTagNameSurrogate) != 0;

    public EntryKinds Kind => IsLink ? EntryKinds.Link : IsDirectory ? EntryKinds.Directory : EntryKinds.File;
}
