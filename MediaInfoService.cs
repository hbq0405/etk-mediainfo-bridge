using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace ETKMediaInfoBridge
{
    [Route("/Items/{Id}/ETKMediaInfo", "POST", Summary = "Imports ETK media information")]
    [Authenticated(Roles = "Admin")]
    public sealed class ApplyEtkMediaInfo : IReturn<ApplyEtkMediaInfoResult>
    {
        [ApiMember(Name = "Id", IsRequired = true, DataType = "long", ParameterType = "path", Verb = "POST")]
        public long Id { get; set; }

        public MediaSourceInfo MediaSourceInfo { get; set; }

        public List<ChapterInfo> Chapters { get; set; }
    }

    public sealed class ApplyEtkMediaInfoResult
    {
        public long ItemId { get; set; }

        public int StreamCount { get; set; }

        public int ChapterCount { get; set; }

        public int PreservedExternalStreamCount { get; set; }
    }

    public sealed class MediaInfoService : IService
    {
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;

        public MediaInfoService(ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
        }

        public ApplyEtkMediaInfoResult Post(ApplyEtkMediaInfo request)
        {
            return MediaInfoImporter.Apply(
                this.libraryManager,
                this.itemRepository,
                request.Id,
                request.MediaSourceInfo,
                request.Chapters);
        }
    }

    internal static class MediaInfoImporter
    {
        public static ApplyEtkMediaInfoResult Apply(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            long itemId,
            MediaSourceInfo source,
            List<ChapterInfo> chapters)
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

            foreach (var externalStream in externalStreams)
            {
                if (!streams.Any(stream => IsSameExternalStream(stream, externalStream)))
                {
                    streams.Add(externalStream);
                }
            }
            itemRepository.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);

            chapters = chapters ?? new List<ChapterInfo>();
            itemRepository.SaveChapters(item.InternalId, chapters);

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
                ChapterCount = chapters.Count,
                PreservedExternalStreamCount = externalStreams.Count
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
