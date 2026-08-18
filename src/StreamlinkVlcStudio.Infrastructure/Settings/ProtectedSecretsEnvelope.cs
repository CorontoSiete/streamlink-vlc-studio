namespace StreamlinkVlcStudio.Infrastructure.Settings;

internal sealed class ProtectedSecretsEnvelope
{
    public int Version { get; set; }
    public string Protection { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}
