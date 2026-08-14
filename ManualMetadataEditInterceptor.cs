using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace ETKMediaInfoBridge
{
    internal static class ManualMetadataEditInterceptor
    {
        internal sealed class ManualMetadataEdit
        {
            public DateTime ExpiresAt { get; set; }
            public bool HasCustomRating { get; set; }
            public string CustomRating { get; set; }
        }

        private const string HarmonyId = "ETKMediaInfoBridge.ManualMetadataEdit";
        private static readonly ConcurrentDictionary<long, ManualMetadataEdit> MarkedEdits =
            new ConcurrentDictionary<long, ManualMetadataEdit>();
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static ILogger logger;

        public static void Install(ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }
            var serviceType = Type.GetType("Emby.Api.ItemUpdateService, Emby.Api", false);
            targetMethod = serviceType?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.Name == "Post"
                        && parameters.Length == 1
                        && parameters[0].ParameterType.Name == "UpdateItem";
                });
            if (targetMethod == null)
            {
                pluginLogger.Warn("ETK manual metadata edit hook was not installed: Emby update API was not found.");
                return;
            }

            logger = pluginLogger;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(
                targetMethod,
                prefix: new HarmonyMethod(typeof(ManualMetadataEditInterceptor), nameof(BeforeMetadataRequest)));
            logger.Info("ETK manual metadata edit hook is active.", Array.Empty<object>());
        }

        public static bool TryConsume(long itemId, out ManualMetadataEdit edit)
        {
            if (!MarkedEdits.TryRemove(itemId, out edit))
            {
                return false;
            }
            return edit.ExpiresAt > DateTime.UtcNow;
        }

        public static void Uninstall()
        {
            if (harmony == null)
            {
                return;
            }
            harmony.Unpatch(targetMethod, HarmonyPatchType.All, HarmonyId);
            MarkedEdits.Clear();
            harmony = null;
            targetMethod = null;
            logger = null;
        }

        private static void BeforeMetadataRequest(object __instance, object __0)
        {
            try
            {
                var request = GetPropertyValue(__instance, "Request");
                var userAgent = Convert.ToString(GetPropertyValue(request, "UserAgent")) ?? string.Empty;
                if (userAgent.IndexOf("python-requests", StringComparison.OrdinalIgnoreCase) >= 0
                    || userAgent.IndexOf("python-urllib", StringComparison.OrdinalIgnoreCase) >= 0
                    || userAgent.IndexOf("ETK", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                var value = GetPropertyValue(__0, "ItemId");
                if (long.TryParse(Convert.ToString(value), out var itemId) && itemId > 0)
                {
                    var hasCustomRating = TryGetPropertyValue(__0, "CustomRating", out var customRating);
                    MarkedEdits[itemId] = new ManualMetadataEdit
                    {
                        ExpiresAt = DateTime.UtcNow.AddSeconds(10),
                        HasCustomRating = hasCustomRating,
                        CustomRating = customRating == null ? null : Convert.ToString(customRating)
                    };
                }
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK manual metadata edit hook failed.", ex);
            }
        }

        private static object GetPropertyValue(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    return property.GetValue(instance);
                }
            }
            return null;
        }

        private static bool TryGetPropertyValue(object instance, string name, out object value)
        {
            value = null;
            if (instance == null)
            {
                return false;
            }
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    value = property.GetValue(instance);
                    return true;
                }
            }
            return false;
        }
    }
}
