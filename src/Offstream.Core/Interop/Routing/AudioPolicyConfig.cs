using NAudio.CoreAudioApi;
using Offstream.Core.Interop;

namespace Offstream.Core.Interop.Routing;

/// <summary>Uniform access to the undocumented audio policy config, whichever IID answered.</summary>
public interface IAudioPolicyConfig
{
    string Variant { get; }
    int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);
    int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId);
    int ClearAllPersistedApplicationDefaultEndpoints();
}

/// <summary>
/// Activates <c>Windows.Media.Internal.AudioPolicyConfig</c> and binds whichever of the two
/// known IIDs this Windows build actually exposes.
/// </summary>
/// <remarks>
/// <para>
/// The reference implementation selected by OS build number
/// (<c>Environment.OSVersion.IsAtLeast(Version21H2)</c>). That is a proxy for the real
/// question — which IID the factory answers to — so this asks directly and keeps the build
/// number only as the order in which to try. Probing cannot be wrong about a build it has
/// never seen, including builds newer than this code.
/// </para>
/// <para>
/// <b>Offstream targets Windows 11 only</b>, whose floor is build 22000, so the 21H2 IID is
/// the one that answers in practice. The older IID is kept anyway, not for Windows 10 but
/// because Microsoft has already changed this IID once on an interface that appears in no
/// header: a second candidate costs one failed QueryInterface on a path that runs once per
/// session, and buys a chance of surviving the next change.
/// </para>
/// </remarks>
public static class AudioPolicyConfigFactory
{
    private const string RuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>Build at which the IID changed. Below Windows 11's floor; kept as probe order only.</summary>
    private const int Build21H2 = 21390;

    public static IAudioPolicyConfig Create()
    {
        var preferModern = Environment.OSVersion.Version.Build >= Build21H2;

        var errors = new List<string>();

        foreach (var attemptModern in preferModern ? new[] { true, false } : new[] { false, true })
        {
            try
            {
                if (attemptModern) return CreateModern();
                return CreateDownlevel();
            }
            catch (Exception ex)
            {
                errors.Add($"  {(attemptModern ? "21H2" : "downlevel")}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Could not bind IAudioPolicyConfig under either known IID:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static ModernPolicyConfig CreateModern()
    {
        var iid = typeof(IAudioPolicyConfig21H2).GUID;
        var factory = (IAudioPolicyConfig21H2)ComBase.GetActivationFactory(RuntimeClass, iid);
        return new ModernPolicyConfig(factory);
    }

    private static DownlevelPolicyConfig CreateDownlevel()
    {
        var iid = typeof(IAudioPolicyConfigDownlevel).GUID;
        var factory = (IAudioPolicyConfigDownlevel)ComBase.GetActivationFactory(RuntimeClass, iid);
        return new DownlevelPolicyConfig(factory);
    }

    private sealed class ModernPolicyConfig(IAudioPolicyConfig21H2 factory) : IAudioPolicyConfig
    {
        public string Variant => "21H2 (ab3d4648-e242-459f-b02f-541c70306324)";

        public int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId)
            => factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);

        public int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId)
            => factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);

        public int ClearAllPersistedApplicationDefaultEndpoints()
            => factory.ClearAllPersistedApplicationDefaultEndpoints();
    }

    private sealed class DownlevelPolicyConfig(IAudioPolicyConfigDownlevel factory) : IAudioPolicyConfig
    {
        public string Variant => "downlevel (2a59116d-6c4f-45e0-a74f-707e3fef9258)";

        public int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId)
            => factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);

        public int GetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, out IntPtr deviceId)
            => factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);

        public int ClearAllPersistedApplicationDefaultEndpoints()
            => factory.ClearAllPersistedApplicationDefaultEndpoints();
    }
}
