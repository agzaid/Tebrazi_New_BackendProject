namespace Tebrazi.Identity.Application.UseCases.Commands.SharedPhone;

/// <summary>
/// Node normalizes the same way in all three OTP-flow handlers
/// (<c>phone.replace(/[\s\-]/g, '')</c>, auth.js:1018/1061/1124). One shared helper so the
/// three callers cannot drift.
/// </summary>
public static class PhoneNormalizer
{
    public static string Normalize(string phone) => phone.Replace(" ", "").Replace("-", "");
}
