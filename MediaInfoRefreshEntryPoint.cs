using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
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
            if (!SuppressedUntil.TryRemove(itemId, out var until))
            {
                return false;
            }
            return until > DateTime.UtcNow;
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
        private readonly IItemRepository itemRepository;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> pending =
            new ConcurrentDictionary<long, CancellationTokenSource>();
        private bool disposed;

        public MediaInfoRefreshEntryPoint(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            ITaskManager taskManager,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.jsonSerializer = jsonSerializer;
            this.taskManager = taskManager;
            this.logger = logger;
        }

        public void Run()
        {
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
                            using (var response = await HttpClient.PostAsync(
                                BuildCallbackUrl(mediaInfoUrl, "intro-sync", episode.InternalId),
                                new StringContent(string.Empty)).ConfigureAwait(false))
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    notified++;
                                }
                                else
                                {
                                    failed++;
                                }
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

            var itemType = item.GetType().Name;
            if (string.Equals(itemType, "Series", StringComparison.Ordinal)
                || string.Equals(itemType, "Season", StringComparison.Ordinal))
            {
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

        private void ScheduleRestore(BaseItem item, bool dropConflictingExternalStreams = false)
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
                return;
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
                dropConflictingExternalStreams);
        }

        private async Task RestoreAfterRefreshAsync(
            long itemId,
            string mediaInfoUrl,
            CancellationTokenSource cancellation,
            bool dropConflictingExternalStreams)
        {
            var slotAcquired = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token).ConfigureAwait(false);
                await RestoreSlots.WaitAsync(cancellation.Token).ConfigureAwait(false);
                slotAcquired = true;
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
        }
    }
}
