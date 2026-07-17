namespace Linkuity.Infrastructure.Azure;

public class BlobStorageOptions
{
    public required string ConnectionString { get; init; }
    public required string ContainerName { get; init; }
}
