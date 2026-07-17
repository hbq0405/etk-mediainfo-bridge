using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;

namespace ETKMediaInfoBridge
{
    public sealed class Plugin : BasePlugin, IHasThumbImage
    {
        private static readonly object DependencyLock = new object();
        private static Assembly harmonyAssembly;

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
            lock (DependencyLock)
            {
                if (harmonyAssembly != null)
                {
                    return;
                }
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(
                        assembly.GetName().Name,
                        "0Harmony",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        harmonyAssembly = assembly;
                        return;
                    }
                }
                using (var stream = typeof(Plugin).GetTypeInfo().Assembly
                    .GetManifestResourceStream("ETKMediaInfoBridge.0Harmony.dll"))
                {
                    if (stream == null)
                    {
                        throw new FileNotFoundException("Embedded 0Harmony dependency was not found.");
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
                    harmonyAssembly = LoadIntoDefaultContext(bytes);
                }
            }
        }

        private static Assembly LoadIntoDefaultContext(byte[] bytes)
        {
            var loadContextType = Type.GetType(
                "System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader",
                false);
            var defaultContext = loadContextType?
                .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            var loadFromStream = loadContextType?.GetMethod(
                "LoadFromStream",
                new[] { typeof(Stream) });
            if (defaultContext != null && loadFromStream != null)
            {
                using (var stream = new MemoryStream(bytes, false))
                {
                    return (Assembly)loadFromStream.Invoke(defaultContext, new object[] { stream });
                }
            }
            return Assembly.Load(bytes);
        }
    }
}
