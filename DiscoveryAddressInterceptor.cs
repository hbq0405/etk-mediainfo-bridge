using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.System;

namespace ETKMediaInfoBridge
{
    internal static class DiscoveryAddressInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.DiscoveryAddress";
        private const string FileName = "etk-mediainfo-bridge-discovery-url.txt";
        private static readonly object SyncRoot = new object();
        private static Harmony harmony;
        private static MethodInfo udpSendMethod;
        private static MethodInfo[] publicInfoMethods = Array.Empty<MethodInfo>();
        private static MethodInfo[] systemInfoMethods = Array.Empty<MethodInfo>();
        private static string discoveryUrl;
        private static ILogger logger;

        public static bool Configure(string url, string configurationDirectory)
        {
            var normalized = Normalize(url);
            if (!string.IsNullOrWhiteSpace(url) && normalized == null)
            {
                return false;
            }

            lock (SyncRoot)
            {
                discoveryUrl = normalized;
                if (!string.IsNullOrWhiteSpace(configurationDirectory))
                {
                    Directory.CreateDirectory(configurationDirectory);
                    var path = Path.Combine(configurationDirectory, FileName);
                    if (normalized == null)
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    else
                    {
                        File.WriteAllText(path, normalized);
                    }
                }
            }
            return true;
        }

        public static void Install(
            IServerApplicationHost applicationHost,
            string configurationDirectory,
            ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }

            Load(configurationDirectory);
            logger = pluginLogger;
            var udpType = Type.GetType(
                "Emby.Server.Implementations.Udp.UdpServer, Emby.Server.Implementations",
                false);
            udpSendMethod = udpType?.GetMethod(
                "SendMessage",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(IPEndPoint), typeof(Encoding) },
                null);

            var hostType = applicationHost?.GetType();
            publicInfoMethods = FindHostMethods(hostType, "GetPublicSystemInfo", typeof(Task<PublicSystemInfo>));
            systemInfoMethods = FindHostMethods(hostType, "GetSystemInfo", typeof(Task<SystemInfo>));
            if (udpSendMethod == null || (publicInfoMethods.Length == 0 && systemInfoMethods.Length == 0))
            {
                pluginLogger.Warn("ETK discovery address hook was not installed: compatible Emby methods were not found.");
                return;
            }

            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                udpSendMethod,
                prefix: new HarmonyMethod(typeof(DiscoveryAddressInterceptor), nameof(BeforeUdpSend)));
            foreach (var method in publicInfoMethods)
            {
                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(DiscoveryAddressInterceptor), nameof(AfterPublicInfo)));
            }
            foreach (var method in systemInfoMethods)
            {
                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(typeof(DiscoveryAddressInterceptor), nameof(AfterSystemInfo)));
            }
            pluginLogger.Info(
                "ETK discovery address hook is active ({0} public, {1} system methods).",
                publicInfoMethods.Length,
                systemInfoMethods.Length);
        }

        public static void Uninstall()
        {
            if (harmony == null)
            {
                return;
            }
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
            udpSendMethod = null;
            publicInfoMethods = Array.Empty<MethodInfo>();
            systemInfoMethods = Array.Empty<MethodInfo>();
            logger = null;
        }

        private static MethodInfo[] FindHostMethods(Type hostType, string name, Type returnType)
        {
            return hostType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == name && method.ReturnType == returnType)
                .Select(method => method.GetBaseDefinition())
                .Distinct()
                .ToArray()
                ?? Array.Empty<MethodInfo>();
        }

        private static void Load(string configurationDirectory)
        {
            if (string.IsNullOrWhiteSpace(configurationDirectory))
            {
                return;
            }
            var path = Path.Combine(configurationDirectory, FileName);
            if (File.Exists(path))
            {
                Configure(File.ReadAllText(path).Trim(), null);
            }
        }

        private static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static string GetConfiguredUrl()
        {
            lock (SyncRoot)
            {
                return discoveryUrl;
            }
        }

        private static void BeforeUdpSend(ref string __0)
        {
            var configured = GetConfiguredUrl();
            if (!string.IsNullOrEmpty(configured))
            {
                __0 = configured;
            }
        }

        private static void AfterPublicInfo(ref Task<PublicSystemInfo> __result)
        {
            if (__result != null && !string.IsNullOrEmpty(GetConfiguredUrl()))
            {
                __result = RewritePublicInfoAsync(__result);
            }
        }

        private static void AfterSystemInfo(ref Task<SystemInfo> __result)
        {
            if (__result != null && !string.IsNullOrEmpty(GetConfiguredUrl()))
            {
                __result = RewriteSystemInfoAsync(__result);
            }
        }

        private static async Task<PublicSystemInfo> RewritePublicInfoAsync(Task<PublicSystemInfo> source)
        {
            var result = await source.ConfigureAwait(false);
            RewriteAddresses(result);
            return result;
        }

        private static async Task<SystemInfo> RewriteSystemInfoAsync(Task<SystemInfo> source)
        {
            var result = await source.ConfigureAwait(false);
            RewriteAddresses(result);
            return result;
        }

        private static void RewriteAddresses(PublicSystemInfo result)
        {
            try
            {
                var configured = GetConfiguredUrl();
                if (result == null || string.IsNullOrEmpty(configured))
                {
                    return;
                }
                result.LocalAddresses = new[] { configured };
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK discovery address rewrite failed.", ex);
            }
        }
    }
}
