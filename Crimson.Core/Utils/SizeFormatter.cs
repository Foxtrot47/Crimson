using Crimson.Resources;

namespace Crimson.Utils;

public static class SizeFormatter
{
    public static string Format(double bytes)
    {
        string[] sizes = [UiStrings.B, UiStrings.KB, UiStrings.MB, UiStrings.GB, UiStrings.TB];
        var order = 0;
        var size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return UiStrings.Format(UiStrings.SizeWithUnitFormat, size, sizes[order]);
    }
}
