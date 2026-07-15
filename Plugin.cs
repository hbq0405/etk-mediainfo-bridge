using System;
using System.IO;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;

namespace ETKMediaInfoBridge
{
    public sealed class Plugin : BasePlugin, IHasThumbImage
    {
        public override string Name => "ETK MediaInfo Bridge";

        public override string Description => "将 ETK 格式化媒体信息写入 Emby，并在刷新后自动恢复。";

        public override Guid Id => new Guid("27f69312-8cb8-4b59-88d1-4077fc8c86d4");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("ETKMediaInfoBridge.logo.png");
        }
    }
}
