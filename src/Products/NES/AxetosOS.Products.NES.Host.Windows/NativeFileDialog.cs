using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AxetosOS.Products.NES.Host.Windows;

/// <summary>
/// Provides a small AxetosOS-owned boundary over the native Windows file dialog.
/// </summary>
public static class NativeFileDialog
{
    private const int MaxPathCharacters = 32_768;

    public static string? OpenFile(
        string title,
        string filter,
        string? initialDirectory = null,
        string? defaultExtension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        var fileBuffer = Marshal.AllocHGlobal(MaxPathCharacters * sizeof(char));
        var filterBuffer = Marshal.StringToHGlobalUni(NormalizeFilter(filter));
        var titleBuffer = Marshal.StringToHGlobalUni(title);
        var initialDirectoryBuffer = string.IsNullOrWhiteSpace(initialDirectory)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(initialDirectory);
        var defaultExtensionBuffer = string.IsNullOrWhiteSpace(defaultExtension)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(defaultExtension);

        try
        {
            Marshal.WriteInt16(fileBuffer, 0);

            var request = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                File = fileBuffer,
                MaxFile = MaxPathCharacters,
                Filter = filterBuffer,
                FilterIndex = 1,
                InitialDirectory = initialDirectoryBuffer,
                Title = titleBuffer,
                Flags = OpenFileNameFlags.Explorer |
                        OpenFileNameFlags.FileMustExist |
                        OpenFileNameFlags.PathMustExist |
                        OpenFileNameFlags.NoChangeDirectory,
                DefaultExtension = defaultExtensionBuffer
            };

            if (GetOpenFileNameW(ref request))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }

            var error = CommDlgExtendedError();
            if (error == 0)
            {
                return null;
            }

            throw new Win32Exception(
                unchecked((int)error),
                $"The native file dialog failed with common-dialog error 0x{error:X4}.");
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
            Marshal.FreeHGlobal(titleBuffer);

            if (initialDirectoryBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(initialDirectoryBuffer);
            }

            if (defaultExtensionBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(defaultExtensionBuffer);
            }
        }
    }

    public static string? SaveFile(
        string title,
        string filter,
        string? initialDirectory = null,
        string? defaultExtension = null,
        string? defaultFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        if (defaultFileName is not null && defaultFileName.Length >= MaxPathCharacters)
            throw new ArgumentOutOfRangeException(nameof(defaultFileName));

        var fileBuffer = Marshal.AllocHGlobal(MaxPathCharacters * sizeof(char));
        var filterBuffer = Marshal.StringToHGlobalUni(NormalizeFilter(filter));
        var titleBuffer = Marshal.StringToHGlobalUni(title);
        var initialDirectoryBuffer = string.IsNullOrWhiteSpace(initialDirectory)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(initialDirectory);
        var defaultExtensionBuffer = string.IsNullOrWhiteSpace(defaultExtension)
            ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(defaultExtension);

        try
        {
            Marshal.WriteInt16(fileBuffer, 0);
            if (!string.IsNullOrEmpty(defaultFileName))
            {
                var characters = (defaultFileName + '\0').ToCharArray();
                Marshal.Copy(characters, 0, fileBuffer, characters.Length);
            }

            var request = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                File = fileBuffer,
                MaxFile = MaxPathCharacters,
                Filter = filterBuffer,
                FilterIndex = 1,
                InitialDirectory = initialDirectoryBuffer,
                Title = titleBuffer,
                Flags = OpenFileNameFlags.Explorer |
                        OpenFileNameFlags.PathMustExist |
                        OpenFileNameFlags.NoChangeDirectory |
                        OpenFileNameFlags.OverwritePrompt,
                DefaultExtension = defaultExtensionBuffer
            };

            if (GetSaveFileNameW(ref request))
            {
                return Marshal.PtrToStringUni(fileBuffer);
            }

            var error = CommDlgExtendedError();
            if (error == 0)
            {
                return null;
            }

            throw new Win32Exception(
                unchecked((int)error),
                $"The native save-file dialog failed with common-dialog error 0x{error:X4}.");
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
            Marshal.FreeHGlobal(titleBuffer);

            if (initialDirectoryBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(initialDirectoryBuffer);
            }

            if (defaultExtensionBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(defaultExtensionBuffer);
            }
        }
    }

    private static string NormalizeFilter(string filter)
    {
        var normalized = filter.Replace('|', '\0');
        return normalized.EndsWith("\0\0", StringComparison.Ordinal)
            ? normalized
            : normalized.EndsWith('\0')
                ? normalized + '\0'
                : normalized + "\0\0";
    }

    [Flags]
    private enum OpenFileNameFlags : uint
    {
        PathMustExist = 0x00000800,
        OverwritePrompt = 0x00000002,
        FileMustExist = 0x00001000,
        NoChangeDirectory = 0x00000008,
        Explorer = 0x00080000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public IntPtr Title;
        public OpenFileNameFlags Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedValue;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
}
