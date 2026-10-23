using System.Runtime.InteropServices;

namespace Fd;

static partial class Terminal
{
    const int StdOutputHandle = -11;
    const uint EnableVirtualTerminalProcessing = 0x4;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    /// <summary>Turns on ANSI escape sequence processing for the console. Returns false if stdout is not a console that supports it.</summary>
    public static bool TryEnableAnsi()
    {
        nint handle = GetStdHandle(StdOutputHandle);
        return GetConsoleMode(handle, out uint mode) && ((mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing));
    }
}
