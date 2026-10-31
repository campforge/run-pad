using Invoklet.Contracts.Connections;
using Invoklet.Format.MdRun;
using Invoklet.Windows.Models;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Windows.Models;

public sealed class ConnectionFieldViewModelTests
{
    [Theory]
    [InlineData(ConnectionParamType.File, true)]
    [InlineData(ConnectionParamType.Text, false)]
    [InlineData(ConnectionParamType.Number, false)]
    [InlineData(ConnectionParamType.Boolean, false)]
    public void CanBrowse_TheTypeOfTheParameter_IsTrueOnlyForAFile(ConnectionParamType type, bool expected)
    {
        // Arrange
        var sut = new ConnectionFieldViewModel(new ConnectionParam("p", "Param", type), null, []);

        // Act / Assert
        Assert.Equal(expected, sut.CanBrowse);
    }

    [Fact]
    public void IsTextBox_AFile_IsEditedAsTextSoThePathCanBeTypedOrHoldAPlaceholder()
    {
        var sut = new ConnectionFieldViewModel(new ConnectionParam("p", "Param", ConnectionParamType.File), null, []);

        Assert.True(sut.IsTextBox);
    }

    // The form asks for host and port and a checkbox; the file keeps one text.
    [Theory]
    [InlineData("https://localhost:5001", "localhost:5001", true)]
    [InlineData("http://localhost:5001", "localhost:5001", false)]
    [InlineData("localhost:5001", "localhost:5001", true)]
    [InlineData("", "", true)]
    public void Endpoint_AStoredAddress_ShowsHostAndPortAndTheSslCheckbox(string stored, string host, bool ssl)
    {
        var sut = new ConnectionFieldViewModel(new ConnectionParam("address", "Address", ConnectionParamType.Endpoint), MdRunValue.Of(stored), []);

        Assert.Equal(host, sut.EndpointHost);
        Assert.Equal(ssl, sut.UseSsl);
        Assert.True(sut.IsEndpoint);
        Assert.False(sut.IsTextBox);
    }

    [Fact]
    public void Endpoint_TheHostAndTheCheckbox_AreStoredAsOneText()
    {
        var sut = new ConnectionFieldViewModel(new ConnectionParam("address", "Address", ConnectionParamType.Endpoint), null, []);

        sut.EndpointHost = "api.local:443";
        Assert.Equal("https://api.local:443", sut.ToValue()!.Text);
        sut.UseSsl = false;
        Assert.Equal("http://api.local:443", sut.ToValue()!.Text);
        sut.EndpointHost = "";
        Assert.Null(sut.ToValue());
    }

    [Fact]
    public void Endpoint_AnAddressPastedWithItsProtocol_SetsTheCheckboxAndShowsOnlyTheHost()
    {
        var sut = new ConnectionFieldViewModel(new ConnectionParam("address", "Address", ConnectionParamType.Endpoint), null, []);

        sut.EndpointHost = "http://10.0.0.5:8080";

        Assert.False(sut.UseSsl);
        Assert.Equal("10.0.0.5:8080", sut.EndpointHost);
        Assert.Equal("http://10.0.0.5:8080", sut.ToValue()!.Text);
    }
}
