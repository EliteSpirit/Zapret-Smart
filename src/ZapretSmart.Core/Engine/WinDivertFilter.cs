using System.Runtime.InteropServices;

namespace ZapretSmart.Core.Engine;

/// <summary>
/// Проверка фильтра WinDivert без открытия драйвера: WinDivertHelperCompileFilter возвращает текст ошибки
/// и позицию. --dry-run движка фильтр не компилирует, поэтому ошибка в нём видна только при настоящем запуске.
/// </summary>
public static class WinDivertFilter
{
    public const int LayerNetwork = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CompileFn([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, IntPtr obj, uint objLen, out IntPtr errorStr, out uint errorPos);

    /// <summary>null — фильтр корректен; иначе текст ошибки с позицией и куском фильтра рядом с ней.</summary>
    public static string? Check(string winDivertDll, string filter, int layer = LayerNetwork)
    {
        var lib = NativeLibrary.Load(winDivertDll);
        try
        {
            var compile = Marshal.GetDelegateForFunctionPointer<CompileFn>(NativeLibrary.GetExport(lib, "WinDivertHelperCompileFilter"));
            if (compile(filter, layer, IntPtr.Zero, 0, out var err, out var pos)) return null;
            var message = err == IntPtr.Zero ? "неизвестная ошибка" : Marshal.PtrToStringAnsi(err);
            var at = (int)Math.Min(pos, (uint)filter.Length);
            var from = Math.Max(0, at - 40);
            return $"{message} (позиция {pos}): …{filter[from..at]}⟦{filter[at..Math.Min(filter.Length, at + 40)]}…";
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }
}
