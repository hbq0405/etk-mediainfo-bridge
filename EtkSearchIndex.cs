using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace ETKMediaInfoBridge
{
    internal sealed class EtkSearchDocument
    {
        public long id { get; set; }
        public string type { get; set; }
        public string title { get; set; }
        public string original_title { get; set; }
        public string series_name { get; set; }
    }

    internal sealed class EtkSearchResult
    {
        public long id { get; set; }
        public string type { get; set; }
        public string title { get; set; }
        public int rank { get; set; }
    }

    internal sealed class EtkSearchResponse
    {
        public bool ready { get; set; }
        public EtkSearchResult[] items { get; set; }
    }

    internal sealed class EtkSearchIndexRequest
    {
        public string mode { get; set; }
        public string token { get; set; }
        public EtkSearchDocument[] items { get; set; }
    }

    internal sealed class StrmAssistantConfiguration
    {
        public StrmAssistantModOptions ModOptions { get; set; }
    }

    internal sealed class StrmAssistantModOptions
    {
        public bool EnhanceChineseSearch { get; set; }
        public bool EnhanceChineseSearchApply { get; set; }
    }

    internal static class StrmAssistantSearchCompatibility
    {
        private static readonly object SyncRoot = new object();
        private static DateTime lastWriteUtc;
        private static bool hasCachedValue;
        private static bool enabled;

        public static bool IsEnabled(
            string pluginConfigurationsPath,
            IJsonSerializer serializer,
            ILogger logger)
        {
            var path = Path.Combine(pluginConfigurationsPath ?? string.Empty, "Strm Assistant.json");
            if (!File.Exists(path))
            {
                return false;
            }
            var currentWriteUtc = File.GetLastWriteTimeUtc(path);
            lock (SyncRoot)
            {
                if (hasCachedValue && currentWriteUtc == lastWriteUtc)
                {
                    return enabled;
                }
                try
                {
                    var config = serializer.DeserializeFromString<StrmAssistantConfiguration>(
                        File.ReadAllText(path));
                    var nextEnabled = config?.ModOptions?.EnhanceChineseSearch == true
                        || config?.ModOptions?.EnhanceChineseSearchApply == true;
                    if (!hasCachedValue || enabled != nextEnabled)
                    {
                        logger.Info(
                            nextEnabled
                                ? "ETK Chinese search is yielding to Strm Assistant Chinese search."
                                : "ETK Chinese search is active; Strm Assistant Chinese search is disabled.",
                            Array.Empty<object>());
                    }
                    enabled = nextEnabled;
                    lastWriteUtc = currentWriteUtc;
                    hasCachedValue = true;
                }
                catch (Exception ex)
                {
                    logger.Debug("ETK could not read Strm Assistant search configuration: {0}", ex.Message);
                }
                return enabled;
            }
        }
    }

    internal static class EtkSearchIndexClient
    {
        private static readonly HttpClient SearchHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(250)
        };
        private static readonly HttpClient IndexHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public static async Task<EtkSearchResponse> SearchAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            string query,
            string[] itemTypes,
            CancellationToken cancellationToken)
        {
            var origin = EtkMetadataClient.GetEtkOrigin(libraryManager);
            if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(query))
            {
                return null;
            }
            var url = origin + "/api/emby/search?query="
                + Uri.EscapeDataString(query.Trim())
                + "&limit=300";
            if (itemTypes != null && itemTypes.Length > 0)
            {
                url += "&types=" + Uri.EscapeDataString(string.Join(",", itemTypes));
            }
            using (var response = await SearchHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return serializer.DeserializeFromString<EtkSearchResponse>(json);
            }
        }

        public static async Task<bool> RebuildAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var origin = EtkMetadataClient.GetEtkOrigin(libraryManager);
            if (string.IsNullOrWhiteSpace(origin))
            {
                return false;
            }
            var token = Guid.NewGuid().ToString("N");
            if (!await PostAsync(serializer, origin, new EtkSearchIndexRequest
            {
                mode = "start",
                token = token
            }, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var items = libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[]
                    {
                        "Movie", "Series", "Season", "Episode", "BoxSet", "Person",
                        "MusicArtist", "MusicAlbum", "Audio", "Video", "Playlist"
                    }
                });
                var batch = new List<EtkSearchDocument>(500);
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = ToDocument(item);
                    if (document == null)
                    {
                        continue;
                    }
                    batch.Add(document);
                    if (batch.Count >= 500)
                    {
                        if (!await PostBatchAsync(serializer, origin, token, batch, cancellationToken)
                            .ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("ETK search index batch failed.");
                        }
                        batch.Clear();
                    }
                }
                if (batch.Count > 0)
                {
                    if (!await PostBatchAsync(serializer, origin, token, batch, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("ETK search index batch failed.");
                    }
                }
                if (!await PostAsync(serializer, origin, new EtkSearchIndexRequest
                {
                    mode = "complete",
                    token = token
                }, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("ETK search index completion failed.");
                }
                logger.Info("ETK Chinese search index rebuild completed.", Array.Empty<object>());
                return true;
            }
            catch (Exception ex)
            {
                await PostAsync(serializer, origin, new EtkSearchIndexRequest
                {
                    mode = "abort",
                    token = token
                }, CancellationToken.None).ConfigureAwait(false);
                logger.Warn("ETK Chinese search index rebuild failed: {0}", ex.Message);
                return false;
            }
        }

        public static Task UpsertAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            BaseItem item)
        {
            var origin = EtkMetadataClient.GetEtkOrigin(libraryManager);
            var document = ToDocument(item);
            if (string.IsNullOrWhiteSpace(origin) || document == null)
            {
                return Task.CompletedTask;
            }
            return PostAsync(serializer, origin, new EtkSearchIndexRequest
            {
                mode = "incremental",
                items = new[] { document }
            }, CancellationToken.None);
        }

        public static Task DeleteAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            long itemId)
        {
            var origin = EtkMetadataClient.GetEtkOrigin(libraryManager);
            if (string.IsNullOrWhiteSpace(origin) || itemId <= 0)
            {
                return Task.CompletedTask;
            }
            return PostAsync(serializer, origin, new EtkSearchIndexRequest
            {
                mode = "delete",
                items = new[] { new EtkSearchDocument { id = itemId } }
            }, CancellationToken.None);
        }

        private static async Task<bool> PostBatchAsync(
            IJsonSerializer serializer,
            string origin,
            string token,
            List<EtkSearchDocument> batch,
            CancellationToken cancellationToken)
        {
            return await PostAsync(serializer, origin, new EtkSearchIndexRequest
            {
                mode = "batch",
                token = token,
                items = batch.ToArray()
            }, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> PostAsync(
            IJsonSerializer serializer,
            string origin,
            EtkSearchIndexRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var content = new StringContent(
                    serializer.SerializeToString(request),
                    Encoding.UTF8,
                    "application/json"))
                using (var response = await IndexHttpClient.PostAsync(
                    origin + "/api/emby/search/index",
                    content,
                    cancellationToken).ConfigureAwait(false))
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        private static EtkSearchDocument ToDocument(BaseItem item)
        {
            if (item == null || item.InternalId <= 0 || string.IsNullOrWhiteSpace(item.Name))
            {
                return null;
            }
            return new EtkSearchDocument
            {
                id = item.InternalId,
                type = item.GetType().Name,
                title = item.Name,
                original_title = Convert.ToString(GetProperty(item, "OriginalTitle")) ?? string.Empty,
                series_name = Convert.ToString(GetProperty(item, "SeriesName")) ?? string.Empty
            };
        }

        private static object GetProperty(object instance, string name)
        {
            return instance?.GetType().GetProperty(name)?.GetValue(instance);
        }
    }

    internal static class EtkSearchInterceptor
    {
        private const string HarmonyId = "ETKMediaInfoBridge.ChineseSearch";
        private static readonly object SyncRoot = new object();
        private static Harmony harmony;
        private static readonly List<System.Reflection.MethodBase> PatchedMethods =
            new List<System.Reflection.MethodBase>();
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, EtkSearchResponse>> Cache =
            new ConcurrentDictionary<string, Tuple<DateTime, EtkSearchResponse>>();
        private static ILibraryManager libraryManager;
        private static IJsonSerializer serializer;
        private static ILogger logger;
        private static string pluginConfigurationsPath;

        public static void Install(
            ILibraryManager manager,
            string configurationsPath,
            IJsonSerializer jsonSerializer,
            ILogger pluginLogger)
        {
            lock (SyncRoot)
            {
                if (harmony != null)
                {
                    return;
                }
                libraryManager = manager;
                pluginConfigurationsPath = configurationsPath;
                serializer = jsonSerializer;
                logger = pluginLogger;
                StrmAssistantSearchCompatibility.IsEnabled(
                    pluginConfigurationsPath,
                    serializer,
                    logger);
                harmony = new Harmony(HarmonyId);
                foreach (var method in manager.GetType().GetMethods(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic))
                {
                    var parameters = method.GetParameters();
                    if ((method.Name != "GetItemList"
                            && method.Name != "GetItems"
                            && method.Name != "GetItemsResult")
                        || parameters.Length == 0
                        || parameters[0].ParameterType != typeof(InternalItemsQuery))
                    {
                        continue;
                    }
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(typeof(EtkSearchInterceptor), nameof(BeforeGetItemList)));
                    PatchedMethods.Add(method);
                }
                if (PatchedMethods.Count == 0)
                {
                    harmony = null;
                    pluginLogger.Warn("ETK Chinese search hook was not installed: compatible query methods were not found.");
                    return;
                }
                pluginLogger.Info("ETK search recommendation suppression is active.", Array.Empty<object>());
                pluginLogger.Info("ETK Chinese search hook is active.", Array.Empty<object>());
            }
        }

        public static void Uninstall()
        {
            lock (SyncRoot)
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
                Cache.Clear();
                harmony = null;
                libraryManager = null;
                serializer = null;
                logger = null;
                pluginConfigurationsPath = null;
            }
        }

        private static void BeforeGetItemList(InternalItemsQuery __0)
        {
            try
            {
                var query = __0?.SearchTerm;
                if (string.IsNullOrWhiteSpace(query))
                {
                    if (IsSearchRecommendationQuery(__0))
                    {
                        __0.ItemIds = new[] { long.MaxValue };
                    }
                    return;
                }
                if (StrmAssistantSearchCompatibility.IsEnabled(
                    pluginConfigurationsPath,
                    serializer,
                    logger))
                {
                    return;
                }
                var itemTypes = IncludeBoxSetsInCombinedSearch(__0.IncludeItemTypes);
                var cacheKey = query.Trim() + "|" + string.Join(",", itemTypes ?? Array.Empty<string>());
                EtkSearchResponse response = null;
                if (Cache.TryGetValue(cacheKey, out var cached)
                    && cached.Item1 > DateTime.UtcNow.AddSeconds(-2))
                {
                    response = cached.Item2;
                }
                else
                {
                    response = EtkSearchIndexClient.SearchAsync(
                        serializer,
                        libraryManager,
                        query,
                        itemTypes,
                        CancellationToken.None).GetAwaiter().GetResult();
                    if (response != null)
                    {
                        if (Cache.Count >= 500)
                        {
                            Cache.Clear();
                        }
                        Cache[cacheKey] = Tuple.Create(DateTime.UtcNow, response);
                    }
                }
                if (response?.ready != true)
                {
                    return;
                }
                var responseItems = response.items ?? Array.Empty<EtkSearchResult>();
                if ((itemTypes == null || itemTypes.Length == 0) && responseItems.Length > 0)
                {
                    itemTypes = responseItems
                        .Select(item => item.type)
                        .Where(type => !string.IsNullOrWhiteSpace(type))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                var ids = responseItems
                        .Select(item => item.id)
                        .Where(id => id > 0)
                        .Distinct()
                        .ToArray();
                if (__0.ItemIds != null && __0.ItemIds.Length > 0)
                {
                    var allowed = new HashSet<long>(__0.ItemIds);
                    ids = ids.Where(allowed.Contains).ToArray();
                }
                __0.ItemIds = ids.Length > 0 ? ids : new[] { long.MaxValue };
                __0.IncludeItemTypes = itemTypes;
                __0.SearchTerm = null;
            }
            catch (Exception ex)
            {
                logger?.Debug("ETK Chinese search fallback to Emby: {0}", ex.Message);
            }
        }

        private static bool IsSearchRecommendationQuery(InternalItemsQuery query)
        {
            if (query == null
                || !string.Equals(query.QueryName, "ItemsService.GetItems", StringComparison.Ordinal)
                || !query.Recursive
                || query.Limit != 20
                || query.EnableTotalRecordCount)
            {
                return false;
            }
            var itemTypes = new HashSet<string>(
                query.IncludeItemTypes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (itemTypes.Count != 3
                || !itemTypes.Contains("Movie")
                || !itemTypes.Contains("Series")
                || !itemTypes.Contains("MusicArtist"))
            {
                return false;
            }
            var orderBy = query.OrderBy ?? Array.Empty<(string, MediaBrowser.Model.Entities.SortOrder)>();
            return orderBy.Length == 2
                && orderBy.Any(value => string.Equals(
                    value.Item1,
                    "IsFavoriteOrLiked",
                    StringComparison.OrdinalIgnoreCase))
                && orderBy.Any(value => string.Equals(
                    value.Item1,
                    "Random",
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string[] IncludeBoxSetsInCombinedSearch(string[] itemTypes)
        {
            if (itemTypes == null
                || itemTypes.Length == 0
                || itemTypes.Contains("BoxSet", StringComparer.OrdinalIgnoreCase)
                || !itemTypes.Contains("Movie", StringComparer.OrdinalIgnoreCase)
                || !itemTypes.Contains("Series", StringComparer.OrdinalIgnoreCase))
            {
                return itemTypes;
            }
            return itemTypes.Concat(new[] { "BoxSet" }).ToArray();
        }

    }

    public sealed class EtkSearchIndexEntryPoint : IServerEntryPoint, IDisposable
    {
        private readonly ILibraryManager libraryManager;
        private readonly MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths;
        private readonly MediaBrowser.Model.Serialization.IJsonSerializer serializer;
        private readonly MediaBrowser.Model.Logging.ILogger logger;
        private bool disposed;

        public EtkSearchIndexEntryPoint(
            ILibraryManager libraryManager,
            MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths,
            MediaBrowser.Model.Serialization.IJsonSerializer serializer,
            MediaBrowser.Model.Logging.ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.applicationPaths = applicationPaths;
            this.serializer = serializer;
            this.logger = logger;
        }

        public void Run()
        {
            Plugin.EnsureDependenciesLoaded();
            EtkMetadataClient.LoadEtkOrigin(this.applicationPaths.PluginConfigurationsPath);
            EtkSearchInterceptor.Install(
                this.libraryManager,
                this.applicationPaths.PluginConfigurationsPath,
                this.serializer,
                this.logger);
            this.libraryManager.ItemAdded += this.OnItemChanged;
            this.libraryManager.ItemUpdated += this.OnItemChanged;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            _ = Task.Run(this.RebuildWhenReadyAsync);
        }

        private async Task RebuildWhenReadyAsync()
        {
            while (!this.disposed)
            {
                if (!string.IsNullOrWhiteSpace(EtkMetadataClient.GetEtkOrigin(this.libraryManager)))
                {
                    if (await EtkSearchIndexClient.RebuildAsync(
                        this.serializer,
                        this.libraryManager,
                        this.logger,
                        CancellationToken.None).ConfigureAwait(false))
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    continue;
                }
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        private void OnItemChanged(object sender, ItemChangeEventArgs eventArgs)
        {
            var item = eventArgs?.Item;
            if (item != null)
            {
                _ = EtkSearchIndexClient.UpsertAsync(this.serializer, this.libraryManager, item);
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs eventArgs)
        {
            if (!this.disposed && eventArgs?.Item?.InternalId > 0)
            {
                _ = EtkSearchIndexClient.DeleteAsync(
                    this.serializer,
                    this.libraryManager,
                    eventArgs.Item.InternalId);
            }
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            this.libraryManager.ItemAdded -= this.OnItemChanged;
            this.libraryManager.ItemUpdated -= this.OnItemChanged;
            this.libraryManager.ItemRemoved -= this.OnItemRemoved;
            EtkSearchInterceptor.Uninstall();
        }
    }
}
