using MapCue.Application.Ports;
using MapCue.Domain.Secrets;

namespace MapCue.Infrastructure.Artifacts.Secrets;

/// <summary>
/// Resolves <c>secret://env/NAME</c> from the process environment and registers the value with the
/// redactor before returning it, so the value can never be written unredacted afterwards.
/// </summary>
public sealed class EnvironmentSecretResolver(IRedactor redactor, Func<string, string?>? readVariable = null) : ISecretResolver
{
    private readonly Func<string, string?> _readVariable = readVariable ?? Environment.GetEnvironmentVariable;

    public SecretValue Resolve(SecretRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var value = _readVariable(reference.Name);
        if (string.IsNullOrEmpty(value))
        {
            throw new SecretUnavailableException(reference);
        }

        redactor.RegisterSensitiveValue(value);
        return new SecretValue(reference, value);
    }
}

/// <summary>The referenced secret is not available. The message names the reference only.</summary>
public sealed class SecretUnavailableException : Exception
{
    public SecretUnavailableException(SecretRef reference)
        : base($"Secret {reference?.Uri} is not set.") => Reference = reference;

    public SecretUnavailableException()
    {
    }

    public SecretUnavailableException(string message)
        : base(message)
    {
    }

    public SecretUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SecretRef? Reference { get; }
}
