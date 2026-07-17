using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace ETKMediaInfoBridge
{
    public sealed class EmbyEventRelay : IServerEntryPoint, IDisposable
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private readonly ILibraryManager libraryManager;
        private readonly ISessionManager sessionManager;
        private readonly IUserDataManager userDataManager;
        private readonly IUserManager userManager;
        private readonly ICollectionManager collectionManager;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<string, bool> pauseStates =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string webhookUrl;
        private bool disposed;

        public EmbyEventRelay(
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            IUserManager userManager,
            ICollectionManager collectionManager,
            IJsonSerializer jsonSerializer,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.sessionManager = sessionManager;
            this.userDataManager = userDataManager;
            this.userManager = userManager;
            this.collectionManager = collectionManager;
            this.jsonSerializer = jsonSerializer;
            this.logger = logger;
        }

        public void Run()
        {
            Plugin.EnsureDependenciesLoaded();
            ManualImageEditInterceptor.Install(this.logger);
            this.DiscoverWebhookUrl();
            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            this.sessionManager.PlaybackStart += this.OnPlaybackStart;
            this.sessionManager.PlaybackProgress += this.OnPlaybackProgress;
            this.sessionManager.PlaybackStopped += this.OnPlaybackStopped;
            this.userDataManager.UserDataSaved += this.OnUserDataSaved;
            this.userManager.UserPolicyUpdated += this.OnUserPolicyUpdated;
            this.collectionManager.ItemsRemovedFromCollection += this.OnItemsRemovedFromCollection;
            this.logger.Info(
                "ETK Emby event relay is active. Endpoint discovered: {0}.",
                !string.IsNullOrEmpty(this.webhookUrl));
        }

        private void DiscoverWebhookUrl()
        {
            foreach (var item in this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { "Movie", "Episode" }
            }))
            {
                if (this.TrySetWebhookUrl(item))
                {
                    return;
                }
            }
        }

        private bool TrySetWebhookUrl(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }
            var mediaInfoUrl = EtkMetadataClient.ResolveMediaInfoUrl(item.Path);
            if (string.IsNullOrEmpty(mediaInfoUrl)
                || !Uri.TryCreate(mediaInfoUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }
            this.webhookUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/api/emby/events";
            return true;
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs eventArgs)
        {
            this.TrySetWebhookUrl(eventArgs?.Item);
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs eventArgs)
        {
            var item = eventArgs?.Item;
            if (item == null)
            {
                return;
            }
            if (MediaInfoRefreshGuard.IsSuppressed(item.InternalId))
            {
                return;
            }
            this.TrySetWebhookUrl(item);
            var reason = eventArgs.UpdateReason;
            if ((reason & ItemUpdateType.ImageUpdate) != 0
                && ManualImageEditInterceptor.Consume(item.InternalId))
            {
                this.QueueEvent("image.update", item);
                return;
            }
            if ((reason & ItemUpdateType.MetadataEdit) != 0)
            {
                this.QueueEvent("metadata.update", item);
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs eventArgs)
        {
            this.pauseStates[PlaybackKey(eventArgs)] = false;
            this.QueuePlaybackEvent("playback.start", eventArgs, false);
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs eventArgs)
        {
            var key = PlaybackKey(eventArgs);
            var wasPaused = this.pauseStates.TryGetValue(key, out var previous) && previous;
            this.pauseStates[key] = eventArgs.IsPaused;
            if (eventArgs.IsPaused && !wasPaused)
            {
                this.QueuePlaybackEvent("playback.pause", eventArgs, false);
            }
            else if (!eventArgs.IsPaused && wasPaused)
            {
                this.QueuePlaybackEvent("playback.start", eventArgs, false);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs eventArgs)
        {
            this.pauseStates.TryRemove(PlaybackKey(eventArgs), out _);
            this.QueuePlaybackEvent("playback.stop", eventArgs, eventArgs.PlayedToCompletion);
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs eventArgs)
        {
            if (eventArgs?.Item == null || eventArgs.User == null || eventArgs.UserData == null)
            {
                return;
            }
            if (eventArgs.SaveReason == UserDataSaveReason.PlaybackStart
                || eventArgs.SaveReason == UserDataSaveReason.PlaybackProgress
                || eventArgs.SaveReason == UserDataSaveReason.PlaybackFinished
                || eventArgs.SaveReason == UserDataSaveReason.Import)
            {
                return;
            }
            this.QueueEvent(
                "item.rate",
                eventArgs.Item,
                UserPayload(eventArgs.User),
                new Dictionary<string, object>
                {
                    ["UserData"] = UserDataPayload(eventArgs.UserData)
                });
        }

        private void OnUserPolicyUpdated(object sender, GenericEventArgs<User> eventArgs)
        {
            var user = eventArgs?.Argument;
            if (user == null)
            {
                return;
            }
            this.QueueEvent("user.policyupdated", null, UserPayload(user));
        }

        private void OnItemsRemovedFromCollection(object sender, CollectionModifiedEventArgs eventArgs)
        {
            if (eventArgs?.Collection != null)
            {
                this.QueueEvent("collection.items.removed", eventArgs.Collection);
            }
        }

        private void QueuePlaybackEvent(
            string eventName,
            PlaybackProgressEventArgs eventArgs,
            bool playedToCompletion)
        {
            if (eventArgs?.Item == null)
            {
                return;
            }
            Dictionary<string, object> user = null;
            var firstUser = eventArgs.Users?.FirstOrDefault();
            if (firstUser != null)
            {
                user = UserPayload(firstUser);
            }
            else if (eventArgs.Session != null && !string.IsNullOrEmpty(eventArgs.Session.UserId))
            {
                user = new Dictionary<string, object>
                {
                    ["Id"] = eventArgs.Session.UserId,
                    ["Name"] = eventArgs.Session.UserName
                };
            }
            if (user == null)
            {
                return;
            }

            this.QueueEvent(
                eventName,
                eventArgs.Item,
                user,
                new Dictionary<string, object>
                {
                    ["PlaybackInfo"] = new Dictionary<string, object>
                    {
                        ["PositionTicks"] = eventArgs.PlaybackPositionTicks,
                        ["RunTimeTicks"] = eventArgs.Item.RunTimeTicks,
                        ["PlayedToCompletion"] = playedToCompletion,
                        ["PlaySessionId"] = eventArgs.PlaySessionId,
                        ["MediaSourceId"] = eventArgs.MediaSourceId
                    },
                    ["Session"] = new Dictionary<string, object>
                    {
                        ["Id"] = eventArgs.Session?.Id,
                        ["DeviceId"] = eventArgs.DeviceId ?? eventArgs.Session?.DeviceId,
                        ["DeviceName"] = eventArgs.DeviceName ?? eventArgs.Session?.DeviceName,
                        ["Client"] = eventArgs.ClientName ?? eventArgs.Session?.Client
                    }
                });
        }

        private void QueueEvent(
            string eventName,
            BaseItem item,
            Dictionary<string, object> user = null,
            Dictionary<string, object> extra = null)
        {
            if (item != null)
            {
                this.TrySetWebhookUrl(item);
            }
            var target = this.webhookUrl;
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            var payload = extra ?? new Dictionary<string, object>();
            payload["Event"] = eventName;
            payload["_etk_source"] = "ETKMediaInfoBridge";
            if (item != null)
            {
                payload["Item"] = ItemPayload(item);
            }
            if (user != null)
            {
                payload["User"] = user;
            }
            _ = Task.Run(() => this.PostAsync(target, eventName, payload));
        }

        private async Task PostAsync(
            string target,
            string eventName,
            Dictionary<string, object> payload)
        {
            try
            {
                using (var content = new StringContent(
                    this.jsonSerializer.SerializeToString(payload),
                    Encoding.UTF8,
                    "application/json"))
                using (var response = await HttpClient.PostAsync(target, content).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        this.logger.Warn(
                            "ETK Emby event relay {0} returned HTTP {1}.",
                            eventName,
                            (int)response.StatusCode);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Debug("ETK Emby event relay {0} failed: {1}", eventName, ex.Message);
            }
        }

        private static Dictionary<string, object> ItemPayload(BaseItem item)
        {
            var payload = new Dictionary<string, object>
            {
                ["Id"] = item.InternalId.ToString(),
                ["Type"] = item.GetType().Name,
                ["Name"] = item.Name,
                ["Path"] = item.Path,
                ["RunTimeTicks"] = item.RunTimeTicks
            };
            AddReflectedValue(payload, item, "SeriesId");
            AddReflectedValue(payload, item, "SeriesName");
            return payload;
        }

        private static Dictionary<string, object> UserPayload(User user)
        {
            return new Dictionary<string, object>
            {
                ["Id"] = user.Id.ToString("N"),
                ["Name"] = user.Name
            };
        }

        private static Dictionary<string, object> UserDataPayload(UserItemData data)
        {
            return new Dictionary<string, object>
            {
                ["IsFavorite"] = data.IsFavorite,
                ["Played"] = data.Played,
                ["PlaybackPositionTicks"] = data.PlaybackPositionTicks,
                ["PlayCount"] = data.PlayCount
            };
        }

        private static void AddReflectedValue(
            Dictionary<string, object> target,
            BaseItem item,
            string propertyName)
        {
            var property = item.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            var value = property?.GetValue(item);
            if (value != null)
            {
                target[propertyName] = value.ToString();
            }
        }

        private static string PlaybackKey(PlaybackProgressEventArgs eventArgs)
        {
            return eventArgs?.PlaySessionId
                ?? eventArgs?.Session?.Id
                ?? eventArgs?.Item?.InternalId.ToString()
                ?? string.Empty;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }
            this.disposed = true;
            ManualImageEditInterceptor.Uninstall();
            this.libraryManager.ItemAdded -= this.OnItemAdded;
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
            this.sessionManager.PlaybackStart -= this.OnPlaybackStart;
            this.sessionManager.PlaybackProgress -= this.OnPlaybackProgress;
            this.sessionManager.PlaybackStopped -= this.OnPlaybackStopped;
            this.userDataManager.UserDataSaved -= this.OnUserDataSaved;
            this.userManager.UserPolicyUpdated -= this.OnUserPolicyUpdated;
            this.collectionManager.ItemsRemovedFromCollection -= this.OnItemsRemovedFromCollection;
            this.pauseStates.Clear();
        }
    }
}
