using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace ETKMediaInfoBridge
{
    internal static class ManualImageSearchInterceptor
    {
        private sealed class SearchRequestState
        {
            public DateTime ExpiresAt { get; set; }
            public bool IncludeAllLanguages { get; set; }
        }

        private const string HarmonyId = "ETKMediaInfoBridge.ManualImageSearch";
        private static readonly ConcurrentDictionary<long, SearchRequestState> ActiveRequests =
            new ConcurrentDictionary<long, SearchRequestState>();
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static ILogger logger;

        public static void Install(ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }
            var serviceType = Type.GetType("Emby.Api.Images.RemoteImageService, Emby.Api", false);
            targetMethod = serviceType?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "Get"
                        && parameters.Length == 1
                        && parameters[0].ParameterType.Name == "GetRemoteImages";
                });
            if (targetMethod == null)
            {
                pluginLogger.Warn("ETK manual image search hook was not installed: Emby remote image API was not found.");
                return;
            }
            logger = pluginLogger;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                targetMethod,
                prefix: new HarmonyMethod(typeof(ManualImageSearchInterceptor), nameof(BeforeSearch)));
            logger.Info("ETK manual image search hook is active.", Array.Empty<object>());
        }

        public static bool TryGetRequest(long itemId, out bool includeAllLanguages)
        {
            includeAllLanguages = false;
            if (!ActiveRequests.TryGetValue(itemId, out var state))
            {
                return false;
            }
            if (state.ExpiresAt > DateTime.UtcNow)
            {
                includeAllLanguages = state.IncludeAllLanguages;
                return true;
            }
            ActiveRequests.TryRemove(itemId, out _);
            return false;
        }

        public static void Uninstall()
        {
            if (harmony != null)
            {
                harmony.Unpatch(targetMethod, HarmonyPatchType.All, HarmonyId);
            }
            harmony = null;
            targetMethod = null;
            logger = null;
            ActiveRequests.Clear();
        }

        private static void BeforeSearch(object __0)
        {
            try
            {
                var value = __0?.GetType().GetProperty("Id")?.GetValue(__0);
                if (long.TryParse(Convert.ToString(value), out var itemId) && itemId > 0)
                {
                    var includeValue = __0?.GetType().GetProperty("IncludeAllLanguages")?.GetValue(__0);
                    ActiveRequests[itemId] = new SearchRequestState
                    {
                        ExpiresAt = DateTime.UtcNow.AddSeconds(30),
                        IncludeAllLanguages = includeValue != null && Convert.ToBoolean(includeValue)
                    };
                }
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK manual image search hook failed.", ex);
            }
        }
    }
}
