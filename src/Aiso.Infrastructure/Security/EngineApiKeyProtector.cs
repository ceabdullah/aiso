using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Aiso.Infrastructure.Security;

/// <summary>
/// Encrypts AI engine API keys entered in the dashboard. Only ciphertext and a 4-character hint are stored;
/// plaintext exists in memory in the web app while saving and in the worker while calling the engine.
/// </summary>
public sealed class EngineApiKeyProtector(IDataProtectionProvider provider)
{
    /// <summary>Changing this string makes every stored key unreadable: version it, never edit it.</summary>
    public const string Purpose = "Aiso.AnswerEngine.ApiKey.v1";

    public const int MinLength = 8;
    public const int MaxLength = 512;

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return _protector.Protect(apiKey);
    }

    /// <summary>Returns null when the ciphertext cannot be read (key ring lost or replaced).</summary>
    public string? TryUnprotect(string? ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Last four characters, enough to recognise which key is set without revealing it.</summary>
    public static string Hint(string apiKey) => apiKey.Length <= 4 ? "****" : apiKey[^4..];

    /// <summary>Normalises and validates a pasted key; returns an error message or null.</summary>
    public static string? Validate(string apiKey, out string normalized)
    {
        normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length is < MinLength or > MaxLength)
        {
            return $"API key must be {MinLength}-{MaxLength} characters.";
        }

        return normalized.Any(char.IsWhiteSpace) ? "API key must not contain spaces or line breaks." : null;
    }
}
