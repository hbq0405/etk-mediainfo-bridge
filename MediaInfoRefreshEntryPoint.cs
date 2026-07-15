using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

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
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> pending =
            new ConcurrentDictionary<long, CancellationTokenSource>();
        private bool disposed;

        public MediaInfoRefreshEntryPoint(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.jsonSerializer = jsonSerializer;
            this.logger = logger;
        }

        public void Run()
        {
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            this.logger.Info("ETK MediaInfo refresh restore is active.", Array.Empty<object>());
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs eventArgs)
        {
            var item = eventArgs?.Item;
            if (this.disposed || item == null || MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }

            string mediaInfoUrl;
            try
            {
                mediaInfoUrl = ResolveMediaInfoUrl(item.Path);
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
            _ = this.RestoreAfterRefreshAsync(item.InternalId, mediaInfoUrl, cancellation);
        }

        private async Task RestoreAfterRefreshAsync(
            long itemId,
            string mediaInfoUrl,
            CancellationTokenSource cancellation)
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
                        payload?.Chapters);
                    this.logger.Info(
                        "ETK MediaInfo restored after refresh for Item {0}: {1} streams.",
                        itemId,
                        result.StreamCount);
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

        private static string ResolveMediaInfoUrl(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return null;
            }

            var mediaUrl = itemPath.Trim();
            if (!mediaUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !mediaUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!mediaUrl.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || !File.Exists(mediaUrl))
                {
                    return null;
                }
                mediaUrl = File.ReadAllText(mediaUrl).Trim();
            }

            const string playMarker = "/api/p115/play/";
            var markerIndex = mediaUrl.IndexOf(playMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var value = FirstPathSegment(mediaUrl.Substring(markerIndex + playMarker.Length));
                return string.IsNullOrEmpty(value)
                    ? null
                    : mediaUrl.Substring(0, markerIndex) + "/api/p115/mediainfo/" + value;
            }

            const string virtualMarker = "/api/p115/virtual-play/";
            markerIndex = mediaUrl.IndexOf(virtualMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }
            var virtualPath = mediaUrl.Substring(markerIndex + virtualMarker.Length);
            var separator = virtualPath.IndexOf('/');
            if (separator < 0)
            {
                return null;
            }
            var sha1 = FirstPathSegment(virtualPath.Substring(separator + 1));
            return string.IsNullOrEmpty(sha1)
                ? null
                : mediaUrl.Substring(0, markerIndex) + "/api/p115/mediainfo/sha1/" + sha1;
        }

        private static string FirstPathSegment(string value)
        {
            var end = value.IndexOfAny(new[] { '/', '?', '#' });
            return (end < 0 ? value : value.Substring(0, end)).Trim();
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
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
