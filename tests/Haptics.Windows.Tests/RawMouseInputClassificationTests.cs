using Haptics.Core;

namespace Haptics.Windows.Tests;

public class RawMouseInputClassificationTests
{
    [Theory]
    [InlineData(12, 0, PointerInputClass.Handle)]
    [InlineData(0, 0, PointerInputClass.Handleless)]
    [InlineData(12, 0xFF515701, PointerInputClass.Promoted)]
    [InlineData(0, 0xFF5157FF, PointerInputClass.Promoted)]
    [InlineData(0, 0xFF525700, PointerInputClass.Handleless)]
    public void InputClassDistinguishesHandlelessAndPromotedInput(long deviceHandle, uint extraInformation, PointerInputClass expected)
    {
        var input = new RawMouseInput(10, RawInputCode.Background, deviceHandle, 0, RawMouseButtons.None, 0, 0, 0, extraInformation);

        Assert.Equal(expected, input.InputClass);
    }
}
