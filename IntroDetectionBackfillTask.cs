using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace ETKMediaInfoBridge
{
    public sealed class IntroDetectionBackfillTask : IScheduledTask, IConfigurableScheduledTask
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        private readonly IApplicationPaths applicationPaths;
        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public IntroDetectionBackfillTask(
            IApplicationPaths applicationPaths,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            this.applicationPaths = applicationPaths;
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        public string Name => "ETK 自主片头提取兜底";

        public string Key => "ETKIntroDetectionBackfill";

        public string Description => "通知已注册的 ETK 服务扫描在库剧集并补齐缺失片头章节。";

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
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            EtkMetadataClient.LoadEtkOrigin(this.applicationPaths.PluginConfigurationsPath);
            var etkOrigin = EtkMetadataClient.GetEtkOrigin(this.libraryManager);
            if (string.IsNullOrWhiteSpace(etkOrigin))
            {
                throw new InvalidOperationException("无法确定 ETK 服务地址，无法触发自主片头提取兜底。");
            }

            var endpoint = etkOrigin.TrimEnd('/') + "/api/emby/intro-detection/backfill";
            this.logger.Info("Submitting ETK intro detection backfill to {0}.", endpoint);
            progress.Report(10);

            using (var response = await HttpClient.PostAsync(
                endpoint,
                new StringContent("{}", Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        "ETK 自主片头提取兜底提交失败: HTTP "
                        + (int)response.StatusCode
                        + " "
                        + body);
                }
            }

            progress.Report(100);
            this.logger.Info("ETK intro detection backfill submitted successfully.");
        }
    }
}
