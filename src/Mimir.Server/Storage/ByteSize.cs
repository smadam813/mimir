namespace Mimir.Server.Storage;

/// <summary>Renders a byte count for the Storage tile face.</summary>
internal static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        var unit = 0;
        double value = bytes;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole bytes never want a decimal point; everything else reads better with one.
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {Units[unit]}";
    }
}
