using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace ETKMediaInfoBridge
{
    internal sealed class EtkMetadataImages
    {
        public string primary { get; set; }
        public string backdrop { get; set; }
        public string logo { get; set; }
        public string thumb { get; set; }
    }

    internal sealed class EtkMetadataPerson
    {
        public string name { get; set; }
        public string role { get; set; }
        public string type { get; set; }
        public string tmdb_id { get; set; }
        public string image_url { get; set; }
        public int order { get; set; }
    }

    internal sealed class EtkMetadataCollection
    {
        public string name { get; set; }
        public string tmdb_id { get; set; }
        public string[] member_tmdb_ids { get; set; }
    }

    internal sealed class EtkCollectionActivation
    {
        public string tmdb_collection_id { get; set; }
        public long emby_collection_id { get; set; }
        public string name { get; set; }
    }

    internal sealed class EtkMetadataPayload
    {
        public string item_type { get; set; }
        public string tmdb_id { get; set; }
        public string series_tmdb_id { get; set; }
        public string imdb_id { get; set; }
        public string name { get; set; }
        public string original_title { get; set; }
        public string overview { get; set; }
        public string tagline { get; set; }
        public string premiere_date { get; set; }
        public string end_date { get; set; }
        public int? production_year { get; set; }
        public float? community_rating { get; set; }
        public string official_rating { get; set; }
        public int? runtime_minutes { get; set; }
        public string[] genres { get; set; }
        public string[] tags { get; set; }
        public string[] studios { get; set; }
        public int? season_number { get; set; }
        public int? episode_number { get; set; }
        public bool actors_ready { get; set; }
        public EtkMetadataPerson[] people { get; set; }
        public EtkMetadataCollection[] collections { get; set; }
        public EtkMetadataImages images { get; set; }
    }

    internal sealed class EtkRemoteImageCandidate
    {
        public string type { get; set; }
        public string url { get; set; }
        public string thumbnail_url { get; set; }
        public string language { get; set; }
        public int? width { get; set; }
        public int? height { get; set; }
        public double? community_rating { get; set; }
        public int? vote_count { get; set; }
    }

    internal sealed class EtkRemoteImageSearchResponse
    {
        public EtkRemoteImageCandidate[] images { get; set; }
    }

    internal static class EtkMetadataClient
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private static readonly HttpClient ImageRefreshHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, EtkMetadataPayload>> Cache =
            new ConcurrentDictionary<string, Tuple<DateTime, EtkMetadataPayload>>();
        private static readonly object OriginLock = new object();
        private static string etkOrigin;

        public static bool ConfigureEtkOrigin(string url, string configurationDirectory)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }
            var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            lock (OriginLock)
            {
                etkOrigin = origin;
                if (!string.IsNullOrWhiteSpace(configurationDirectory))
                {
                    Directory.CreateDirectory(configurationDirectory);
                    File.WriteAllText(
                        Path.Combine(configurationDirectory, "etk-mediainfo-bridge-origin.txt"),
                        origin);
                }
            }
            return true;
        }

        public static void LoadEtkOrigin(string configurationDirectory)
        {
            if (string.IsNullOrWhiteSpace(configurationDirectory))
            {
                return;
            }
            var path = Path.Combine(configurationDirectory, "etk-mediainfo-bridge-origin.txt");
            if (File.Exists(path))
            {
                ConfigureEtkOrigin(File.ReadAllText(path).Trim(), null);
            }
        }

        public static string GetEtkOrigin(ILibraryManager libraryManager)
        {
            return ResolveEtkOrigin(libraryManager);
        }

        public static async Task<EtkMetadataPayload> GetAsync(
            IJsonSerializer serializer,
            string itemPath,
            string itemType,
            int? seasonNumber,
            int? episodeNumber,
            CancellationToken cancellationToken,
            ILibraryManager libraryManager = null)
        {
            var mediaInfoUrl = ResolveMediaInfoUrl(itemPath);
            string url;
            if (!string.IsNullOrEmpty(mediaInfoUrl))
            {
                RememberEtkOrigin(mediaInfoUrl);
                url = BuildMetadataUrl(mediaInfoUrl, itemType, seasonNumber, episodeNumber);
            }
            else
            {
                var origin = ResolveEtkOrigin(libraryManager);
                if (string.IsNullOrEmpty(origin))
                {
                    return null;
                }
                url = BuildPhysicalMetadataUrl(
                    origin,
                    itemPath,
                    itemType,
                    seasonNumber,
                    episodeNumber);
            }
            if (Cache.TryGetValue(url, out var cached)
                && cached.Item1 > DateTime.UtcNow.AddSeconds(-1))
            {
                return cached.Item2;
            }
            using (var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payload = serializer.DeserializeFromString<EtkMetadataPayload>(json);
                if (payload != null)
                {
                    RewriteImageUrls(url, payload);
                    Cache[url] = Tuple.Create(DateTime.UtcNow, payload);
                }
                return payload;
            }
        }

        public static async Task<EtkMetadataPayload> GetCollectionAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            string tmdbId,
            CancellationToken cancellationToken)
        {
            var origin = ResolveEtkOrigin(libraryManager);
            if (string.IsNullOrEmpty(origin) || string.IsNullOrWhiteSpace(tmdbId))
            {
                return null;
            }
            var url = origin + "/api/collections/provider/metadata/"
                + Uri.EscapeDataString(tmdbId.Trim());
            using (var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payload = serializer.DeserializeFromString<EtkMetadataPayload>(json);
                if (payload != null)
                {
                    RewriteImageUrls(url, payload);
                }
                return payload;
            }
        }

        public static async Task<bool> NotifyCollectionActivatedAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            string tmdbId,
            long embyCollectionId,
            string name,
            CancellationToken cancellationToken)
        {
            var origin = ResolveEtkOrigin(libraryManager);
            if (string.IsNullOrEmpty(origin) || string.IsNullOrWhiteSpace(tmdbId))
            {
                return false;
            }
            var payload = new EtkCollectionActivation
            {
                tmdb_collection_id = tmdbId.Trim(),
                emby_collection_id = embyCollectionId,
                name = name ?? string.Empty
            };
            using (var content = new StringContent(
                serializer.SerializeToString(payload),
                Encoding.UTF8,
                "application/json"))
            using (var response = await HttpClient.PostAsync(
                origin + "/api/emby/collections/activate",
                content,
                cancellationToken).ConfigureAwait(false))
            {
                return response.IsSuccessStatusCode;
            }
        }

        public static async Task<EtkMetadataPayload[]> SearchCollectionsAsync(
            IJsonSerializer serializer,
            ILibraryManager libraryManager,
            string query,
            CancellationToken cancellationToken)
        {
            var origin = ResolveEtkOrigin(libraryManager);
            if (string.IsNullOrEmpty(origin) || string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<EtkMetadataPayload>();
            }
            var url = origin + "/api/collections/provider/search?query="
                + Uri.EscapeDataString(query.Trim());
            using (var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<EtkMetadataPayload>();
                }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payloads = serializer.DeserializeFromString<EtkMetadataPayload[]>(json)
                    ?? Array.Empty<EtkMetadataPayload>();
                foreach (var payload in payloads)
                {
                    RewriteImageUrls(url, payload);
                }
                return payloads;
            }
        }

        public static async Task<bool> RefreshImagesAsync(
            string itemPath,
            string itemType,
            int? seasonNumber,
            int? episodeNumber,
            string tmdbId,
            CancellationToken cancellationToken,
            ILibraryManager libraryManager)
        {
            var origin = ResolveEtkOrigin(libraryManager);
            if (string.IsNullOrEmpty(origin))
            {
                return false;
            }
            var url = BuildImageApiUrl(
                origin,
                "refresh",
                itemPath,
                itemType,
                seasonNumber,
                episodeNumber,
                tmdbId);
            using (var response = await ImageRefreshHttpClient.PostAsync(
                url,
                new StringContent(string.Empty),
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }
                Cache.Clear();
                return true;
            }
        }

        public static async Task<EtkRemoteImageCandidate[]> SearchImagesAsync(
            IJsonSerializer serializer,
            string itemPath,
            string itemType,
            int? seasonNumber,
            int? episodeNumber,
            string tmdbId,
            bool includeAllLanguages,
            CancellationToken cancellationToken,
            ILibraryManager libraryManager)
        {
            var origin = ResolveEtkOrigin(libraryManager);
            if (string.IsNullOrEmpty(origin))
            {
                return Array.Empty<EtkRemoteImageCandidate>();
            }
            var url = BuildImageApiUrl(
                origin,
                "search",
                itemPath,
                itemType,
                seasonNumber,
                episodeNumber,
                tmdbId,
                includeAllLanguages);
            using (var response = await ImageRefreshHttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<EtkRemoteImageCandidate>();
                }
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var payload = serializer.DeserializeFromString<EtkRemoteImageSearchResponse>(json);
                var images = payload?.images ?? Array.Empty<EtkRemoteImageCandidate>();
                var originUri = new Uri(origin, UriKind.Absolute);
                foreach (var image in images)
                {
                    image.url = BuildImageProxyUrl(originUri, image.url);
                    image.thumbnail_url = BuildImageProxyUrl(originUri, image.thumbnail_url);
                }
                return images;
            }
        }

        private static string ResolveEtkOrigin(ILibraryManager libraryManager)
        {
            lock (OriginLock)
            {
                if (!string.IsNullOrEmpty(etkOrigin))
                {
                    return etkOrigin;
                }
            }
            if (libraryManager == null)
            {
                return null;
            }
            foreach (var item in libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { "Movie", "Episode" }
            }))
            {
                var mediaInfoUrl = ResolveMediaInfoUrl(item.Path);
                if (RememberEtkOrigin(mediaInfoUrl))
                {
                    return etkOrigin;
                }
            }
            return null;
        }

        private static bool RememberEtkOrigin(string url)
        {
            return ConfigureEtkOrigin(url, null);
        }

        private static void RewriteImageUrls(string mediaInfoUrl, EtkMetadataPayload payload)
        {
            if (!Uri.TryCreate(mediaInfoUrl, UriKind.Absolute, out var mediaInfoUri))
            {
                return;
            }

            if (payload.images != null)
            {
                payload.images.primary = BuildImageProxyUrl(mediaInfoUri, payload.images.primary);
                payload.images.backdrop = BuildImageProxyUrl(mediaInfoUri, payload.images.backdrop);
                payload.images.logo = BuildImageProxyUrl(mediaInfoUri, payload.images.logo);
                payload.images.thumb = BuildImageProxyUrl(mediaInfoUri, payload.images.thumb);
            }

            foreach (var person in payload.people ?? Array.Empty<EtkMetadataPerson>())
            {
                person.image_url = BuildImageProxyUrl(mediaInfoUri, person.image_url);
            }
        }

        private static string BuildImageProxyUrl(Uri mediaInfoUri, string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)
                || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri)
                || (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
            {
                return imageUrl;
            }

            var etkOrigin = mediaInfoUri.GetLeftPart(UriPartial.Authority);
            if (string.Equals(imageUri.GetLeftPart(UriPartial.Authority), etkOrigin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(imageUri.AbsolutePath, "/api/image_proxy", StringComparison.OrdinalIgnoreCase))
            {
                return imageUrl;
            }

            return etkOrigin + "/api/image_proxy?url=" + Uri.EscapeDataString(imageUrl);
        }

        public static string ResolveMediaInfoUrl(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return null;
            }

            var mediaPath = itemPath.Trim();
            if (Directory.Exists(mediaPath))
            {
                try
                {
                    mediaPath = Directory.EnumerateFiles(mediaPath, "*.strm", SearchOption.AllDirectories)
                        .FirstOrDefault();
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                return null;
            }
            var mediaUrl = mediaPath.Trim();
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

        private static string BuildMetadataUrl(
            string mediaInfoUrl,
            string itemType,
            int? seasonNumber,
            int? episodeNumber)
        {
            var values = new List<string>
            {
                "item_type=" + Uri.EscapeDataString(itemType)
            };
            if (seasonNumber.HasValue)
            {
                values.Add("season_number=" + seasonNumber.Value);
            }
            if (episodeNumber.HasValue)
            {
                values.Add("episode_number=" + episodeNumber.Value);
            }
            return mediaInfoUrl.TrimEnd('/') + "/metadata?" + string.Join("&", values);
        }

        private static string BuildPhysicalMetadataUrl(
            string origin,
            string itemPath,
            string itemType,
            int? seasonNumber,
            int? episodeNumber)
        {
            var values = new List<string>
            {
                "path=" + Uri.EscapeDataString(itemPath ?? string.Empty),
                "item_type=" + Uri.EscapeDataString(itemType)
            };
            if (seasonNumber.HasValue)
            {
                values.Add("season_number=" + seasonNumber.Value);
            }
            if (episodeNumber.HasValue)
            {
                values.Add("episode_number=" + episodeNumber.Value);
            }
            return origin.TrimEnd('/') + "/api/emby/metadata?" + string.Join("&", values);
        }

        private static string BuildImageApiUrl(
            string origin,
            string action,
            string itemPath,
            string itemType,
            int? seasonNumber,
            int? episodeNumber,
            string tmdbId,
            bool? includeAllLanguages = null)
        {
            var values = new List<string>
            {
                "item_type=" + Uri.EscapeDataString(itemType ?? string.Empty)
            };
            if (!string.IsNullOrWhiteSpace(itemPath))
            {
                values.Add("path=" + Uri.EscapeDataString(itemPath));
            }
            if (seasonNumber.HasValue)
            {
                values.Add("season_number=" + seasonNumber.Value);
            }
            if (episodeNumber.HasValue)
            {
                values.Add("episode_number=" + episodeNumber.Value);
            }
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                values.Add("tmdb_id=" + Uri.EscapeDataString(tmdbId));
            }
            if (includeAllLanguages.HasValue)
            {
                values.Add("include_all_languages=" + includeAllLanguages.Value.ToString().ToLowerInvariant());
            }
            return origin.TrimEnd('/') + "/api/emby/metadata/images/" + action
                + "?" + string.Join("&", values);
        }

        private static string FirstPathSegment(string value)
        {
            var end = value.IndexOfAny(new[] { '/', '?', '#' });
            return (end < 0 ? value : value.Substring(0, end)).Trim();
        }

        public static int? ResolveSeasonNumber(string itemPath, int? fallback)
        {
            var name = Path.GetFileName((itemPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(name, "Specials", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            var match = Regex.Match(name ?? string.Empty, @"^(?:Season\s*|S|\u7b2c)0*(\d+)(?:\u5b63)?$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var value)
                ? value
                : fallback;
        }
    }

    public abstract class EtkMetadataProviderBase<TItem, TLookup> :
        IRemoteMetadataProvider<TItem, TLookup>, IHasOrder, IForcedProvider
        where TItem : BaseItem, IHasLookupInfo<TLookup>, new()
        where TLookup : ItemLookupInfo, new()
    {
        private readonly IJsonSerializer jsonSerializer;
        private readonly IHttpClient httpClient;
        private readonly ILibraryManager libraryManager;

        protected EtkMetadataProviderBase(
            IJsonSerializer jsonSerializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
        {
            this.jsonSerializer = jsonSerializer;
            this.httpClient = httpClient;
            this.libraryManager = libraryManager;
        }

        public string Name => "ETK Metadata";

        public int Order => -1000;

        protected abstract string ItemType { get; }

        public async Task<MetadataResult<TItem>> GetMetadata(TLookup info, CancellationToken cancellationToken)
        {
            var payload = await EtkMetadataClient.GetAsync(
                this.jsonSerializer,
                info.Path,
                this.ItemType,
                string.Equals(this.ItemType, "Season", StringComparison.Ordinal)
                    ? EtkMetadataClient.ResolveSeasonNumber(info.Path, info.IndexNumber)
                    : info.ParentIndexNumber,
                string.Equals(this.ItemType, "Episode", StringComparison.Ordinal) ? info.IndexNumber : null,
                cancellationToken,
                this.libraryManager).ConfigureAwait(false);
            return BuildResult(info, payload);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            TLookup searchInfo,
            CancellationToken cancellationToken)
        {
            var payload = await EtkMetadataClient.GetAsync(
                this.jsonSerializer,
                searchInfo.Path,
                this.ItemType,
                string.Equals(this.ItemType, "Season", StringComparison.Ordinal)
                    ? EtkMetadataClient.ResolveSeasonNumber(searchInfo.Path, searchInfo.IndexNumber)
                    : searchInfo.ParentIndexNumber,
                string.Equals(this.ItemType, "Episode", StringComparison.Ordinal) ? searchInfo.IndexNumber : null,
                cancellationToken,
                this.libraryManager).ConfigureAwait(false);
            if (payload == null)
            {
                return Array.Empty<RemoteSearchResult>();
            }
            var item = BuildItem(searchInfo, payload);
            return new[]
            {
                new RemoteSearchResult
                {
                    Name = item.Name,
                    OriginalTitle = item.OriginalTitle,
                    Overview = item.Overview,
                    PremiereDate = item.PremiereDate,
                    ProductionYear = item.ProductionYear,
                    IndexNumber = item.IndexNumber,
                    ParentIndexNumber = item.ParentIndexNumber,
                    ImageUrl = payload.images?.primary,
                    SearchProviderName = this.Name,
                    ProviderIds = item.ProviderIds
                }
            };
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return this.httpClient.GetResponse(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = false
            });
        }

        private MetadataResult<TItem> BuildResult(TLookup info, EtkMetadataPayload payload)
        {
            if (payload == null)
            {
                return new MetadataResult<TItem>();
            }
            var item = BuildItem(info, payload);
            var result = new MetadataResult<TItem>
            {
                HasMetadata = true,
                Item = item,
                Provider = this.Name,
                ResultLanguage = "zh-CN",
                SearchImageUrl = payload.images?.primary
            };
            if (payload.actors_ready)
            {
                result.ResetPeople();
                foreach (var person in payload.people ?? Array.Empty<EtkMetadataPerson>())
                {
                    if (string.IsNullOrWhiteSpace(person.name))
                    {
                        continue;
                    }
                    var infoPerson = new PersonInfo
                    {
                        Name = person.name,
                        Role = person.role,
                        Type = string.Equals(person.type, "Director", StringComparison.OrdinalIgnoreCase)
                            ? PersonType.Director
                            : PersonType.Actor,
                        ImageUrl = person.image_url
                    };
                    if (!string.IsNullOrWhiteSpace(person.tmdb_id))
                    {
                        infoPerson.ProviderIds["Tmdb"] = person.tmdb_id;
                    }
                    result.AddPerson(infoPerson);
                }
            }
            return result;
        }

        private TItem BuildItem(TLookup info, EtkMetadataPayload payload)
        {
            var item = new TItem
            {
                Name = string.IsNullOrWhiteSpace(payload.name) ? info.Name : payload.name,
                OriginalTitle = payload.original_title,
                Overview = payload.overview,
                Tagline = payload.tagline,
                OfficialRating = payload.official_rating,
                CommunityRating = payload.community_rating,
                ProductionYear = payload.production_year,
                IndexNumber = string.Equals(this.ItemType, "Episode", StringComparison.Ordinal)
                    ? payload.episode_number ?? info.IndexNumber
                    : string.Equals(this.ItemType, "Season", StringComparison.Ordinal)
                        ? payload.season_number ?? info.IndexNumber
                        : info.IndexNumber,
                ParentIndexNumber = string.Equals(this.ItemType, "Episode", StringComparison.Ordinal)
                    ? payload.season_number ?? info.ParentIndexNumber
                    : info.ParentIndexNumber
            };
            if (DateTimeOffset.TryParse(payload.premiere_date, out var premiereDate))
            {
                item.PremiereDate = premiereDate;
            }
            if (DateTimeOffset.TryParse(payload.end_date, out var endDate))
            {
                item.EndDate = endDate;
            }
            if (payload.runtime_minutes.HasValue && payload.runtime_minutes.Value > 0)
            {
                item.RunTimeTicks = TimeSpan.FromMinutes(payload.runtime_minutes.Value).Ticks;
            }
            item.SetGenres((payload.genres ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)));
            item.SetTags((payload.tags ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)));
            item.SetStudios((payload.studios ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(payload.tmdb_id))
            {
                item.ProviderIds["Tmdb"] = payload.tmdb_id;
            }
            if (!string.IsNullOrWhiteSpace(payload.imdb_id))
            {
                item.ProviderIds["Imdb"] = payload.imdb_id;
            }
            return item;
        }
    }

    public sealed class EtkMovieMetadataProvider :
        EtkMetadataProviderBase<Movie, MovieInfo>, IHasMetadataFeatures
    {
        public EtkMovieMetadataProvider(
            IJsonSerializer serializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
            : base(serializer, httpClient, libraryManager) { }

        protected override string ItemType => "Movie";

        public MetadataFeatures[] Features => new[] { MetadataFeatures.Collections };

    }

    public sealed class EtkSeriesMetadataProvider : EtkMetadataProviderBase<Series, SeriesInfo>
    {
        public EtkSeriesMetadataProvider(
            IJsonSerializer serializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
            : base(serializer, httpClient, libraryManager) { }

        protected override string ItemType => "Series";
    }

    public sealed class EtkSeasonMetadataProvider : EtkMetadataProviderBase<Season, SeasonInfo>
    {
        public EtkSeasonMetadataProvider(
            IJsonSerializer serializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
            : base(serializer, httpClient, libraryManager) { }

        protected override string ItemType => "Season";
    }

    public sealed class EtkEpisodeMetadataProvider : EtkMetadataProviderBase<Episode, EpisodeInfo>
    {
        public EtkEpisodeMetadataProvider(
            IJsonSerializer serializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
            : base(serializer, httpClient, libraryManager) { }

        protected override string ItemType => "Episode";
    }

    public sealed class EtkBoxSetMetadataProvider :
        IRemoteMetadataProvider<BoxSet, ItemLookupInfo>, IHasOrder, IForcedProvider
    {
        private readonly IJsonSerializer jsonSerializer;
        private readonly IHttpClient httpClient;
        private readonly ILibraryManager libraryManager;

        public EtkBoxSetMetadataProvider(
            IJsonSerializer jsonSerializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
        {
            this.jsonSerializer = jsonSerializer;
            this.httpClient = httpClient;
            this.libraryManager = libraryManager;
        }

        public string Name => "ETK Metadata";

        public int Order => -1000;

        public async Task<MetadataResult<BoxSet>> GetMetadata(
            ItemLookupInfo info,
            CancellationToken cancellationToken)
        {
            info.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
            EtkMetadataPayload payload;
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                var candidates = await EtkMetadataClient.SearchCollectionsAsync(
                    this.jsonSerializer,
                    this.libraryManager,
                    info.Name,
                    cancellationToken).ConfigureAwait(false);
                payload = candidates.FirstOrDefault(item => string.Equals(
                    item?.name,
                    info.Name,
                    StringComparison.OrdinalIgnoreCase));
                if (payload != null && !string.IsNullOrWhiteSpace(payload.tmdb_id))
                {
                    payload = await EtkMetadataClient.GetCollectionAsync(
                        this.jsonSerializer,
                        this.libraryManager,
                        payload.tmdb_id,
                        cancellationToken).ConfigureAwait(false) ?? payload;
                }
            }
            else
            {
                payload = await EtkMetadataClient.GetCollectionAsync(
                    this.jsonSerializer,
                    this.libraryManager,
                    tmdbId,
                    cancellationToken).ConfigureAwait(false);
            }
            return BuildResult(info, payload);
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            ItemLookupInfo searchInfo,
            CancellationToken cancellationToken)
        {
            var payloads = await EtkMetadataClient.SearchCollectionsAsync(
                this.jsonSerializer,
                this.libraryManager,
                searchInfo.Name,
                cancellationToken).ConfigureAwait(false);
            return payloads
                .Where(payload => payload != null && !string.IsNullOrWhiteSpace(payload.name))
                .Select(payload =>
                {
                    var item = BuildItem(searchInfo, payload);
                    return new RemoteSearchResult
                    {
                        Name = item.Name,
                        OriginalTitle = item.OriginalTitle,
                        Overview = item.Overview,
                        ImageUrl = payload.images?.primary,
                        SearchProviderName = this.Name,
                        ProviderIds = item.ProviderIds
                    };
                })
                .ToArray();
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return this.httpClient.GetResponse(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = false
            });
        }

        private MetadataResult<BoxSet> BuildResult(ItemLookupInfo info, EtkMetadataPayload payload)
        {
            if (payload == null)
            {
                return new MetadataResult<BoxSet>();
            }
            var item = BuildItem(info, payload);
            return new MetadataResult<BoxSet>
            {
                HasMetadata = true,
                Item = item,
                Provider = this.Name,
                ResultLanguage = "zh-CN",
                SearchImageUrl = payload.images?.primary
            };
        }

        private static BoxSet BuildItem(ItemLookupInfo info, EtkMetadataPayload payload)
        {
            var item = new BoxSet
            {
                Name = string.IsNullOrWhiteSpace(payload.name) ? info.Name : payload.name,
                OriginalTitle = payload.original_title,
                Overview = payload.overview
            };
            if (!string.IsNullOrWhiteSpace(payload.tmdb_id))
            {
                item.ProviderIds["Tmdb"] = payload.tmdb_id;
            }
            return item;
        }
    }

    public sealed class EtkImageProvider : IRemoteImageProvider, IHasOrder, IForcedProvider
    {
        private readonly IJsonSerializer jsonSerializer;
        private readonly IHttpClient httpClient;
        private readonly ILibraryManager libraryManager;

        public EtkImageProvider(
            IJsonSerializer jsonSerializer,
            IHttpClient httpClient,
            ILibraryManager libraryManager)
        {
            this.jsonSerializer = jsonSerializer;
            this.httpClient = httpClient;
            this.libraryManager = libraryManager;
        }

        public string Name => "ETK Images";

        public int Order => -1000;

        public bool Supports(BaseItem item)
        {
            return item is Movie || item is Series || item is Season || item is Episode || item is BoxSet;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            if (item is Season)
            {
                return new[] { ImageType.Primary };
            }
            if (item is Episode)
            {
                return new[] { ImageType.Primary, ImageType.Thumb };
            }
            if (item is BoxSet)
            {
                return new[] { ImageType.Primary, ImageType.Backdrop };
            }
            return new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken)
        {
            if (ManualImageSearchInterceptor.TryGetRequest(
                item.InternalId,
                out var includeAllLanguages))
            {
                return await this.GetLiveImages(
                    item,
                    includeAllLanguages,
                    cancellationToken).ConfigureAwait(false);
            }

            if (item is BoxSet)
            {
                item.ProviderIds.TryGetValue("Tmdb", out var collectionTmdbId);
                var collection = await EtkMetadataClient.GetCollectionAsync(
                    this.jsonSerializer,
                    this.libraryManager,
                    collectionTmdbId,
                    cancellationToken).ConfigureAwait(false);
                if (collection?.images == null)
                {
                    return Array.Empty<RemoteImageInfo>();
                }
                var collectionImages = new List<RemoteImageInfo>();
                Add(collectionImages, collection.images.primary, ImageType.Primary);
                Add(collectionImages, collection.images.backdrop, ImageType.Backdrop);
                return collectionImages;
            }

            var itemType = item is Movie ? "Movie"
                : item is Series ? "Series"
                : item is Season ? "Season"
                : "Episode";
            var payload = await EtkMetadataClient.GetAsync(
                this.jsonSerializer,
                item.Path,
                itemType,
                item is Season
                    ? EtkMetadataClient.ResolveSeasonNumber(item.Path, item.IndexNumber)
                    : item.ParentIndexNumber,
                item is Episode ? item.IndexNumber : null,
                cancellationToken,
                this.libraryManager).ConfigureAwait(false);
            if (payload?.images == null)
            {
                return Array.Empty<RemoteImageInfo>();
            }
            var cached = new List<RemoteImageInfo>();
            Add(cached, payload.images.primary, ImageType.Primary);
            if (!(item is Season))
            {
                if (!(item is Episode))
                {
                    Add(cached, payload.images.backdrop, ImageType.Backdrop);
                    Add(cached, payload.images.logo, ImageType.Logo);
                }
                Add(cached, payload.images.thumb, ImageType.Thumb);
            }
            return cached;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetLiveImages(
            BaseItem item,
            bool includeAllLanguages,
            CancellationToken cancellationToken)
        {
            var itemType = item is Movie ? "Movie"
                : item is Series ? "Series"
                : item is Season ? "Season"
                : item is Episode ? "Episode"
                : "BoxSet";
            item.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
            var candidates = await EtkMetadataClient.SearchImagesAsync(
                this.jsonSerializer,
                item.Path,
                itemType,
                item is Season
                    ? EtkMetadataClient.ResolveSeasonNumber(item.Path, item.IndexNumber)
                    : item.ParentIndexNumber,
                item is Episode ? item.IndexNumber : null,
                tmdbId,
                includeAllLanguages,
                cancellationToken,
                this.libraryManager).ConfigureAwait(false);
            var result = new List<RemoteImageInfo>();
            foreach (var candidate in candidates)
            {
                if (!Enum.TryParse(candidate.type, true, out ImageType imageType)
                    || string.IsNullOrWhiteSpace(candidate.url))
                {
                    continue;
                }
                result.Add(new RemoteImageInfo
                {
                    ProviderName = this.Name,
                    Url = candidate.url,
                    ThumbnailUrl = string.IsNullOrWhiteSpace(candidate.thumbnail_url)
                        ? candidate.url
                        : candidate.thumbnail_url,
                    Type = imageType,
                    Language = candidate.language,
                    DisplayLanguage = candidate.language,
                    Width = candidate.width,
                    Height = candidate.height,
                    CommunityRating = candidate.community_rating,
                    VoteCount = candidate.vote_count
                });
            }
            return result;
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return this.httpClient.GetResponse(new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                BufferContent = false
            });
        }

        private static void Add(List<RemoteImageInfo> images, string url, ImageType type)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                images.Add(new RemoteImageInfo
                {
                    ProviderName = "ETK Images",
                    Url = url,
                    ThumbnailUrl = url,
                    Type = type
                });
            }
        }

    }
}
