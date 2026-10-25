using MapCue.Domain.Secrets;

namespace MapCue.Application.Ports;

/// <summary>Resolves <c>secret://env/NAME</c> references at execution time. Implementations register resolved values with the redactor.</summary>
public interface ISecretResolver
{
    SecretValue Resolve(SecretRef reference);
}
