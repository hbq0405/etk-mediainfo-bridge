using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace ETKMediaInfoBridge
{
    internal static class MediaInfoRefreshGuard
    {
        private static readonly ConcurrentDictionary<long, DateTime> SuppressedUntil =
            new ConcurrentDictionary<long, DateTime>();

        public static void Suppress(long itemId)
        {
            SuppressedUntil[itemId] = DateTime.UtcNow.AddSeconds(5);
        }

        public static bool IsSuppressed(long itemId)
        {
            if (!SuppressedUntil.TryGetValue(itemId, out var until))
            {
                return false;
            }
            if (until > DateTime.UtcNow)
            {
                return true;
            }
            SuppressedUntil.TryRemove(itemId, out _);
            return false;
        }
    }

    internal static class EtkCollectionRestorer
    {
        private static readonly SemaphoreSlim Slot = new SemaphoreSlim(1, 1);

        public static async Task RestoreAsync(
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IJsonSerializer jsonSerializer,
            ILogger logger,
            long itemId,
            EtkMetadataCollection[] knownCollections = null)
        {
            var movie = libraryManager.GetItemById(itemId) as Movie;
            if (movie == null)
            {
                return;
            }
            var libraryOptions = libraryManager.GetLibraryOptions(movie);
            if (libraryOptions == null || !libraryOptions.ImportCollections)
            {
                return;
            }

            var collections = knownCollections;
            if (collections == null)
            {
                var payload = await EtkMetadataClient.GetAsync(
                    jsonSerializer,
                    movie.Path,
                    "Movie",
                    null,
                    null,
                    CancellationToken.None,
                    libraryManager).ConfigureAwait(false);
                collections = payload?.collections;
            }
            if (collections == null || collections.Length == 0)
            {
                return;
            }

            await Slot.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                foreach (var collection in collections)
                {
                    if (string.IsNullOrWhiteSpace(collection.name)
                        || string.IsNullOrWhiteSpace(collection.tmdb_id))
                    {
                        continue;
                    }
                    var boxSet = libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = new[] { "BoxSet" }
                    }).OfType<BoxSet>().FirstOrDefault(item =>
                        item.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                        && string.Equals(tmdbId, collection.tmdb_id, StringComparison.Ordinal));
                    if (boxSet == null)
                    {
                        var itemIds = GetCollectionItemIds(libraryManager, movie, collection);
                        var minimumItems = Math.Max(1, libraryOptions.MinCollectionItems);
                        if (itemIds.Length < minimumItems)
                        {
                            logger.Info(
                                "ETK Collections skipped {0} (Tmdb={1}): {2}/{3} items in this library.",
                                collection.name,
                                collection.tmdb_id,
                                itemIds.Length,
                                minimumItems);
                            continue;
                        }
                        boxSet = await collectionManager.CreateCollection(
                            new CollectionCreationOptions
                            {
                                Name = collection.name,
                                ProviderIds = new ProviderIdDictionary
                                {
                                    ["Tmdb"] = collection.tmdb_id
                                },
                                ItemIdList = itemIds
                            }).ConfigureAwait(false);
                        logger.Info(
                            "ETK Collections created {0} (Tmdb={1}) for Item {2}.",
                            collection.name,
                            collection.tmdb_id,
                            movie.InternalId);
                    }
                    else if (movie.CollectionFolders == null
                        || !movie.CollectionFolders.Any(item => item.InternalId == boxSet.InternalId))
                    {
                        await collectionManager.AddToCollection(
                            boxSet.InternalId,
                            new[] { movie.InternalId }).ConfigureAwait(false);
                        logger.Info(
                            "ETK Collections added Item {0} to {1} (Tmdb={2}).",
                            movie.InternalId,
                            boxSet.Name,
                            collection.tmdb_id);
                    }
                }
            }
            finally
            {
                Slot.Release();
            }
        }

        private static long[] GetCollectionItemIds(
            ILibraryManager libraryManager,
            Movie sourceMovie,
            EtkMetadataCollection collection)
        {
            var memberIds = new HashSet<string>(
                collection.member_tmdb_ids ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            if (memberIds.Count == 0)
            {
                return new[] { sourceMovie.InternalId };
            }

            var sourceFolderIds = new HashSet<long>(
                libraryManager.GetCollectionFolders(sourceMovie).Select(item => item.InternalId));
            return libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie" }
                })
                .OfType<Movie>()
                .Where(item => item.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                    && memberIds.Contains(tmdbId)
                    && (sourceFolderIds.Count == 0
                        || libraryManager.GetCollectionFolders(item)
                            .Any(folder => sourceFolderIds.Contains(folder.InternalId))))
                .Select(item => item.InternalId)
                .Distinct()
                .ToArray();
        }
    }

    public sealed class MediaInfoRefreshEntryPoint : IServerEntryPoint, IDisposable
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private static readonly SemaphoreSlim RestoreSlots = new SemaphoreSlim(4, 4);

        private readonly ILibraryManager libraryManager;
        private readonly IApplicationPaths applicationPaths;
        private readonly ICollectionManager collectionManager;
        private readonly IItemRepository itemRepository;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ITaskManager taskManager;
        private readonly IProviderManager providerManager;
        private readonly IDirectoryService directoryService;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> pending =
            new ConcurrentDictionary<long, CancellationTokenSource>();
        private readonly ConcurrentDictionary<long, DateTime> replaceImagesUntil =
            new ConcurrentDictionary<long, DateTime>();
        private bool disposed;

        public MediaInfoRefreshEntryPoint(
            ILibraryManager libraryManager,
            IApplicationPaths applicationPaths,
            ICollectionManager collectionManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            ITaskManager taskManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.applicationPaths = applicationPaths;
            this.collectionManager = collectionManager;
            this.itemRepository = itemRepository;
            this.jsonSerializer = jsonSerializer;
            this.taskManager = taskManager;
            this.providerManager = providerManager;
            this.directoryService = new DirectoryService(fileSystem);
            this.logger = logger;
        }

        public void Run()
        {
            Plugin.EnsureDependenciesLoaded();
            EtkMetadataClient.LoadEtkOrigin(this.applicationPaths.PluginConfigurationsPath);
            DeepDeleteInterceptor.Install(this.libraryManager, this.jsonSerializer, this.logger);
            ManualImageSearchInterceptor.Install(this.logger);
            RefreshItemInterceptor.Install(
                this.OnRefreshStarting,
                this.OnRefreshRequested,
                this.logger);
            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            this.taskManager.TaskCompleted += this.OnTaskCompleted;
            _ = Task.Run(this.RefreshIntroSnapshotsAsync);
            this.logger.Info("ETK MediaInfo refresh restore is active.", Array.Empty<object>());
        }

        private void OnTaskCompleted(object sender, TaskCompletionEventArgs eventArgs)
        {
            if (string.Equals(eventArgs?.Task?.Name, "Extract Intro Fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(this.RefreshIntroSnapshotsAsync);
            }
        }

        private async Task RefreshIntroSnapshotsAsync()
        {
            try
            {
                var episodes = this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" }
                });
                var captured = 0;
                var notified = 0;
                var failed = 0;
                foreach (var episode in episodes)
                {
                    var chapters = this.itemRepository.GetChapters(
                        episode.InternalId,
                        IntroChapterSnapshotStore.MarkerTypes,
                        CancellationToken.None);
                    if (IntroChapterSnapshotStore.Store(episode.InternalId, chapters))
                    {
                        captured++;
                        try
                        {
                            var mediaInfoUrl = EtkMetadataClient.ResolveMediaInfoUrl(episode.Path);
                            if (string.IsNullOrEmpty(mediaInfoUrl))
                            {
                                continue;
                            }
                            if (await this.TryNotifyIntroAsync(
                                episode.InternalId,
                                mediaInfoUrl,
                                chapters).ConfigureAwait(false))
                            {
                                notified++;
                            }
                            else
                            {
                                failed++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            this.logger.Debug(
                                "ETK MediaInfo intro snapshot notify failed for Item {0}: {1}",
                                episode.InternalId,
                                ex.Message);
                        }
                    }
                }
                this.logger.Info(
                    "ETK MediaInfo captured {0} intro snapshots and notified ETK for {1} episodes ({2} failed).",
                    captured,
                    notified,
                    failed);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ETK MediaInfo intro snapshot refresh failed.", ex);
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs eventArgs)
        {
            var item = eventArgs?.Item;
            if (this.disposed || item == null || MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }

            this.ScheduleRestoreTree(item);
        }

        private void OnRefreshRequested(long itemId)
        {
            if (this.disposed)
            {
                return;
            }
            this.ScheduleRestoreTree(this.libraryManager.GetItemById(itemId));
        }

        private void OnRefreshStarting(long itemId, bool replaceAllImages)
        {
            if (this.disposed || !replaceAllImages)
            {
                return;
            }
            var item = this.libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return;
            }
            var itemType = item is Movie ? "Movie"
                : item is Series ? "Series"
                : item is Season ? "Season"
                : item is Episode ? "Episode"
                : item is BoxSet ? "BoxSet"
                : null;
            if (itemType == null)
            {
                return;
            }
            var seasonNumber = item is Season
                ? EtkMetadataClient.ResolveSeasonNumber(item.Path, item.IndexNumber)
                : item.ParentIndexNumber;
            var episodeNumber = item is Episode ? item.IndexNumber : null;
            item.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
            var refreshed = EtkMetadataClient.RefreshImagesAsync(
                item.Path,
                itemType,
                seasonNumber,
                episodeNumber,
                tmdbId,
                CancellationToken.None,
                this.libraryManager).GetAwaiter().GetResult();
            if (refreshed)
            {
                this.replaceImagesUntil[itemId] = DateTime.UtcNow.AddMinutes(1);
                this.logger.Info(
                    "ETK Images refreshed the TMDb image snapshot before replacing images for Item {0}.",
                    itemId);
            }
            else
            {
                this.logger.Warn(
                    "ETK Images could not refresh the TMDb image snapshot before replacing images for Item {0}.",
                    itemId);
            }
        }

        private void ScheduleRestoreTree(BaseItem item)
        {
            if (item == null || MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }
            var itemType = item.GetType().Name;
            if (string.Equals(itemType, "Series", StringComparison.Ordinal)
                || string.Equals(itemType, "Season", StringComparison.Ordinal))
            {
                this.ScheduleRestore(item, imagesOnly: true);
                foreach (var episode in this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    Parent = item,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" }
                }))
                {
                    this.ScheduleRestore(episode);
                }
                return;
            }

            this.ScheduleRestore(item);
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs eventArgs)
        {
            var item = eventArgs?.Item;
            if (this.disposed || item == null || MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }
            this.ScheduleRestore(item, dropConflictingExternalStreams: true);
        }

        private void ScheduleRestore(
            BaseItem item,
            bool dropConflictingExternalStreams = false,
            bool imagesOnly = false)
        {
            if (item == null || MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }

            string mediaInfoUrl;
            try
            {
                mediaInfoUrl = EtkMetadataClient.ResolveMediaInfoUrl(item.Path);
            }
            catch (Exception ex)
            {
                this.logger.Debug(
                    "ETK MediaInfo ignored unreadable STRM for Item {0}: {1}",
                    item.InternalId,
                    ex.Message);
                return;
            }
            if (string.IsNullOrEmpty(mediaInfoUrl))
            {
                var itemType = item.GetType().Name;
                if (!string.Equals(itemType, "Movie", StringComparison.Ordinal)
                    && !string.Equals(itemType, "Series", StringComparison.Ordinal)
                    && !string.Equals(itemType, "Season", StringComparison.Ordinal)
                    && !string.Equals(itemType, "Episode", StringComparison.Ordinal))
                {
                    return;
                }
            }

            var cancellation = new CancellationTokenSource();
            this.pending.AddOrUpdate(
                item.InternalId,
                cancellation,
                (_, previous) =>
                {
                    try
                    {
                        previous.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    return cancellation;
                });
            _ = this.RestoreAfterRefreshAsync(
                item.InternalId,
                mediaInfoUrl,
                cancellation,
                dropConflictingExternalStreams,
                imagesOnly);
        }

        private async Task RestoreAfterRefreshAsync(
            long itemId,
            string mediaInfoUrl,
            CancellationTokenSource cancellation,
            bool dropConflictingExternalStreams,
            bool imagesOnly)
        {
            var slotAcquired = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token).ConfigureAwait(false);
                await RestoreSlots.WaitAsync(cancellation.Token).ConfigureAwait(false);
                slotAcquired = true;
                var item = this.libraryManager.GetItemById(itemId);
                if (item is Episode && !string.IsNullOrEmpty(mediaInfoUrl))
                {
                    var chapters = this.itemRepository.GetChapters(
                        itemId,
                        IntroChapterSnapshotStore.MarkerTypes,
                        CancellationToken.None);
                    IntroChapterSnapshotStore.Store(itemId, chapters);
                    if (IntroChapterSnapshotStore.NeedsSync(itemId, chapters))
                    {
                        await this.TryNotifyIntroAsync(itemId, mediaInfoUrl, chapters).ConfigureAwait(false);
                    }
                }
                if (!imagesOnly && !string.IsNullOrEmpty(mediaInfoUrl))
                {
                    using (var response = await HttpClient.GetAsync(mediaInfoUrl, cancellation.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            this.logger.Debug(
                                "ETK MediaInfo cache request skipped for Item {0}: HTTP {1}",
                                itemId,
                                (int)response.StatusCode);
                            return;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var payload = this.jsonSerializer.DeserializeFromString<ApplyEtkMediaInfo>(json);
                        var result = MediaInfoImporter.Apply(
                            this.libraryManager,
                            this.itemRepository,
                            itemId,
                            payload?.MediaSourceInfo,
                            payload?.Chapters,
                            IntroChapterSnapshotStore.Get(itemId),
                            dropConflictingExternalStreams);
                        this.logger.Info(
                            "ETK MediaInfo restored after refresh for Item {0}: {1} streams.",
                            itemId,
                            result.StreamCount);
                        await this.NotifyItemReadyAsync(mediaInfoUrl, itemId).ConfigureAwait(false);
                    }
                }
                await this.RestoreImagesAsync(
                    itemId,
                    this.ShouldReplaceImages(itemId),
                    CancellationToken.None).ConfigureAwait(false);
                await EtkCollectionRestorer.RestoreAsync(
                    this.libraryManager,
                    this.collectionManager,
                    this.jsonSerializer,
                    this.logger,
                    itemId).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ETK MediaInfo restore after refresh failed for Item " + itemId,
                    ex);
            }
            finally
            {
                if (slotAcquired)
                {
                    RestoreSlots.Release();
                }
                if (this.pending.TryGetValue(itemId, out var current) && ReferenceEquals(current, cancellation))
                {
                    this.pending.TryRemove(itemId, out _);
                }
                cancellation.Dispose();
            }
        }

        private async Task<bool> TryNotifyIntroAsync(
            long itemId,
            string mediaInfoUrl,
            IEnumerable<ChapterInfo> chapters)
        {
            if (string.IsNullOrEmpty(mediaInfoUrl))
            {
                return false;
            }
            try
            {
                using (var response = await HttpClient.PostAsync(
                    BuildCallbackUrl(mediaInfoUrl, "intro-sync", itemId),
                    new StringContent(string.Empty)).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        this.logger.Debug(
                            "ETK MediaInfo intro snapshot notify returned HTTP {0} for Item {1}.",
                            (int)response.StatusCode,
                            itemId);
                        return false;
                    }
                }
                IntroChapterSnapshotStore.MarkSynced(itemId, chapters);
                this.logger.Info(
                    "ETK MediaInfo notified ETK of an intro update for Item {0}.",
                    itemId);
                return true;
            }
            catch (Exception ex)
            {
                this.logger.Debug(
                    "ETK MediaInfo intro snapshot notify failed for Item {0}: {1}",
                    itemId,
                    ex.Message);
                return false;
            }
        }

        private bool ShouldReplaceImages(long itemId)
        {
            return this.replaceImagesUntil.TryRemove(itemId, out var until)
                && until > DateTime.UtcNow;
        }

        private async Task RestoreImagesAsync(
            long itemId,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            var item = this.libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return;
            }

            var itemType = item.GetType().Name;
            if (!string.Equals(itemType, "Movie", StringComparison.Ordinal)
                && !string.Equals(itemType, "Series", StringComparison.Ordinal)
                && !string.Equals(itemType, "Season", StringComparison.Ordinal)
                && !string.Equals(itemType, "Episode", StringComparison.Ordinal)
                && !string.Equals(itemType, "BoxSet", StringComparison.Ordinal))
            {
                return;
            }

            EtkMetadataPayload payload;
            if (string.Equals(itemType, "BoxSet", StringComparison.Ordinal))
            {
                item.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
                payload = await EtkMetadataClient.GetCollectionAsync(
                    this.jsonSerializer,
                    this.libraryManager,
                    tmdbId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                payload = await EtkMetadataClient.GetAsync(
                    this.jsonSerializer,
                    item.Path,
                    itemType,
                    string.Equals(itemType, "Season", StringComparison.Ordinal)
                        ? EtkMetadataClient.ResolveSeasonNumber(item.Path, item.IndexNumber)
                        : item.ParentIndexNumber,
                    string.Equals(itemType, "Episode", StringComparison.Ordinal) ? item.IndexNumber : null,
                    cancellationToken,
                    this.libraryManager).ConfigureAwait(false);
            }
            if (payload?.images == null)
            {
                return;
            }

            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            var restored = 0;
            restored += await this.SaveImageAsync(
                item, libraryOptions, payload.images.primary, ImageType.Primary,
                replaceExisting, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(itemType, "Season", StringComparison.Ordinal)
                && !string.Equals(itemType, "Episode", StringComparison.Ordinal)
                && !string.Equals(itemType, "BoxSet", StringComparison.Ordinal))
            {
                restored += await this.SaveImageAsync(
                    item, libraryOptions, payload.images.backdrop, ImageType.Backdrop,
                    replaceExisting, cancellationToken).ConfigureAwait(false);
                restored += await this.SaveImageAsync(
                    item, libraryOptions, payload.images.logo, ImageType.Logo,
                    replaceExisting, cancellationToken).ConfigureAwait(false);
                restored += await this.SaveImageAsync(
                    item, libraryOptions, payload.images.thumb, ImageType.Thumb,
                    replaceExisting, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(itemType, "Episode", StringComparison.Ordinal))
            {
                restored += await this.SaveImageAsync(
                    item, libraryOptions, payload.images.thumb, ImageType.Thumb,
                    replaceExisting, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(itemType, "BoxSet", StringComparison.Ordinal))
            {
                restored += await this.SaveImageAsync(
                    item, libraryOptions, payload.images.backdrop, ImageType.Backdrop,
                    replaceExisting, cancellationToken).ConfigureAwait(false);
            }

            if (restored > 0)
            {
                this.logger.Info(
                    replaceExisting
                        ? "ETK Images replaced {0} images after refresh for Item {1}."
                        : "ETK Images restored {0} missing images after refresh for Item {1}.",
                    restored,
                    itemId);
            }
        }

        private async Task<int> SaveImageAsync(
            BaseItem item,
            MediaBrowser.Model.Configuration.LibraryOptions libraryOptions,
            string url,
            ImageType imageType,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url)
                || (!replaceExisting && item.HasImage(imageType, 0)))
            {
                return 0;
            }

            try
            {
                await this.providerManager.SaveImage(
                    item,
                    libraryOptions,
                    url,
                    imageType,
                    null,
                    Array.Empty<long>(),
                    this.directoryService,
                    true,
                    cancellationToken).ConfigureAwait(false);
                MediaInfoRefreshGuard.Suppress(item.InternalId);
                item.UpdateToRepository(ItemUpdateType.ImageUpdate);
                return 1;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ETK Images failed to restore " + imageType + " for Item " + item.InternalId,
                    ex);
                return 0;
            }
        }

        private async Task NotifyItemReadyAsync(string mediaInfoUrl, long itemId)
        {
            try
            {
                using (var response = await HttpClient.PostAsync(
                    BuildCallbackUrl(mediaInfoUrl, "item-ready", itemId),
                    new StringContent(string.Empty)).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        this.logger.Debug(
                            "ETK MediaInfo Item {0} ready notification returned HTTP {1}.",
                            itemId,
                            (int)response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Debug(
                    "ETK MediaInfo Item {0} ready notification failed: {1}",
                    itemId,
                    ex.Message);
            }
        }

        private static string BuildCallbackUrl(string mediaInfoUrl, string action, long itemId)
        {
            var queryIndex = mediaInfoUrl.IndexOf('?');
            var path = queryIndex < 0 ? mediaInfoUrl : mediaInfoUrl.Substring(0, queryIndex);
            var query = queryIndex < 0 ? "?" : mediaInfoUrl.Substring(queryIndex) + "&";
            return path.TrimEnd('/') + "/" + action + query + "emby_item_id=" + itemId;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            RefreshItemInterceptor.Uninstall();
            ManualImageSearchInterceptor.Uninstall();
            DeepDeleteInterceptor.Uninstall();
            this.libraryManager.ItemAdded -= this.OnItemAdded;
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
            this.taskManager.TaskCompleted -= this.OnTaskCompleted;
            foreach (var cancellation in this.pending.Values)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            this.pending.Clear();
            this.replaceImagesUntil.Clear();
        }
    }
}
