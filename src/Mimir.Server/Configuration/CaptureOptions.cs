using System.ComponentModel.DataAnnotations;

namespace Mimir.Server.Configuration;

public sealed class CaptureOptions : IValidatableObject
{
    public const string SectionName = "Mimir:Capture";

    [Range(1, int.MaxValue)]
    public int PayloadFieldCapBytes { get; init; } = 4096;

    [Range(1, int.MaxValue)]
    public int PayloadHeadBytes { get; init; } = 3072;

    [Range(1, int.MaxValue)]
    public int PayloadTailBytes { get; init; } = 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((long)PayloadHeadBytes + PayloadTailBytes > PayloadFieldCapBytes)
        {
            yield return new ValidationResult(
                $"PayloadHeadBytes ({PayloadHeadBytes}) + PayloadTailBytes ({PayloadTailBytes}) must not "
                + $"exceed PayloadFieldCapBytes ({PayloadFieldCapBytes}); head plus tail is what survives the cap.",
                [nameof(PayloadHeadBytes), nameof(PayloadTailBytes)]);
        }
    }
}
