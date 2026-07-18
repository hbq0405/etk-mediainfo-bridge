using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace ETKMediaInfoBridge
{
    internal static class ManualImageEditInterceptor
    {
        internal sealed class ManualImageEdit
        {
            public DateTime ExpiresAt { get; set; }
            public string ImageType { get; set; }
            public string ImageUrl { get; set; }
        }

        private const string HarmonyId = "ETKMediaInfoBridge.ManualImageEdit";
        private static readonly ConcurrentDictionary<long, ManualImageEdit> MarkedEdits =
            new ConcurrentDictionary<long, ManualImageEdit>();
        private static readonly List<MethodInfo> PatchedMethods = new List<MethodInfo>();
        private static Harmony harmony;
        private static ILogger logger;

        public static void Install(ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }
            logger = pluginLogger;
            harmony = new Harmony(HarmonyId);
            PatchImageService();
            PatchRemoteImageService();
            logger.Info(
                "ETK manual image edit hook is active. Patched methods: {0}.",
                PatchedMethods.Count);
        }

        public static bool TryConsume(long itemId, out ManualImageEdit edit)
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
            foreach (var method in PatchedMethods)
            {
                harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
            }
            PatchedMethods.Clear();
            MarkedEdits.Clear();
            harmony = null;
        }

        private static void PatchImageService()
        {
            var type = Type.GetType("Emby.Api.Images.ImageService, Emby.Api", false);
            if (type == null)
            {
                return;
            }
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameters = method.GetParameters();
                if (method.Name == "PostImage"
                    && parameters.Length > 0
                    && typeof(BaseItem).IsAssignableFrom(parameters[0].ParameterType))
                {
                    Patch(method, nameof(BeforeBaseItemImageEdit));
                    continue;
                }
                if (parameters.Length != 1)
                {
                    continue;
                }
                var requestName = parameters[0].ParameterType.Name;
                if ((method.Name == "Any" && requestName == "DeleteItemImage")
                    || (method.Name == "Post" && (requestName == "UpdateItemImageIndex"
                        || requestName == "UpdateItemImageFromUrl")))
                {
                    Patch(method, nameof(BeforeImageRequest));
                }
            }
        }

        private static void PatchRemoteImageService()
        {
            var type = Type.GetType("Emby.Api.Images.RemoteImageService, Emby.Api", false);
            var method = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return candidate.Name == "Post"
                        && parameters.Length == 1
                        && parameters[0].ParameterType.Name == "DownloadRemoteImage";
                });
            if (method != null)
            {
                Patch(method, nameof(BeforeRemoteImageRequest));
            }
        }

        private static void Patch(MethodInfo method, string prefixName)
        {
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(ManualImageEditInterceptor), prefixName));
            PatchedMethods.Add(method);
        }

        private static void BeforeBaseItemImageEdit(BaseItem __0)
        {
            Mark(__0?.InternalId ?? 0);
        }

        private static void BeforeImageRequest(object __0)
        {
            Mark(
                RequestItemId(__0),
                Convert.ToString(GetPropertyValue(__0, "Type")),
                Convert.ToString(GetPropertyValue(__0, "ImageUrl")));
        }

        private static void BeforeRemoteImageRequest(object __instance, object __0)
        {
            var request = GetPropertyValue(__instance, "Request");
            var userAgent = Convert.ToString(GetPropertyValue(request, "UserAgent")) ?? string.Empty;
            if (userAgent.IndexOf("python-requests", StringComparison.OrdinalIgnoreCase) >= 0
                || userAgent.IndexOf("ETK", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }
            Mark(
                RequestItemId(__0),
                Convert.ToString(GetPropertyValue(__0, "Type")),
                Convert.ToString(GetPropertyValue(__0, "ImageUrl")));
        }

        private static long RequestItemId(object request)
        {
            var value = GetPropertyValue(request, "Id");
            return long.TryParse(Convert.ToString(value), out var itemId) ? itemId : 0;
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

        private static void Mark(long itemId, string imageType = null, string imageUrl = null)
        {
            if (itemId > 0)
            {
                MarkedEdits[itemId] = new ManualImageEdit
                {
                    ExpiresAt = DateTime.UtcNow.AddSeconds(10),
                    ImageType = imageType,
                    ImageUrl = imageUrl
                };
            }
        }
    }
}
