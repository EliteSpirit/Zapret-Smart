using Avalonia.Data.Converters;

namespace ZapretSmart.App;

public static class Converters
{
    /// <summary>Строка журнала с «!» в начале: предупреждение или ошибка.</summary>
    public static readonly IValueConverter IsWarningLine =
        new FuncValueConverter<string?, bool>(s => s is not null && s.StartsWith('!'));

    /// <summary>Строка журнала с «>» в начале: команда запуска движка, второстепенная.</summary>
    public static readonly IValueConverter IsCommandLine =
        new FuncValueConverter<string?, bool>(s => s is not null && s.StartsWith('>'));

    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);

    public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(n => n > 0);
}
