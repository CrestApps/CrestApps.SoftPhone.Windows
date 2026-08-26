using SoftPhone.Core.Config;
using Xunit;

namespace SoftPhone.Core.Tests;

public class DomainHelperTests
{
    [Theory]
    [InlineData("dialpad-dev.crestapps.online", "dialpad-dev.crestapps.online")]
    [InlineData("https://dialpad-dev.crestapps.online", "dialpad-dev.crestapps.online")]
    [InlineData("HTTP://Phone.Example.COM/softphone", "phone.example.com")]
    [InlineData("  phone.example.com/  ", "phone.example.com")]
    [InlineData("phone.example.com:443/x?y=1", "phone.example.com:443")]
    public void NormalizeDomain_strips_scheme_path_and_lowercases(string input, string expected)
    {
        Assert.Equal(expected, DomainHelper.NormalizeDomain(input));
    }

    [Fact]
    public void OriginFor_builds_https_origin()
    {
        Assert.Equal("https://phone.example.com", DomainHelper.OriginFor("HTTPS://phone.example.com/x"));
    }

    [Theory]
    [InlineData("phone.example.com", true)]
    [InlineData("dialpad-dev.crestapps.online", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("localhost", false)] // no dot
    [InlineData("has space.com", false)]
    public void IsValidDomain_checks_plausibility(string input, bool expected)
    {
        Assert.Equal(expected, DomainHelper.IsValidDomain(input));
    }
}
