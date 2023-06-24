using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUiApp.Core
{
    public class Util
    {
        public static BitmapImage GetBitmapImage(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return null;
            var bitmapImage = new BitmapImage
            {
                UriSource = new Uri(imageUrl)
            };
            return bitmapImage;
        }

        public static string ConvertMiBToGiBOrMiB(double mib)
        {
            var gib = mib / 1024;

            return gib >= 1 ? $"{gib:F2} GiB" : $"{mib:F2} MiB";
        }
    }
}
