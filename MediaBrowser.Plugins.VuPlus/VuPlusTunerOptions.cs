using System;
using System.Collections.Generic;
using System.Text;

namespace MediaBrowser.Plugins.VuPlus
{
    public class VuPlusTunerOptions
    {
        public int StreamingPort { get; set; } = 8001;
        public string WebInterfaceUsername { get; set; }
        public string WebInterfacePassword { get; set; }
        public bool OnlyOneBouquet { get; set; } = true;
        public string TVBouquet { get; set; } = "Favourites (TV)";
        public bool ZapToChannel { get; set; }
    }
}
