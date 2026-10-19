using Avalonia.Data.Converters;
using Avalonia.Input;

namespace NRSVaultMini;

public static class Converters
{
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private static readonly Cursor Arrow = new(StandardCursorType.Arrow);

    public static readonly IValueConverter HandCursor =
        new FuncValueConverter<bool, Cursor>(b => b ? Hand : Arrow);

    public static readonly IValueConverter FilterTip =
        new FuncValueConverter<string?, string?>(f => f == null ? null : $"Click to show: {f}");
}
