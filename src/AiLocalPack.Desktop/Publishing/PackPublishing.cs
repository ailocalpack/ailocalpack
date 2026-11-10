using AiLocalPack.Core.Publishing;

namespace AiLocalPack.Desktop.Publishing;

/// <summary>
/// Sends a local pack to the registry (task 60's <see cref="PackPublisher"/>) for the publish flow (task 61). Behind an
/// interface so view models are tested without GitHub; never throws but for cancellation.
/// </summary>
public interface IPackPublishing
{
    /// <summary>The registry repository packs go to (<c>ailocalpack/registry</c>, or <c>AILOCALPACK_REGISTRY</c>).</summary>
    RegistryRepo Registry { get; }

    Task<PublishResult> PublishAsync(PublishRequest request, IProgress<PublishStep> progress, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IPackPublishing"/> over the app's <see cref="PackPublisher"/>.</summary>
internal sealed class PackPublishing(PackPublisher publisher, RegistryRepo registry) : IPackPublishing
{
    public RegistryRepo Registry { get; } = registry;

    public Task<PublishResult> PublishAsync(PublishRequest request, IProgress<PublishStep> progress, CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(request, progress, cancellationToken);
}
