using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinUiApp.StateManager
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
    }
}
