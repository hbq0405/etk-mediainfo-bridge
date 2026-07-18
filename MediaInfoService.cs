using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace ETKMediaInfoBridge
{
    internal static class IntroChapterSnapshotStore
    {
        private static readonly ConcurrentDictionary<long, List<ChapterInfo>> Snapshots =
            new ConcurrentDictionary<long, List<ChapterInfo>>();
        private static readonly ConcurrentDictionary<long, List<ChapterInfo>> SyncedSnapshots =
            new ConcurrentDictionary<long, List<ChapterInfo>>();

        public static readonly MarkerType[] MarkerTypes =
            { MarkerType.IntroStart, MarkerType.IntroEnd };

        public static List<ChapterInfo> Get(long itemId)
        {
            return Snapshots.TryGetValue(itemId, out var chapters)
                ? chapters.ToList()
                : new List<ChapterInfo>();
        }

        public static bool Store(long itemId, IEnumerable<ChapterInfo> chapters)
        {
            var introChapters = (chapters ?? Enumerable.Empty<ChapterInfo>())
                .Where(IsIntroChapter)
                .ToList();
            if (!HasCompleteIntro(introChapters))
            {
                return false;
            }
            Snapshots[itemId] = introChapters;
            return true;
        }

        public static bool NeedsSync(long itemId, IEnumerable<ChapterInfo> chapters)
        {
            var introChapters = (chapters ?? Enumerable.Empty<ChapterInfo>())
                .Where(IsIntroChapter)
                .ToList();
            if (!HasCompleteIntro(introChapters))
            {
                return false;
            }
            if (!SyncedSnapshots.TryGetValue(itemId, out var syncedChapters))
            {
                return true;
            }
            return MarkerTypes.Any(marker =>
                introChapters.First(chapter => chapter.MarkerType == marker).StartPositionTicks
                != syncedChapters.First(chapter => chapter.MarkerType == marker).StartPositionTicks);
        }

        public static void MarkSynced(long itemId, IEnumerable<ChapterInfo> chapters)
        {
            var introChapters = (chapters ?? Enumerable.Empty<ChapterInfo>())
                .Where(IsIntroChapter)
                .ToList();
            if (HasCompleteIntro(introChapters))
            {
                SyncedSnapshots[itemId] = introChapters;
            }
        }

        public static bool HasCompleteIntro(IEnumerable<ChapterInfo> chapters)
        {
            var values = (chapters ?? Enumerable.Empty<ChapterInfo>()).ToList();
            return MarkerTypes.All(marker => values.Any(chapter => chapter.MarkerType == marker));
        }

        public static bool IsIntroChapter(ChapterInfo chapter)
        {
            return chapter != null
                && (chapter.MarkerType == MarkerType.IntroStart || chapter.MarkerType == MarkerType.IntroEnd);
        }
    }

    [Route("/Items/{Id}/ETKMediaInfo", "POST", Summary = "Imports ETK media information")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyEtkMediaInfo : IReturn<ApplyEtkMediaInfoResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "long", ParameterType = "path", Verb = "POST")]
        public long Id { get; set; }

        public MediaSourceInfo MediaSourceInfo { get; set; }

        public List<ChapterInfo> Chapters { get; set; }

        public bool DropConflictingExternalStreams { get; set; }
    }

    public sealed class ApplyEtkMediaInfoResult
    {
        public long ItemId { get; set; }

        public int StreamCount { get; set; }

        public int ChapterCount { get; set; }

        public int PreservedExternalStreamCount { get; set; }
    }

    [Route("/ETKMediaInfo/Origin", "POST", Summary = "Configures the ETK server origin")]
    [Authenticated(Roles = "Admin")]
    public sealed class ConfigureEtkOrigin : IReturn<ConfigureEtkOriginResult>
    {
        public string Url { get; set; }
    }

    public sealed class ConfigureEtkOriginResult
    {
        public bool Configured { get; set; }
    }

    public sealed class MediaInfoService : IService
    {
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly IApplicationPaths applicationPaths;

        public MediaInfoService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IApplicationPaths applicationPaths)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.applicationPaths = applicationPaths;
        }

        public ApplyEtkMediaInfoResult Post(ApplyEtkMediaInfo request)
        {
            return MediaInfoImporter.Apply(
                this.libraryManager,
                this.itemRepository,
                request.Id,
                request.MediaSourceInfo,
                request.Chapters,
                dropConflictingExternalStreams: request.DropConflictingExternalStreams);
        }

        public ConfigureEtkOriginResult Post(ConfigureEtkOrigin request)
        {
            return new ConfigureEtkOriginResult
            {
                Configured = EtkMetadataClient.ConfigureEtkOrigin(
                    request?.Url,
                    this.applicationPaths.PluginConfigurationsPath)
            };
        }
    }

    internal static class MediaInfoImporter
    {
        public static ApplyEtkMediaInfoResult Apply(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            long itemId,
            MediaSourceInfo source,
            List<ChapterInfo> chapters,
            List<ChapterInfo> introSnapshot = null,
            bool dropConflictingExternalStreams = false)
        {
            var item = libraryManager.GetItemById(itemId);
            if (item == null)
            {
                throw new ArgumentException("Emby item was not found.", nameof(itemId));
            }

            if (source == null || source.MediaStreams == null || source.MediaStreams.Count == 0)
            {
                throw new ArgumentException("ETK media information contains no media streams.", nameof(source));
            }

            MediaInfoRefreshGuard.Suppress(item.InternalId);
            var streams = source.MediaStreams.ToList();
            var externalStreams = itemRepository.GetMediaStreams(
                    new MediaStreamQuery { ItemId = item.InternalId },
                    CancellationToken.None)
                .Where(stream => stream.IsExternal)
                .ToList();
            var usedIndexes = new HashSet<int>(streams.Select(stream => stream.Index));
            var nextIndex = usedIndexes.Count == 0 ? 0 : usedIndexes.Max() + 1;
            var preservedExternalStreamCount = 0;

            foreach (var externalStream in externalStreams)
            {
                if (streams.Any(stream => IsSameExternalStream(stream, externalStream)))
                {
                    continue;
                }
                if (usedIndexes.Contains(externalStream.Index))
                {
                    if (dropConflictingExternalStreams)
                    {
                        continue;
                    }
                    while (usedIndexes.Contains(nextIndex))
                    {
                        nextIndex++;
                    }
                    externalStream.Index = nextIndex++;
                }
                usedIndexes.Add(externalStream.Index);
                streams.Add(externalStream);
                preservedExternalStreamCount++;
            }
            itemRepository.SaveMediaStreams(
                item.InternalId,
                new List<MediaStream>(),
                CancellationToken.None);
            itemRepository.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);

            var existingChapters = itemRepository.GetChapters(item, CancellationToken.None)
                ?? new List<ChapterInfo>();
            var incomingChapters = (chapters ?? new List<ChapterInfo>())
                .Where(chapter => !IntroChapterSnapshotStore.IsIntroChapter(chapter))
                .ToList();
            var incomingIntro = (chapters ?? new List<ChapterInfo>())
                .Where(IntroChapterSnapshotStore.IsIntroChapter)
                .ToList();
            var existingIntro = existingChapters
                .Where(IntroChapterSnapshotStore.IsIntroChapter)
                .ToList();
            var snapshotIntro = introSnapshot ?? new List<ChapterInfo>();
            var preservedIntro = IntroChapterSnapshotStore.HasCompleteIntro(existingIntro)
                ? existingIntro
                : IntroChapterSnapshotStore.HasCompleteIntro(snapshotIntro)
                    ? snapshotIntro
                    : IntroChapterSnapshotStore.HasCompleteIntro(incomingIntro)
                        ? incomingIntro
                        : new List<ChapterInfo>();
            if (incomingChapters.Count > 0)
            {
                incomingChapters.AddRange(preservedIntro);
                itemRepository.SaveChapters(item.InternalId, incomingChapters);
                existingChapters = incomingChapters;
            }
            else if (!IntroChapterSnapshotStore.HasCompleteIntro(existingIntro) && preservedIntro.Count > 0)
            {
                existingChapters = existingChapters
                    .Where(chapter => !IntroChapterSnapshotStore.IsIntroChapter(chapter))
                    .Concat(preservedIntro)
                    .ToList();
                itemRepository.SaveChapters(item.InternalId, existingChapters);
            }
            IntroChapterSnapshotStore.Store(item.InternalId, existingChapters);

            item.RunTimeTicks = source.RunTimeTicks;
            if (source.Size.HasValue)
            {
                item.Size = source.Size.Value;
            }
            item.Container = source.Container;
            if (source.Bitrate.HasValue)
            {
                item.TotalBitrate = source.Bitrate.Value;
            }

            var videoStream = streams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video && !stream.IsExternal);
            if (videoStream != null)
            {
                if (videoStream.Width.HasValue)
                {
                    item.Width = videoStream.Width.Value;
                }
                if (videoStream.Height.HasValue)
                {
                    item.Height = videoStream.Height.Value;
                }
            }

            item.UpdateToRepository(ItemUpdateType.MetadataImport);

            return new ApplyEtkMediaInfoResult
            {
                ItemId = item.InternalId,
                StreamCount = streams.Count,
                ChapterCount = existingChapters.Count,
                PreservedExternalStreamCount = preservedExternalStreamCount
            };
        }

        private static bool IsSameExternalStream(MediaStream left, MediaStream right)
        {
            return left.IsExternal
                && right.IsExternal
                && left.Index == right.Index
                && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
