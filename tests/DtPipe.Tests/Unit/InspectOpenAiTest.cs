using System;
using Xunit;
using DtPipe.Cli.Agent;

namespace DtPipe.Tests.Unit;

public class InspectOpenAiTest
{
    [Fact]
    public void TestOpenAiClientInstantiation()
    {
        var client = new OpenAiClient("fake-key");
        Assert.Equal("openai", client.ProviderName);
    }
}
