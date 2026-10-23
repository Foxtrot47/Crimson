using Crimson.Resources;
namespace Crimson.Utils;

public static class StorageSizeFormatter
{
    public static string FormatMebibytes(double mebibytes)
    {
        var gibibytes = mebibytes / 1024;
        return gibibytes >= 1
            ? UiStrings.Format(UiStrings.SizeGiBFormat, gibibytes)
            : UiStrings.Format(UiStrings.SizeMiBFormat, mebibytes);
    }
}
