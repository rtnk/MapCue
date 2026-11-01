using System.Globalization;
using System.Text.Json;
using MapCue.Application.Ports;
using MapCue.Contracts.Schemas;
using MapCue.Contracts.ScreenContracts;
using MapCue.Domain.Graph;

namespace MapCue.Application.ScreenContracts;

/// <summary>A published contract: its version, whether this publish created that version, and the file written.</summary>
public sealed record PublishedContract(ScreenContractDocument Contract, bool NewVersion, string RelativePath);

/// <summary>A built contract does not match <c>schemas/screen-contract.v1.schema.json</c> (a bug: never written).</summary>
public sealed class ScreenContractSchemaException(string message) : Exception(message);

/// <summary>
/// Versions and writes screen contracts (architecture §6, §12): a contract gets a new version only when its content hash differs from the
/// latest stored version of the same screen and group. Every contract is validated against the schema before anything is stored, stored as a
/// redacted artifact, and written (as stored) to <c>screens/&lt;screenKey&gt;/&lt;groupId&gt;/screen-contract.v&lt;k&gt;.json</c>.
/// </summary>
public static class ScreenContractPublisher
{
    public const string ScreensDirectoryName = "screens";

    public static async Task<IReadOnlyList<PublishedContract>> PublishAsync(
        IStateStore store,
        IArtifactStore artifacts,
        IClock clock,
        IReadOnlyList<ScreenContractDocument> contracts,
        string workspace,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(workspace);

        var result = new List<PublishedContract>();
        foreach (var built in contracts)
        {
            var latest = await store.InTransactionAsync(uow => uow.Contracts.GetLatestContractAsync(built.ScreenId, built.Group.GroupKey, ct), ct);
            var isNew = latest is null || latest.ContentHash != built.ContentHash;
            var contract = built with { Version = isNew ? (latest?.Version ?? 0) + 1 : latest!.Version };
            var json = ScreenContractJson.Serialize(contract);
            using (var document = JsonDocument.Parse(json))
            {
                var errors = ContractSchemas.ScreenContract.Validate(document.RootElement);
                if (errors.Count > 0)
                {
                    throw new ScreenContractSchemaException($"Contract of {contract.ScreenKey} does not match the schema: {string.Join("; ", errors)}");
                }
            }

            var artifact = await artifacts.PutTextAsync(json, ArtifactKind.ScreenContract, ct);
            if (isNew)
            {
                await store.InTransactionAsync(
                    async uow =>
                    {
                        await uow.Contracts.InsertContractAsync(
                            new ScreenContractVersion(ScreenContractVersion.IdOf(contract.ScreenId, contract.Group.GroupKey, contract.Version), contract.ScreenId, contract.Group.GroupKey, contract.Version, contract.ContentHash, artifact.Ref.Sha256, clock.UtcNow),
                            ct);
                        return 0;
                    },
                    ct);
            }

            var relative = Path.Combine(ScreensDirectoryName, contract.ScreenKey, ScreenContractBuilder.GroupIdOf(contract.Group.GroupKey), $"screen-contract.v{contract.Version.ToString(CultureInfo.InvariantCulture)}.json");
            var path = Path.Combine(workspace, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, artifact.RedactedText ?? json, ct);
            result.Add(new PublishedContract(contract, isNew, relative.Replace('\\', '/')));
        }

        return result;
    }
}
