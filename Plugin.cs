using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;

namespace ETKMediaInfoBridge
{
    public sealed class Plugin : BasePlugin, IHasThumbImage
    {
        static Plugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
        }

        public override string Name => "ETK MediaInfo Bridge";

        public override string Description => "从 ETK 恢复 Emby 的元数据、图片、媒体流和章节。";

        public override Guid Id => new Guid("27f69312-8cb8-4b59-88d1-4077fc8c86d4");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return GetType().Assembly.GetManifestResourceStream("ETKMediaInfoBridge.logo.png");
        }

        internal static void EnsureDependenciesLoaded()
        {
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            if (!string.Equals(new AssemblyName(args.Name).Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            using (var stream = typeof(Plugin).GetTypeInfo().Assembly
                .GetManifestResourceStream("ETKMediaInfoBridge.0Harmony.dll"))
            {
                if (stream == null)
                {
                    return null;
                }
                var bytes = new byte[stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }
                return Assembly.Load(bytes);
            }
        }
    }
}
