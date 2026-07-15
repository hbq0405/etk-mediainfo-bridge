using System;
using MediaBrowser.Common.Plugins;

namespace ETKMediaInfoBridge
{
    public sealed class Plugin : BasePlugin
    {
        public override string Name => "ETK MediaInfo Bridge";

        public override string Description => "Imports ETK formatted media information into Emby.";

        public override Guid Id => new Guid("27f69312-8cb8-4b59-88d1-4077fc8c86d4");
    }
}
