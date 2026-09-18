using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.Identity.Infrastructure.Configuration;

namespace Tebrazi.Identity.Infrastructure.Security;

/// <summary>
/// Issues HS256 tokens whose payload is byte-identical in shape to the Node backend's:
/// <c>{ id, email, role, userType, displayName, organizationId, iat, exp }</c>.
///
/// The claims are written through <see cref="SecurityTokenDescriptor.Claims"/> rather than a
/// ClaimsIdentity on purpose: an identity would rewrite the short names into WS-Federation URIs
/// and produce a token the Node backend cannot read.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenGenerator(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _options.Validate();

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTime ExpiresAtUtc) Generate(User user, string? organizationId)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(_options.Lifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                ["id"] = user.Id,
                // A null email must serialize as JSON null, not be omitted: the Node payload
                // always carries the key, and phone-first patients have no email.
                ["email"] = user.Email!,
                ["role"] = user.Role.ToString(),
                ["userType"] = user.UserType.ToString(),
                ["displayName"] = user.DisplayName,
                ["organizationId"] = organizationId!
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = _credentials
        };

        if (!string.IsNullOrWhiteSpace(_options.Issuer)) descriptor.Issuer = _options.Issuer;
        if (!string.IsNullOrWhiteSpace(_options.Audience)) descriptor.Audience = _options.Audience;

        return (_handler.CreateToken(descriptor), expires);
    }
}

/// <summary>
/// Opaque tokens for password reset, invitations and OTPs, drawn from the OS CSPRNG.
/// Equivalent to Node's <c>crypto.randomBytes(32).toString('hex')</c>.
/// </summary>
public sealed class SecureTokenGenerator : ISecureTokenGenerator
{
    public string CreateToken(int byteLength = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 16);
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteLength));
    }

    public string CreateNumericCode(int digits = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);

        // Rejection-free range draw: GetInt32 is uniform over [min, max), so no modulo bias.
        var min = (int)Math.Pow(10, digits - 1);
        var max = (int)Math.Pow(10, digits);
        return RandomNumberGenerator.GetInt32(min, max).ToString();
    }
}
