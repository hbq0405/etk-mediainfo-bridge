using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace ETKMediaInfoBridge
{
    internal static class EmbyRepositoryCompat
    {
        public static List<ChapterInfo> GetChapters(
            IItemRepository repository,
            BaseItem item,
            MarkerType[] markerTypes = null)
        {
            if (markerTypes != null)
            {
                var modernMarkerMethod = typeof(IItemRepository).GetMethod(
                    "GetChapters",
                    new[] { typeof(long), typeof(MarkerType[]), typeof(CancellationToken) });
                if (modernMarkerMethod != null)
                {
                    return ToList<ChapterInfo>(modernMarkerMethod.Invoke(
                        repository,
                        new object[] { item.InternalId, markerTypes, CancellationToken.None }));
                }

                var legacyMarkerMethod = typeof(IItemRepository).GetMethod(
                    "GetChapters",
                    new[] { typeof(long), typeof(MarkerType[]) });
                if (legacyMarkerMethod != null)
                {
                    return ToList<ChapterInfo>(legacyMarkerMethod.Invoke(
                        repository,
                        new object[] { item.InternalId, markerTypes }));
                }
            }

            var modernMethod = typeof(IItemRepository).GetMethod(
                "GetChapters",
                new[] { typeof(BaseItem), typeof(CancellationToken) });
            if (modernMethod != null)
            {
                return ToList<ChapterInfo>(modernMethod.Invoke(
                    repository,
                    new object[] { item, CancellationToken.None }));
            }

            var legacyMethod = typeof(IItemRepository).GetMethod(
                "GetChapters",
                new[] { typeof(BaseItem) });
            return ToList<ChapterInfo>(legacyMethod?.Invoke(repository, new object[] { item }));
        }

        public static List<MediaStream> GetMediaStreams(
            IItemRepository repository,
            MediaStreamQuery query)
        {
            var modernMethod = typeof(IItemRepository).GetMethod(
                "GetMediaStreams",
                new[] { typeof(MediaStreamQuery), typeof(CancellationToken) });
            if (modernMethod != null)
            {
                return ToList<MediaStream>(modernMethod.Invoke(
                    repository,
                    new object[] { query, CancellationToken.None }));
            }

            var legacyMethod = typeof(IItemRepository).GetMethod(
                "GetMediaStreams",
                new[] { typeof(MediaStreamQuery) });
            return ToList<MediaStream>(legacyMethod?.Invoke(repository, new object[] { query }));
        }

        private static List<T> ToList<T>(object value)
        {
            return value is IEnumerable<T> values ? values.ToList() : new List<T>();
        }
    }

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

    internal static class CreditsChapterSnapshotStore
    {
        private static readonly ConcurrentDictionary<long, List<ChapterInfo>> Snapshots =
            new ConcurrentDictionary<long, List<ChapterInfo>>();

        public static readonly MarkerType[] MarkerTypes = { MarkerType.CreditsStart };

        public static List<ChapterInfo> Get(long itemId)
        {
            return Snapshots.TryGetValue(itemId, out var chapters)
                ? chapters.ToList()
                : new List<ChapterInfo>();
        }

        public static bool Store(long itemId, IEnumerable<ChapterInfo> chapters)
        {
            var creditsChapters = (chapters ?? Enumerable.Empty<ChapterInfo>())
                .Where(IsCreditsChapter)
                .Take(1)
                .ToList();
            if (!HasCredits(creditsChapters))
            {
                return false;
            }
            Snapshots[itemId] = creditsChapters;
            return true;
        }

        public static bool HasCredits(IEnumerable<ChapterInfo> chapters)
        {
            return (chapters ?? Enumerable.Empty<ChapterInfo>()).Any(IsCreditsChapter);
        }

        public static bool IsCreditsChapter(ChapterInfo chapter)
        {
            return chapter != null && chapter.MarkerType == MarkerType.CreditsStart;
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

    [Route("/Items/{Id}/ETKMediaInfo/Intro", "POST", Summary = "Imports ETK intro chapters")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyEtkIntroChapters : IReturn<ApplyEtkIntroChaptersResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "long", ParameterType = "path", Verb = "POST")]
        public long Id { get; set; }

        public long IntroStartTicks { get; set; }

        public long IntroEndTicks { get; set; }
    }

    public sealed class ApplyEtkIntroChaptersResult
    {
        public long ItemId { get; set; }

        public int ChapterCount { get; set; }

        public long IntroStartTicks { get; set; }

        public long IntroEndTicks { get; set; }
    }

    [Route("/Items/{Id}/ETKMediaInfo/Credits", "POST", Summary = "Imports ETK credits chapter")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyEtkCreditsChapter : IReturn<ApplyEtkCreditsChapterResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "long", ParameterType = "path", Verb = "POST")]
        public long Id { get; set; }

        public long CreditsStartTicks { get; set; }
    }

    public sealed class ApplyEtkCreditsChapterResult
    {
        public long ItemId { get; set; }

        public int ChapterCount { get; set; }

        public long CreditsStartTicks { get; set; }
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

        public ApplyEtkIntroChaptersResult Post(ApplyEtkIntroChapters request)
        {
            return MediaInfoImporter.ApplyIntroChapters(
                this.libraryManager,
                this.itemRepository,
                request.Id,
                request.IntroStartTicks,
                request.IntroEndTicks);
        }

        public ApplyEtkCreditsChapterResult Post(ApplyEtkCreditsChapter request)
        {
            return MediaInfoImporter.ApplyCreditsChapter(
                this.libraryManager,
                this.itemRepository,
                request.Id,
                request.CreditsStartTicks);
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
        public static ApplyEtkCreditsChapterResult ApplyCreditsChapter(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            long itemId,
            long creditsStartTicks)
        {
            var item = libraryManager.GetItemById(itemId);
            if (item == null)
            {
                throw new ArgumentException("Emby item was not found.", nameof(itemId));
            }

            long? runTimeTicks = item.RunTimeTicks;
            if (creditsStartTicks < 0
                || (runTimeTicks.HasValue
                    && runTimeTicks.Value > 0
                    && creditsStartTicks >= runTimeTicks.Value))
            {
                throw new ArgumentException("ETK credits chapter ticks are invalid.");
            }

            var chapters = EmbyRepositoryCompat.GetChapters(itemRepository, item)
                .Where(chapter => chapter != null && chapter.MarkerType != MarkerType.CreditsStart)
                .ToList();
            chapters.Add(new ChapterInfo
            {
                MarkerType = MarkerType.CreditsStart,
                StartPositionTicks = creditsStartTicks
            });
            itemRepository.SaveChapters(item.InternalId, chapters);
            CreditsChapterSnapshotStore.Store(item.InternalId, chapters);

            return new ApplyEtkCreditsChapterResult
            {
                ItemId = item.InternalId,
                ChapterCount = chapters.Count,
                CreditsStartTicks = creditsStartTicks
            };
        }

        public static ApplyEtkIntroChaptersResult ApplyIntroChapters(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            long itemId,
            long introStartTicks,
            long introEndTicks)
        {
            var item = libraryManager.GetItemById(itemId);
            if (item == null)
            {
                throw new ArgumentException("Emby item was not found.", nameof(itemId));
            }

            if (introStartTicks < 0 || introEndTicks <= introStartTicks)
            {
                throw new ArgumentException("ETK intro chapter ticks are invalid.");
            }

            var chapters = EmbyRepositoryCompat.GetChapters(itemRepository, item)
                .Where(chapter => !IntroChapterSnapshotStore.IsIntroChapter(chapter))
                .ToList();
            var introChapters = new List<ChapterInfo>
            {
                new ChapterInfo
                {
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartTicks
                },
                new ChapterInfo
                {
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndTicks
                }
            };
            chapters.AddRange(introChapters);
            itemRepository.SaveChapters(item.InternalId, chapters);
            IntroChapterSnapshotStore.Store(item.InternalId, chapters);
            IntroChapterSnapshotStore.MarkSynced(item.InternalId, chapters);

            return new ApplyEtkIntroChaptersResult
            {
                ItemId = item.InternalId,
                ChapterCount = chapters.Count,
                IntroStartTicks = introStartTicks,
                IntroEndTicks = introEndTicks
            };
        }

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
            var externalStreams = EmbyRepositoryCompat.GetMediaStreams(
                    itemRepository,
                    new MediaStreamQuery { ItemId = item.InternalId })
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

            var existingChapters = EmbyRepositoryCompat.GetChapters(itemRepository, item);
            var incomingChapters = (chapters ?? new List<ChapterInfo>())
                .Where(chapter => !IntroChapterSnapshotStore.IsIntroChapter(chapter)
                    && !CreditsChapterSnapshotStore.IsCreditsChapter(chapter))
                .ToList();
            var incomingIntro = (chapters ?? new List<ChapterInfo>())
                .Where(IntroChapterSnapshotStore.IsIntroChapter)
                .ToList();
            var incomingCredits = (chapters ?? new List<ChapterInfo>())
                .Where(CreditsChapterSnapshotStore.IsCreditsChapter)
                .Take(1)
                .ToList();
            var existingIntro = existingChapters
                .Where(IntroChapterSnapshotStore.IsIntroChapter)
                .ToList();
            var existingCredits = existingChapters
                .Where(CreditsChapterSnapshotStore.IsCreditsChapter)
                .Take(1)
                .ToList();
            var snapshotIntro = introSnapshot ?? new List<ChapterInfo>();
            var preservedIntro = IntroChapterSnapshotStore.HasCompleteIntro(existingIntro)
                ? existingIntro
                : IntroChapterSnapshotStore.HasCompleteIntro(snapshotIntro)
                    ? snapshotIntro
                    : IntroChapterSnapshotStore.HasCompleteIntro(incomingIntro)
                        ? incomingIntro
                        : new List<ChapterInfo>();
            var snapshotCredits = CreditsChapterSnapshotStore.Get(item.InternalId);
            var preservedCredits = CreditsChapterSnapshotStore.HasCredits(existingCredits)
                ? existingCredits
                : CreditsChapterSnapshotStore.HasCredits(snapshotCredits)
                    ? snapshotCredits
                    : CreditsChapterSnapshotStore.HasCredits(incomingCredits)
                        ? incomingCredits
                        : new List<ChapterInfo>();
            if (incomingChapters.Count > 0)
            {
                incomingChapters.AddRange(preservedIntro);
                incomingChapters.AddRange(preservedCredits);
                itemRepository.SaveChapters(item.InternalId, incomingChapters);
                existingChapters = incomingChapters;
            }
            else if ((!IntroChapterSnapshotStore.HasCompleteIntro(existingIntro) && preservedIntro.Count > 0)
                || (!CreditsChapterSnapshotStore.HasCredits(existingCredits) && preservedCredits.Count > 0))
            {
                existingChapters = existingChapters
                    .Where(chapter => !IntroChapterSnapshotStore.IsIntroChapter(chapter)
                        && !CreditsChapterSnapshotStore.IsCreditsChapter(chapter))
                    .Concat(preservedIntro)
                    .Concat(preservedCredits)
                    .ToList();
                itemRepository.SaveChapters(item.InternalId, existingChapters);
            }
            IntroChapterSnapshotStore.Store(item.InternalId, existingChapters);
            CreditsChapterSnapshotStore.Store(item.InternalId, existingChapters);

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
