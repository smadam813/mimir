using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

/// <summary>
/// Spec §11: service port (localhost only).
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Mimir:Server";

    /// <summary>
    /// The port Kestrel listens on when <c>ASPNETCORE_URLS</c> is not set. Under Compose the
    /// container listens on 8080 and the host publishes this port instead (spec §12).
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; init; } = 6464;
}
