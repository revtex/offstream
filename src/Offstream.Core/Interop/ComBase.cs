using System.Runtime.InteropServices;

namespace Offstream.Core.Interop;

/// <summary>Interop for combase.dll / the WinRT activation surface.</summary>
internal static class ComBase
{
    /// <summary>
    /// Gets the activation factory for a runtime class.
    /// </summary>
    /// <remarks>
    /// The reference implementation used
    /// <c>[MarshalAs(UnmanagedType.HString)] string</c> for the class id and
    /// <c>[Out, MarshalAs(UnmanagedType.IInspectable)] out object</c> for the factory.
    /// Both of those marshallers were removed in .NET 5. The class id is therefore passed
    /// as a hand-built HSTRING, and the factory comes back as <c>IUnknown</c> — which is
    /// what Microsoft's own migration example does.
    /// </remarks>
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object factory);

    /// <summary>
    /// Activates <paramref name="runtimeClassName"/> and returns its factory as the
    /// interface identified by <paramref name="iid"/>.
    /// </summary>
    public static object GetActivationFactory(string runtimeClassName, Guid iid)
    {
        var classId = WinRtString.Create(runtimeClassName);
        try
        {
            var hr = RoGetActivationFactory(classId, ref iid, out var factory);
            Marshal.ThrowExceptionForHR(hr);
            return factory;
        }
        finally
        {
            WinRtString.Delete(classId);
        }
    }
}
