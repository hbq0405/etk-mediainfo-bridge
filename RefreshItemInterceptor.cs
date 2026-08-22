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

        public bool Suppressed { get; set; }
    }

    internal static class RefreshItemInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.RefreshItem";
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static Action<RefreshRequestInfo> onRefreshStarting;
        private static Action<RefreshRequestInfo> onRefreshRequested;
        private static Action<RefreshRequestInfo> onRefreshActionRequested;
        private static ILogger logger;

        public static void Install(
            Action<RefreshRequestInfo> startingCallback,
            Action<RefreshRequestInfo> completedCallback,
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
                var metadataModeValue = __0?.GetType().GetProperty("MetadataRefreshMode")?.GetValue(__0);
                var metadataMode = NormalizeRefreshMode(metadataModeValue);
                var imageMode = NormalizeRefreshMode(
                    __0?.GetType().GetProperty("ImageRefreshMode")?.GetValue(__0));
                var recursiveValue = __0?.GetType().GetProperty("Recursive")?.GetValue(__0);
                var recursive = recursiveValue != null && Convert.ToBoolean(recursiveValue);
                logger?.Info(
                    "ETK refresh request observed for Item {0}: metadata={1}, image={2}, replaceMetadata={3}, replaceImages={4}.",
                    itemId,
                    metadataMode ?? "<null>",
                    imageMode ?? "<null>",
                    replaceAllMetadata,
                    replaceAllImages);

                // Emby 4.9.0.35's missing-metadata dialog requests FullRefresh
                // for both metadata and images. ETKN's cache restore deliberately
                // uses ImageRefreshMode=ValidationOnly, so keep that internal
                // path out of backfill even if suppression could not be consumed.
                var isMissingMetadataRefresh = string.Equals(
                    metadataMode,
                    "FullRefresh",
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        imageMode,
                        "FullRefresh",
                        StringComparison.OrdinalIgnoreCase)
                    && !replaceAllMetadata;
                var isMetadataOnlyRefresh = string.Equals(
                    metadataMode,
                    "FullRefresh",
                    StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        imageMode,
                        "FullRefresh",
                        StringComparison.OrdinalIgnoreCase)
                    && !replaceAllMetadata
                    && !replaceAllImages
                    || string.Equals(
                        metadataMode,
                        "Default",
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        imageMode,
                        "ValidationOnly",
                        StringComparison.OrdinalIgnoreCase)
                    && !replaceAllMetadata
                    && !replaceAllImages;
                var action = isMissingMetadataRefresh
                    ? (replaceAllImages ? "missing_metadata_images" : "missing_metadata")
                    : replaceAllImages
                        ? "replace_images"
                        : isMetadataOnlyRefresh ? "metadata" : "default";
                __state = new RefreshRequestInfo
                {
                    ItemId = itemId,
                    Recursive = recursive,
                    MetadataRefreshMode = metadataMode,
                    ImageRefreshMode = imageMode,
                    ReplaceAllMetadata = replaceAllMetadata,
                    ReplaceAllImages = replaceAllImages,
                    Action = action,
                    Suppressed = MediaInfoRefreshGuard.TryConsumeRefreshSuppression(itemId)
                };
                if (!__state.Suppressed)
                {
                    // Keep the user-selected branch online while preventing
                    // Emby's other native provider branch from doing its own
                    // network lookup.  A combined missing+replace request keeps
                    // the image branch online while ETKN owns metadata lookup.
                    if (isMissingMetadataRefresh)
                    {
                        // ETKN's missing-metadata workflow is cache-first and
                        // performs its own online fallback only when needed.
                        TrySetRefreshMode(__0, "MetadataRefreshMode", "ValidationOnly");
                        if (!replaceAllImages)
                        {
                            TrySetRefreshMode(__0, "ImageRefreshMode", "ValidationOnly");
                        }
                    }
                    else if (replaceAllImages && !isMissingMetadataRefresh)
                    {
                        TrySetRefreshMode(__0, "MetadataRefreshMode", "ValidationOnly");
                    }
                    else if (isMetadataOnlyRefresh)
                    {
                        TrySetRefreshMode(__0, "ImageRefreshMode", "ValidationOnly");
                    }
                    else if (!isMissingMetadataRefresh && !replaceAllImages)
                    {
                        TrySetRefreshMode(__0, "MetadataRefreshMode", "ValidationOnly");
                        TrySetRefreshMode(__0, "ImageRefreshMode", "ValidationOnly");
                    }
                }
                // Suppressed requests are ETK-initiated.  The starting callback
                // registers their restore policy too, but skips any online work;
                // only unsuppressed user actions may trigger image prefetch.
                onRefreshStarting?.Invoke(__state);
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
                    if (!__state.Suppressed)
                    {
                        onRefreshActionRequested?.Invoke(__state);
                    }
                    onRefreshRequested?.Invoke(__state);
                }
                else if (TryGetItemId(__0, out var itemId))
                {
                    onRefreshRequested?.Invoke(new RefreshRequestInfo
                    {
                        ItemId = itemId,
                        Action = "default"
                    });
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

        private static string NormalizeRefreshMode(object value)
        {
            if (value == null)
            {
                return null;
            }

            var text = Convert.ToString(value);
            if (!int.TryParse(text, out var numericValue))
            {
                try
                {
                    numericValue = Convert.ToInt32(value);
                }
                catch (Exception)
                {
                    return text;
                }
            }

            // Emby 4.9 exposes the enum names, while older clients and some
            // proxies can serialize the underlying values.  Keep the action
            // classifier stable across both representations.
            switch (numericValue)
            {
                case 1:
                    return "ValidationOnly";
                case 2:
                    return "Default";
                case 3:
                    return "FullRefresh";
                default:
                    return text;
            }
        }

        private static void TrySetRefreshMode(object request, string propertyName, string valueName)
        {
            try
            {
                var property = request?.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || !property.CanWrite)
                {
                    return;
                }

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType)
                    ?? property.PropertyType;
                if (propertyType == typeof(string))
                {
                    property.SetValue(request, valueName);
                }
                else if (propertyType.IsEnum)
                {
                    var enumValue = Enum.Parse(propertyType, valueName, true);
                    property.SetValue(request, enumValue);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(
                    "ETK could not constrain Emby refresh mode {0}: {1}",
                    propertyName,
                    ex.Message);
            }
        }
    }
}
