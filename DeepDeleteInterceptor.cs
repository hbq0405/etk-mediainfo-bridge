using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace ETKMediaInfoBridge
{
    internal sealed class DeepDeletePrepareRequest
    {
        public long root_item_id { get; set; }
        public long anchor_item_id { get; set; }
        public string item_name { get; set; }
        public string item_type { get; set; }
    }

    internal sealed class DeepDeletePrepareResponse
    {
        public bool ok { get; set; }
        public string token { get; set; }
        public int pickcode_count { get; set; }
    }

    internal sealed class DeepDeleteCommitRequest
    {
        public string token { get; set; }
    }

    internal sealed class DeepDeleteState
    {
        public string CommitUrl { get; set; }
        public string Token { get; set; }
        public long ItemId { get; set; }
        public int PickCodeCount { get; set; }
    }

    internal static class DeepDeleteInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.DeepDelete";
        private static readonly object SyncRoot = new object();
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static ILibraryManager libraryManager;
        private static IJsonSerializer jsonSerializer;
        private static ILogger logger;
        private static Harmony harmony;
        private static MethodInfo targetMethod;

        public static void Install(
            ILibraryManager manager,
            IJsonSerializer serializer,
            ILogger pluginLogger)
        {
            lock (SyncRoot)
            {
                if (harmony != null)
                {
                    return;
                }
                libraryManager = manager;
                jsonSerializer = serializer;
                logger = pluginLogger;
                targetMethod = manager.GetType().GetMethod(
                    "DeleteItem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) },
                    null);
                if (targetMethod == null)
                {
                    logger.Warn("ETK deep delete hook was not installed: Emby DeleteItem method not found.");
                    return;
                }

                harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    targetMethod,
                    prefix: new HarmonyMethod(typeof(DeepDeleteInterceptor), nameof(BeforeDelete)),
                    postfix: new HarmonyMethod(typeof(DeepDeleteInterceptor), nameof(AfterDelete)));
                logger.Info("ETK deep delete hook is active.", Array.Empty<object>());
            }
        }

        public static void Uninstall()
        {
            lock (SyncRoot)
            {
                if (harmony == null || targetMethod == null)
                {
                    return;
                }
                harmony.Unpatch(targetMethod, HarmonyPatchType.All, HarmonyId);
                harmony = null;
                targetMethod = null;
            }
        }

        private static void BeforeDelete(BaseItem __0, DeleteOptions __1, out DeepDeleteState __state)
        {
            __state = null;
            if (__0 == null || __1 == null || !__1.DeleteFileLocation)
            {
                return;
            }
            try
            {
                __state = Prepare(__0);
            }
            catch (Exception ex)
            {
                logger.ErrorException(
                    "ETK deep delete preparation failed for Item " + __0.InternalId,
                    ex);
            }
        }

        private static void AfterDelete(DeepDeleteState __state)
        {
            if (__state == null)
            {
                return;
            }
            _ = Task.Run(() => CommitAsync(__state));
        }

        private static DeepDeleteState Prepare(BaseItem rootItem)
        {
            var anchor = FindAnchor(rootItem);
            if (anchor == null)
            {
                logger.Debug(
                    "ETK deep delete ignored Item {0}: no ETK STRM anchor found.",
                    rootItem.InternalId);
                return null;
            }
            var mediaInfoUrl = EtkMetadataClient.ResolveMediaInfoUrl(anchor.Path);
            if (string.IsNullOrEmpty(mediaInfoUrl))
            {
                return null;
            }

            var prepareUrl = mediaInfoUrl.TrimEnd('/') + "/deep-delete/prepare";
            var request = new DeepDeletePrepareRequest
            {
                root_item_id = rootItem.InternalId,
                anchor_item_id = anchor.InternalId,
                item_name = rootItem.Name,
                item_type = rootItem.GetType().Name
            };
            var content = new StringContent(
                jsonSerializer.SerializeToString(request),
                Encoding.UTF8,
                "application/json");
            using (var response = HttpClient.PostAsync(prepareUrl, content).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.Warn(
                        "ETK deep delete preparation rejected for Item {0}: HTTP {1}.",
                        rootItem.InternalId,
                        (int)response.StatusCode);
                    return null;
                }
                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = jsonSerializer.DeserializeFromString<DeepDeletePrepareResponse>(json);
                if (result == null || !result.ok || string.IsNullOrWhiteSpace(result.token))
                {
                    return null;
                }
                logger.Info(
                    "ETK deep delete prepared Item {0} with {1} pick codes.",
                    rootItem.InternalId,
                    result.pickcode_count);
                return new DeepDeleteState
                {
                    CommitUrl = mediaInfoUrl.TrimEnd('/') + "/deep-delete/commit",
                    Token = result.token,
                    ItemId = rootItem.InternalId,
                    PickCodeCount = result.pickcode_count
                };
            }
        }

        private static BaseItem FindAnchor(BaseItem rootItem)
        {
            var itemType = rootItem.GetType().Name;
            if (string.Equals(itemType, "Series", StringComparison.Ordinal)
                || string.Equals(itemType, "Season", StringComparison.Ordinal))
            {
                var descendants = libraryManager.GetItemList(new InternalItemsQuery
                {
                    Parent = rootItem,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode", "Movie" }
                });
                var child = descendants.FirstOrDefault(item =>
                    !string.IsNullOrEmpty(EtkMetadataClient.ResolveMediaInfoUrl(item.Path)));
                if (child != null)
                {
                    return child;
                }
            }
            return string.IsNullOrEmpty(EtkMetadataClient.ResolveMediaInfoUrl(rootItem.Path))
                ? null
                : rootItem;
        }

        private static async Task CommitAsync(DeepDeleteState state)
        {
            try
            {
                var request = new DeepDeleteCommitRequest { token = state.Token };
                using (var content = new StringContent(
                    jsonSerializer.SerializeToString(request),
                    Encoding.UTF8,
                    "application/json"))
                using (var response = await HttpClient.PostAsync(state.CommitUrl, content).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        logger.Info(
                            "ETK deep delete committed Item {0} with {1} pick codes.",
                            state.ItemId,
                            state.PickCodeCount);
                    }
                    else
                    {
                        logger.Warn(
                            "ETK deep delete commit failed for Item {0}: HTTP {1}.",
                            state.ItemId,
                            (int)response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException(
                    "ETK deep delete commit failed for Item " + state.ItemId,
                    ex);
            }
        }
    }
}
