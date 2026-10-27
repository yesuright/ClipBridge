using Xunit;

namespace ClipBridge.Core.Tests;

/// <summary>
/// Tests in this collection read/write process-wide CLIPBRIDGE_* environment variables.
/// Serializing them (and running the collection alone) keeps them independent of the rest
/// of the suite, which is otherwise fully parallel.
/// </summary>
[CollectionDefinition("ClipBridge environment", DisableParallelization = true)]
public class ClipBridgeEnvironmentCollection
{
}
