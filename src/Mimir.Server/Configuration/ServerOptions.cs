using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Mimir:Server";

    [Range(1, 65535)]
    public int Port { get; init; } = 6464;
}
