using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace ETKMediaInfoBridge
{
    public sealed class MetadataBackfillTask : IScheduledTask, IConfigurableScheduledTask
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public MetadataBackfillTask(ILibraryManager libraryManager, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        public string Name => "补齐 ETK 媒体元数据";

        public string Key => "ETKMediaMetadataBackfill";

        public string Description => "扫描在库 STRM，并通知 ETK 补齐旧媒体缺失的当前版本元数据字段。";

        public string Category => "ETK MediaInfo Bridge";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerWeekly,
                    DayOfWeek = System.DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromMinutes(210).Ticks
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            var items = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { "Movie", "Episode", "Video" }
            });
            var endpoints = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var configuredOrigin = EtkMetadataClient.GetEtkOrigin(this.libraryManager);
            if (!string.IsNullOrEmpty(configuredOrigin))
            {
                endpoints[configuredOrigin] = new List<string>
                {
                    configuredOrigin.TrimEnd('/') + "/api/emby/metadata/backfill"
                };
            }
            var total = items.Length;
            for (var index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mediaInfoUrl = EtkMetadataClient.ResolveMediaInfoUrl(items[index].Path);
                var serviceKey = ResolveServiceKey(mediaInfoUrl);
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    if (!endpoints.TryGetValue(serviceKey, out var candidates))
                    {
                        candidates = new List<string>();
                        endpoints[serviceKey] = candidates;
                    }
                    if (candidates.Count < 10)
                    {
                        candidates.Add(mediaInfoUrl.TrimEnd('/') + "/metadata/backfill");
                    }
                }
                if (index % 100 == 0 && total > 0)
                {
                    progress.Report(index * 70.0 / total);
                }
            }

            if (endpoints.Count == 0)
            {
                throw new InvalidOperationException("在库媒体中未找到 ETK STRM，无法触发元数据补齐。");
            }

            var completed = 0;
            foreach (var candidates in endpoints.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var submitted = false;
                foreach (var endpoint in candidates)
                {
                    using (var response = await HttpClient.PostAsync(
                        endpoint,
                        new StringContent("{}", Encoding.UTF8, "application/json"),
                        cancellationToken).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            submitted = true;
                            break;
                        }
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            continue;
                        }
                        var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new HttpRequestException(
                            "ETK 元数据补齐任务提交失败: HTTP "
                            + (int)response.StatusCode
                            + " "
                            + error);
                    }
                }
                if (!submitted)
                {
                    throw new HttpRequestException("ETK 元数据补齐任务提交失败：候选 STRM 均无有效缓存身份。");
                }
                completed++;
                progress.Report(70 + completed * 30.0 / endpoints.Count);
            }

            this.logger.Info(
                "ETK metadata backfill task submitted to {0} ETK server(s).",
                completed);
        }

        internal static string ResolveServiceKey(string mediaInfoUrl)
        {
            if (string.IsNullOrWhiteSpace(mediaInfoUrl))
            {
                return null;
            }
            const string marker = "/api/p115/mediainfo/";
            var markerIndex = mediaInfoUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return markerIndex < 0
                ? null
                : mediaInfoUrl.Substring(0, markerIndex).TrimEnd('/');
        }
    }
}
