using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UnrealPorting.Updater
{
    public class UpdateManifest
    {
        public required string version { get; set; }
        public required string download_url { get; set; }
        public required string changelog { get; set; }
    }
}