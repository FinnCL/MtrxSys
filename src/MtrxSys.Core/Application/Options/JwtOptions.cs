using System.ComponentModel.DataAnnotations;

namespace MtrxSys.Core.Application.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = "mtrxsys";

    [Required]
    public string Audience { get; init; } = "mtrxsys-clients";

    [Range(5, 43200)]
    public int AccessTokenMinutes { get; init; } = 60;
}
