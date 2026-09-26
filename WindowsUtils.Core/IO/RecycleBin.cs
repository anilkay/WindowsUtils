using System.Runtime.InteropServices;

namespace WindowsUtils.Core.IO;

/// <summary>
/// Sends files to the Recycle Bin via the Windows shell (SHFileOperation). Windows-only at runtime.
/// Files that cannot be recycled (larger than the Recycle Bin, or on a network share or
/// removable drive) are never deleted silently: the shell asks the user first.
/// </summary>
/// <remarks>
/// Do not use Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., UIOption.OnlyErrorDialogs,
/// RecycleOption.SendToRecycleBin): it passes FOF_NOCONFIRMATION without FOF_WANTNUKEWARNING,
/// so the shell permanently deletes files it cannot recycle without any prompt.
/// </remarks>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // SHFILEOPSTRUCTW is 1-byte packed on 32-bit Windows (pshpack1.h) and naturally aligned on 64-bit.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct64
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct ShFileOpStruct32
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation64(ref ShFileOpStruct64 operation);

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation32(ref ShFileOpStruct32 operation);

    /// <summary>
    /// Moves <paramref name="path"/> to the Recycle Bin. Returns false when the file could not be
    /// recycled and the user declined the shell's "permanently delete?" prompt (the file is kept).
    /// Throws <see cref="IOException"/> when the shell reports an error.
    /// </summary>
    /// <param name="owner">Window handle that owns any shell dialog, or <see cref="IntPtr.Zero"/>.</param>
    public static bool SendToRecycleBin(string path, IntPtr owner = default)
    {
        // pFrom is a double-null-terminated list; the marshaler adds the final null.
        var from = Path.GetFullPath(path) + "\0";
        const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT;

        int result;
        bool aborted;
        if (Environment.Is64BitProcess)
        {
            var operation = new ShFileOpStruct64 { Hwnd = owner, Func = FO_DELETE, From = from, Flags = flags };
            result = SHFileOperation64(ref operation);
            aborted = operation.AnyOperationsAborted;
        }
        else
        {
            var operation = new ShFileOpStruct32 { Hwnd = owner, Func = FO_DELETE, From = from, Flags = flags };
            result = SHFileOperation32(ref operation);
            aborted = operation.AnyOperationsAborted;
        }

        if (aborted)
            return false;
        if (result != 0)
            throw new IOException($"The shell could not delete the file (error 0x{result:X}).");
        return true;
    }
}
