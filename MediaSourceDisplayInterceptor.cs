using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace ETKMediaInfoBridge
{
    internal static class MediaSourceDisplayInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.MediaSourceDisplay";
        private static readonly object SyncRoot = new object();
        private static Harmony harmony;
        private static MethodInfo targetMethod;
        private static ILibraryManager libraryManager;
        private static IJsonSerializer jsonSerializer;
        private static ILogger logger;

        public static void Install(ILibraryManager manager, IJsonSerializer serializer, ILogger pluginLogger)
        {
            lock (SyncRoot)
            {
                if (harmony != null) return;
                var serviceType = Type.GetType(
                    "Emby.Server.MediaEncoding.Api.MediaInfoService, Emby.Server.MediaEncoding", false);
                targetMethod = serviceType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "GetPlaybackInfo")
                    .Where(method => method.ReturnType == typeof(Task<PlaybackInfoResponse>))
                    .Where(method => method.GetParameters().FirstOrDefault()?.ParameterType.Name == "GetPostedPlaybackInfo")
                    .OrderByDescending(method => method.GetParameters().Length)
                    .FirstOrDefault();
                if (targetMethod == null)
                {
                    pluginLogger.Warn("ETK media source display hook was not installed: playback-info response method was not found.");
                    return;
                }
                libraryManager = manager;
                jsonSerializer = serializer;
                logger = pluginLogger;
                harmony = new Harmony(HarmonyId);
                harmony.Patch(targetMethod, postfix: new HarmonyMethod(
                    typeof(MediaSourceDisplayInterceptor), nameof(AfterGetPlaybackInfo)));
                logger.Info("ETK media source display hook is active on {0}.{1}.",
                    targetMethod.DeclaringType?.FullName, targetMethod.Name);
            }
        }

        public static void Uninstall()
        {
            lock (SyncRoot)
            {
                if (harmony == null) return;
                if (targetMethod != null) harmony.Unpatch(targetMethod, HarmonyPatchType.All, HarmonyId);
                targetMethod = null;
                harmony = null;
                libraryManager = null;
                jsonSerializer = null;
                logger = null;
            }
        }

        private static void AfterGetPlaybackInfo(object __0, ref Task<PlaybackInfoResponse> __result)
        {
            if (__result != null) __result = FormatAsync(__result, ResolveItemPath(__0));
        }

        private static async Task<PlaybackInfoResponse> FormatAsync(Task<PlaybackInfoResponse> responseTask, string itemPath)
        {
            var response = await responseTask.ConfigureAwait(false);
            var sources = response?.MediaSources;
            if (sources == null || sources.Length < 2) return response;
            try
            {
                var values = await Task.WhenAll(sources.Select((source, index) => LoadAsync(source, itemPath, index))).ConfigureAwait(false);
                var resolved = values.Where(value => value.Display != null).ToArray();
                if (resolved.Length == 0) return response;
                foreach (var value in resolved)
                {
                    value.Source.Name = value.Display.slot_name
                        + (value.Display.washing_level.HasValue ? " \u00b7 P" + value.Display.washing_level.Value : string.Empty);
                }
                response.MediaSources = resolved
                    .OrderBy(value => value.Display.slot_order)
                    .ThenBy(value => value.Display.washing_level ?? int.MaxValue)
                    .ThenBy(value => value.Index)
                    .Concat(values.Where(value => value.Display == null).OrderBy(value => value.Index))
                    .Select(value => value.Source)
                    .ToArray();
                logger?.Debug(
                    "ETK media source display formatted {0}/{1} sources; first={2}.",
                    resolved.Length,
                    sources.Length,
                    response.MediaSources[0].Name);
            }
            catch (Exception ex)
            {
                logger?.Debug("ETK media source display fallback to Emby: {0}", ex.Message);
            }
            return response;
        }

        private static async Task<DisplayLookup> LoadAsync(MediaSourceInfo source, string itemPath, int index)
        {
            var sourcePath = string.IsNullOrWhiteSpace(source?.Path) ? itemPath : source.Path;
            EtkVersionDisplayPayload display = null;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                display = await EtkMetadataClient.GetVersionDisplayAsync(
                    jsonSerializer, libraryManager, sourcePath, CancellationToken.None).ConfigureAwait(false);
            }
            return new DisplayLookup(source, display, index);
        }

        private static string ResolveItemPath(object request)
        {
            try
            {
                var idValue = request?.GetType().GetProperty("Id")?.GetValue(request);
                if (idValue == null || !long.TryParse(idValue.ToString(), out var itemId)) return null;
                return libraryManager?.GetItemById(itemId)?.Path;
            }
            catch { return null; }
        }

        private sealed class DisplayLookup
        {
            public DisplayLookup(MediaSourceInfo source, EtkVersionDisplayPayload display, int index)
            {
                Source = source; Display = display; Index = index;
            }
            public MediaSourceInfo Source { get; }
            public EtkVersionDisplayPayload Display { get; }
            public int Index { get; }
        }
    }
}
