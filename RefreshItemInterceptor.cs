using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace ETKMediaInfoBridge
{
    internal static class RefreshItemInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.RefreshItem";
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static Action<long, bool> onRefreshStarting;
        private static Action<long> onRefreshRequested;
        private static ILogger logger;

        public static void Install(
            Action<long, bool> startingCallback,
            Action<long> completedCallback,
            ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }
            var serviceType = Type.GetType("Emby.Api.ItemRefreshService, Emby.Api", false);
            targetMethod = serviceType?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "Post"
                        && parameters.Length == 1
                        && parameters[0].ParameterType.Name == "RefreshItem";
                });
            if (targetMethod == null)
            {
                pluginLogger.Warn("ETK refresh request hook was not installed: Emby refresh API was not found.");
                return;
            }

            onRefreshStarting = startingCallback;
            onRefreshRequested = completedCallback;
            logger = pluginLogger;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                targetMethod,
                prefix: new HarmonyMethod(typeof(RefreshItemInterceptor), nameof(BeforeRefreshRequested)),
                postfix: new HarmonyMethod(typeof(RefreshItemInterceptor), nameof(AfterRefreshRequested)));
            logger.Info("ETK refresh request hook is active.", Array.Empty<object>());
        }

        public static void Uninstall()
        {
            if (harmony == null)
            {
                return;
            }
            harmony.Unpatch(targetMethod, HarmonyPatchType.All, HarmonyId);
            harmony = null;
            targetMethod = null;
            onRefreshStarting = null;
            onRefreshRequested = null;
            logger = null;
        }

        private static void BeforeRefreshRequested(object __0)
        {
            try
            {
                if (!TryGetItemId(__0, out var itemId))
                {
                    return;
                }
                var replaceValue = __0?.GetType().GetProperty("ReplaceAllImages")?.GetValue(__0);
                var replaceAllImages = replaceValue != null && Convert.ToBoolean(replaceValue);
                onRefreshStarting?.Invoke(itemId, replaceAllImages);
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK refresh request pre-hook failed.", ex);
            }
        }

        private static void AfterRefreshRequested(object __0)
        {
            try
            {
                if (TryGetItemId(__0, out var itemId))
                {
                    onRefreshRequested?.Invoke(itemId);
                }
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK refresh request hook failed.", ex);
            }
        }

        private static bool TryGetItemId(object request, out long itemId)
        {
            var value = request?.GetType().GetProperty("Id")?.GetValue(request);
            return long.TryParse(Convert.ToString(value), out itemId) && itemId > 0;
        }
    }
}
