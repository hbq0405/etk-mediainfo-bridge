using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace ETKMediaInfoBridge
{
    internal sealed class RefreshRequestInfo
    {
        public long ItemId { get; set; }

        public bool Recursive { get; set; }

        public string MetadataRefreshMode { get; set; }

        public string ImageRefreshMode { get; set; }

        public bool ReplaceAllMetadata { get; set; }

        public bool ReplaceAllImages { get; set; }

        public string Action { get; set; }
    }

    internal static class RefreshItemInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.RefreshItem";
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static Action<long, bool> onRefreshStarting;
        private static Action<long> onRefreshRequested;
        private static Action<RefreshRequestInfo> onRefreshActionRequested;
        private static ILogger logger;

        public static void Install(
            Action<long, bool> startingCallback,
            Action<long> completedCallback,
            Action<RefreshRequestInfo> actionCallback,
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
            onRefreshActionRequested = actionCallback;
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
            onRefreshActionRequested = null;
            logger = null;
        }

        private static void BeforeRefreshRequested(object __0, out RefreshRequestInfo __state)
        {
            __state = null;
            try
            {
                if (!TryGetItemId(__0, out var itemId))
                {
                    return;
                }
                var replaceValue = __0?.GetType().GetProperty("ReplaceAllImages")?.GetValue(__0);
                var replaceAllImages = replaceValue != null && Convert.ToBoolean(replaceValue);
                var replaceMetadataValue = __0?.GetType().GetProperty("ReplaceAllMetadata")?.GetValue(__0);
                var replaceAllMetadata = replaceMetadataValue != null && Convert.ToBoolean(replaceMetadataValue);
                onRefreshStarting?.Invoke(itemId, replaceAllImages);
                var metadataModeValue = __0?.GetType().GetProperty("MetadataRefreshMode")?.GetValue(__0);
                var metadataMode = Convert.ToString(metadataModeValue);
                var imageMode = Convert.ToString(__0?.GetType().GetProperty("ImageRefreshMode")?.GetValue(__0));
                var recursiveValue = __0?.GetType().GetProperty("Recursive")?.GetValue(__0);
                var recursive = recursiveValue != null && Convert.ToBoolean(recursiveValue);
                logger?.Info(
                    "ETK refresh request observed for Item {0}: metadata={1}, image={2}, replaceMetadata={3}, replaceImages={4}.",
                    itemId,
                    metadataMode ?? "<null>",
                    imageMode ?? "<null>",
                    replaceAllMetadata,
                    replaceAllImages);

                // Emby 4.9's dialog always uses FullRefresh. Only "search missing
                // metadata" clears ReplaceAllMetadata without requesting images.
                var isMissingMetadataRefresh = string.Equals(
                    metadataMode,
                    "FullRefresh",
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        imageMode,
                        "FullRefresh",
                        StringComparison.OrdinalIgnoreCase)
                    && !replaceAllMetadata
                    && !replaceAllImages;
                var action = isMissingMetadataRefresh
                    ? "missing_metadata"
                    : replaceAllImages
                        ? "replace_images"
                        : "default";
                if (!MediaInfoRefreshGuard.TryConsumeRefreshSuppression(itemId))
                {
                    __state = new RefreshRequestInfo
                    {
                        ItemId = itemId,
                        Recursive = recursive,
                        MetadataRefreshMode = metadataMode,
                        ImageRefreshMode = imageMode,
                        ReplaceAllMetadata = replaceAllMetadata,
                        ReplaceAllImages = replaceAllImages,
                        Action = action
                    };
                }
            }
            catch (Exception ex)
            {
                logger?.ErrorException("ETK refresh request pre-hook failed.", ex);
            }
        }

        private static void AfterRefreshRequested(object __0, RefreshRequestInfo __state)
        {
            try
            {
                if (__state != null)
                {
                    onRefreshActionRequested?.Invoke(__state);
                }
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
