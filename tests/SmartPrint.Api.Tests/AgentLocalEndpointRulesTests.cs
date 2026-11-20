using SmartPrint.Api.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentLocalEndpointRulesTests
{
    [Theory]
    [InlineData("http://192.168.1.25:17842", "http://192.168.1.25:17842")]
    [InlineData("http://10.20.30.40:17842/", "http://10.20.30.40:17842")]
    [InlineData("http://172.31.255.2:17842", "http://172.31.255.2:17842")]
    public void AcceptsPrivateIpv4Endpoints(string supplied, string expected)
    {
        var result = AgentLocalEndpointRules.Normalize([supplied]);
        Assert.Equal(new[] { expected }, result);
    }

    [Theory]
    [InlineData("http://8.8.8.8:17842")]
    [InlineData("http://172.32.0.1:17842")]
    [InlineData("http://192.168.1.25:80")]
    [InlineData("https://192.168.1.25:17842")]
    [InlineData("http://localhost:17842")]
    [InlineData("http://printer.local:17842")]
    [InlineData("http://127.0.0.1:17842")]
    [InlineData("http://192.168.1.25:17842/admin")]
    [InlineData("http://user:pass@192.168.1.25:17842")]
    public void RejectsPublicOrUnexpectedEndpoints(string supplied)
    {
        Assert.Empty(AgentLocalEndpointRules.Normalize([supplied]));
    }

    [Fact]
    public void RemovesDuplicatesAndLimitsEndpointCount()
    {
        var supplied = Enumerable.Range(1, 10)
            .Select(index => $"http://192.168.1.{index}:17842")
            .Append("http://192.168.1.1:17842")
            .ToArray();

        var result = AgentLocalEndpointRules.Normalize(supplied);

        Assert.Equal(8, result.Count);
        Assert.Equal(8, result.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MissingEndpointsReturnAnEmptyList()
    {
        Assert.Empty(AgentLocalEndpointRules.Normalize(null));
    }
}
