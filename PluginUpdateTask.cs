using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace ETKMediaInfoBridge
{
    public sealed class PluginUpdateTask : IScheduledTask, IConfigurableScheduledTask
    {
        private const string LatestDownloadUrl =
            "https://github.com/hbq0405/etk-mediainfo-bridge/releases/latest/download/ETKMediaInfoBridge.dll";

        private static readonly HttpClient HttpClient = CreateHttpClient();
        private readonly IApplicationHost applicationHost;
        private readonly ILogger logger;

        public PluginUpdateTask(IApplicationHost applicationHost, ILogger logger)
        {
            this.applicationHost = applicationHost;
            this.logger = logger;
        }

        public string Name => "更新 ETK MediaInfo Bridge";

        public string Key => "ETKMediaInfoBridgeUpdate";

        public string Description => "从 GitHub Release 下载并安装最新版插件，更新完成后需重启 Emby。";

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
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);
            var currentAssembly = typeof(Plugin).GetTypeInfo().Assembly;
            var currentVersion = currentAssembly.GetName().Version;
            var assemblyPath = currentAssembly.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("无法确定当前插件 DLL 路径。");
            }

            var updatePath = assemblyPath + ".update";
            var backupPath = assemblyPath + ".bak";
            DeleteIfExists(updatePath);

            try
            {
                this.logger.Info(
                    "Checking ETK MediaInfo Bridge update. Current version: {0}",
                    currentVersion);

                using (var response = await HttpClient.GetAsync(
                    LatestDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    progress.Report(5);
                    await DownloadAsync(response, updatePath, cancellationToken, progress).ConfigureAwait(false);
                }

                progress.Report(90);
                var downloadedAssembly = AssemblyName.GetAssemblyName(updatePath);
                if (!string.Equals(
                    downloadedAssembly.Name,
                    currentAssembly.GetName().Name,
                    StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "下载文件不是 ETK MediaInfo Bridge 插件: " + downloadedAssembly.Name);
                }

                var downloadedVersion = downloadedAssembly.Version;
                if (downloadedVersion == null || downloadedVersion <= currentVersion)
                {
                    this.logger.Info(
                        "ETK MediaInfo Bridge is already up to date. Current: {0}, latest: {1}",
                        currentVersion,
                        downloadedVersion);
                    progress.Report(100);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReplaceAssembly(updatePath, assemblyPath, backupPath);
                this.applicationHost.NotifyPendingRestart();
                progress.Report(100);
                this.logger.Info(
                    "ETK MediaInfo Bridge updated from {0} to {1}. Restart Emby to apply the update.",
                    currentVersion,
                    downloadedVersion);
            }
            finally
            {
                DeleteIfExists(updatePath);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ETKMediaInfoBridge-Updater");
            return client;
        }

        private static async Task DownloadAsync(
            HttpResponseMessage response,
            string targetPath,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            var totalBytes = response.Content.Headers.ContentLength;
            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                true))
            {
                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    downloadedBytes += read;
                    if (totalBytes.GetValueOrDefault() > 0)
                    {
                        progress.Report(5 + (downloadedBytes * 80.0 / totalBytes.Value));
                    }
                }
            }
        }

        private static void ReplaceAssembly(string updatePath, string assemblyPath, string backupPath)
        {
            DeleteIfExists(backupPath);
            try
            {
                File.Replace(updatePath, assemblyPath, backupPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(assemblyPath, backupPath, true);
                File.Copy(updatePath, assemblyPath, true);
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
