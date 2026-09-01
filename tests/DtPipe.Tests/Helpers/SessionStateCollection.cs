using Xunit;

namespace DtPipe.Tests.Helpers;

/// <summary>
/// Serialises the test classes that redirect DTPIPE_STATE_HOME.
///
/// An environment variable is process-global, so two classes setting it in parallel clobber
/// each other and one of them reads the other's keys. That failure is intermittent and reads
/// like a bug in the store rather than in the test, which is the worst kind — so the classes
/// share a collection and run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SessionStateCollection
{
    public const string Name = "session-state";
}
