using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Offstream.Core.Interop.Routing;

/// <summary>
/// The undocumented <c>IAudioPolicyConfig</c> interface, as exposed by
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>, on Windows 21H2 and later.
/// </summary>
/// <remarks>
/// <para>
/// This is the asset the whole retarget exists to preserve: per-application audio
/// routing. It is not in any SDK header, so the vtable order below is the contract.
/// </para>
/// <para>
/// <b>Why this is not marked <c>InterfaceIsIInspectable</c> like the reference
/// implementation:</b> since .NET 5, casting an RCW to an interface declared that way
/// throws <see cref="PlatformNotSupportedException"/> at cast time — built-in WinRT
/// support was removed from the runtime. The interface is therefore declared as
/// IUnknown-based, with <c>IInspectable</c>'s three methods written out explicitly so the
/// vtable offsets still line up. Slot layout:
/// </para>
/// <list type="bullet">
///   <item>0-2 — IUnknown (implicit)</item>
///   <item>3-5 — IInspectable, declared below</item>
///   <item>6-24 — nineteen methods this app never calls, declared as reserved slots</item>
///   <item>25-27 — the three that matter</item>
/// </list>
/// <para>
/// Every method is <c>[PreserveSig]</c>: the reserved slots have no real signature, so the
/// runtime must not try to interpret their return values as HRESULTs to throw on.
/// </para>
/// </remarks>
// CA1707: the reserved members below are named ReservedNN_OriginalName on purpose. NN is
// the vtable slot index, and this interface has no header anywhere - the numbering *is* the
// documentation. Renaming them to satisfy a style rule would delete the only record of which
// slot each method occupies, and a wrong slot silently calls the wrong native function.
#pragma warning disable CA1707

[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioPolicyConfig21H2
{
    // --- IInspectable (slots 3-5) ---
    [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    // --- Reserved (slots 6-24). Order is load-bearing; do not add, remove or reorder. ---
    [PreserveSig] int Reserved06_AddCtxVolumeChange();
    [PreserveSig] int Reserved07_RemoveCtxVolumeChanged();
    [PreserveSig] int Reserved08_AddRingerVibrateStateChanged();
    [PreserveSig] int Reserved09_RemoveRingerVibrateStateChange();
    [PreserveSig] int Reserved10_SetVolumeGroupGainForId();
    [PreserveSig] int Reserved11_GetVolumeGroupGainForId();
    [PreserveSig] int Reserved12_GetActiveVolumeGroupForEndpointId();
    [PreserveSig] int Reserved13_GetVolumeGroupsForEndpoint();
    [PreserveSig] int Reserved14_GetCurrentVolumeContext();
    [PreserveSig] int Reserved15_SetVolumeGroupMuteForId();
    [PreserveSig] int Reserved16_GetVolumeGroupMuteForId();
    [PreserveSig] int Reserved17_SetRingerVibrateState();
    [PreserveSig] int Reserved18_GetRingerVibrateState();
    [PreserveSig] int Reserved19_SetPreferredChatApplication();
    [PreserveSig] int Reserved20_ResetPreferredChatApplication();
    [PreserveSig] int Reserved21_GetPreferredChatApplication();
    [PreserveSig] int Reserved22_GetCurrentChatApplications();
    [PreserveSig] int Reserved23_AddChatContextChanged();
    [PreserveSig] int Reserved24_RemoveChatContextChanged();

    // --- The three that matter (slots 25-27) ---

    /// <param name="deviceId">An HSTRING, or <see cref="IntPtr.Zero"/> to clear the override.</param>
    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);

    /// <param name="deviceId">Receives an HSTRING owned by the caller.</param>
    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

/// <summary>
/// The same interface on Windows builds before 21H2.
/// </summary>
/// <remarks>
/// <b>Finding:</b> the reference implementation carries two interface declarations and the
/// project describes them as differing vtable layouts. They do not. Diffing the two files
/// shows the <em>only</em> difference is the IID — the method order is byte-identical.
/// What actually varies across Windows builds is which IID the activation factory answers
/// to, so the correct fix is to try one IID and fall back to the other. That is what
/// <see cref="AudioPolicyConfigFactory"/> does.
/// </remarks>
[ComImport]
[Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioPolicyConfigDownlevel
{
    [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr className);
    [PreserveSig] int GetTrustLevel(out int trustLevel);

    [PreserveSig] int Reserved06_AddCtxVolumeChange();
    [PreserveSig] int Reserved07_RemoveCtxVolumeChanged();
    [PreserveSig] int Reserved08_AddRingerVibrateStateChanged();
    [PreserveSig] int Reserved09_RemoveRingerVibrateStateChange();
    [PreserveSig] int Reserved10_SetVolumeGroupGainForId();
    [PreserveSig] int Reserved11_GetVolumeGroupGainForId();
    [PreserveSig] int Reserved12_GetActiveVolumeGroupForEndpointId();
    [PreserveSig] int Reserved13_GetVolumeGroupsForEndpoint();
    [PreserveSig] int Reserved14_GetCurrentVolumeContext();
    [PreserveSig] int Reserved15_SetVolumeGroupMuteForId();
    [PreserveSig] int Reserved16_GetVolumeGroupMuteForId();
    [PreserveSig] int Reserved17_SetRingerVibrateState();
    [PreserveSig] int Reserved18_GetRingerVibrateState();
    [PreserveSig] int Reserved19_SetPreferredChatApplication();
    [PreserveSig] int Reserved20_ResetPreferredChatApplication();
    [PreserveSig] int Reserved21_GetPreferredChatApplication();
    [PreserveSig] int Reserved22_GetCurrentChatApplications();
    [PreserveSig] int Reserved23_AddChatContextChanged();
    [PreserveSig] int Reserved24_RemoveChatContextChanged();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);

    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

#pragma warning restore CA1707
