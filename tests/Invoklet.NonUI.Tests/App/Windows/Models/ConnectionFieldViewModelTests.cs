using Invoklet.Contracts.Connections;
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
}
