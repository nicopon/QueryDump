using Xunit;

namespace DtPipe.Tests;

/// <summary>
/// Tests that redirect Console.Out / Console.Error must not run in parallel with each
/// other: interleaved SetError/SetOut + restore pairs can leave another test's writes
/// going to the wrong sink. Everything touching console redirection joins this
/// serial collection.
/// </summary>
[CollectionDefinition("console-serial", DisableParallelization = true)]
public sealed class ConsoleSerialCollection;
