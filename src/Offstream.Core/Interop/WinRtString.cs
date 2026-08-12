using System.Runtime.InteropServices;

namespace Offstream.Core.Interop;

/// <summary>
/// Manual HSTRING creation and reading.
/// </summary>
/// <remarks>
/// The reference implementation declared HSTRING parameters as
/// <c>[MarshalAs(UnmanagedType.HString)]</c> and let the runtime do this. That marshalling
/// was removed in .NET 5 along with built-in WinRT support, so on .NET 10 every HSTRING
/// crossing the boundary has to be created, read and freed by hand. This mirrors the
/// pattern Microsoft documents in "Native interoperability best practices".
/// </remarks>
internal static class WinRtString
{
    private const string Lib = "api-ms-win-core-winrt-string-l1-1-0.dll";

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport(Lib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

    /// <summary>Creates an HSTRING. The caller owns it and must pass it to <see cref="Delete"/>.</summary>
    public static IntPtr Create(string value)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(value, value.Length, out var hstring));
        return hstring;
    }

    /// <summary>Reads an HSTRING back into a managed string without taking ownership.</summary>
    public static string? Read(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return null;

        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        if (buffer == IntPtr.Zero) return null;

        return length == 0 ? string.Empty : Marshal.PtrToStringUni(buffer, length);
    }

    public static void Delete(IntPtr hstring)
    {
        // Deliberately ignoring the HRESULT: this runs in finally blocks, where throwing
        // would mask the original failure, and a failed delete leaks at worst.
        if (hstring != IntPtr.Zero) _ = WindowsDeleteString(hstring);
    }
}
