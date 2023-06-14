using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUiApp
{
    public class GameItem
    {
        public string Name { get; set; }
        public GameImage GameImage { get; set; }
    }

    public class GameImage
    {
        public string Url { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
