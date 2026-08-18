using System;
using Crimson.Presentation;
using Microsoft.UI.Xaml.Data;

namespace Crimson.Utils;

public sealed class GameActionIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is GameActionIcon icon ? ToGlyph(icon) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static string ToGlyph(GameActionIcon icon) => icon switch
    {
        GameActionIcon.Install => "\uE896",
        GameActionIcon.Play => "\uE768",
        GameActionIcon.Update => "\uE777",
        GameActionIcon.Repair => "\uE90F",
        _ => string.Empty
    };
}
