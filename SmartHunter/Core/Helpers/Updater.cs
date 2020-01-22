using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using SmartHunter.Game.Helpers;

namespace SmartHunter.Core.Helpers
{
    public class Updater
    {
        private bool needsUpdate = false;
        private string ApiEndpoint = "https://api.github.com/repos/gabrielefilipp/SmartHunter/commits";

        public bool CheckForUpdates()
        {
            return false;
            try
            {
                using (var client = new WebClient())
                {
                    using (client.OpenRead("http://google.com/generate_204")) // check connection (maybe pointless?)
                    {
                        string[] files = new string[3] { "SmartHunter/bin/x64/Debug/SmartHunter.exe", "SmartHunter/Game/Config/MonsterDataConfig.cs", "SmartHunter/Game/Config/LocalizationConfig.cs" };
                        var branch = "Peppa";

                        client.Dispose();
                        client.Headers["User-Agent"] = "Mozilla/4.0 (Compatible; Windows NT 5.1; MSIE 6.0) (compatible; MSIE 6.0; Windows NT 5.1; .NET CLR 1.1.4322; .NET CLR 2.0.50727)";

                        foreach (string file in files)
                        {
                            JObject json = JObject.Parse(client.DownloadString($"{ApiEndpoint}/{branch}?path={file}"));
                            if (!ConfigHelper.Versions.Values.GetType().GetField(Path.GetFileNameWithoutExtension(file)).GetValue(ConfigHelper.Versions.Values).Equals(json["commit"]["tree"]["sha"])) //TODO: safe checks?
                            {
                                return true;
                            }

                        }
                    }

                }
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
