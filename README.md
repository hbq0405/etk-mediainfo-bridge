# ETK MediaInfo Bridge

![ETK MediaInfo Bridge](logo.png)

ETK MediaInfo Bridge 是适用于 Emby 4.9.x 的媒体信息桥接插件。它将
[Emby ToolKit](https://github.com/hbq0405/emby-toolkit) 生成的格式化媒体信息直接写入 Emby，
无需 Emby 远程探测，也不生成或读取 `*-mediainfo.json` 旁路文件。

## 功能

- 将 ETK 格式化媒体流和章节精确写入对应的 Emby Item。
- 注入前清空 Emby 旧媒体流；新入库时删除抢占内嵌索引的外挂字幕，最终刷新后再安全保留外挂流。
- Emby 手动刷新或任务刷新后，自动从 ETK 恢复媒体信息；只产生剧集或季父级更新事件时，也会回补其下分集。
- 新 STRM 入库时直接取得 Emby ItemID，媒体信息注入成功后主动通知 ETK 进入后续处理。
- 首次刮削时根据 STRM 中的 pick code/SHA1 从 ETK 数据库取得确定的 TMDb ID，不依赖目录名中的 TMDb 尾巴或 NFO。
- 从 ETK `media_metadata` 恢复电影、剧集、季和分集元数据；演员翻译完成后由 ETK 刷新并替换完整人物表。
- 从 ETK 图片链接恢复海报、背景图、Logo、横版缩略图和季海报。
- 插件启动及神医片头提取任务完成后刷新本地片头快照，同步到 ETK 缓存和共享中心。
- Emby 刷新清空章节时，优先用本地快照恢复片头；本地没有片头时才使用 ETK 缓存中的共享片头。
- 同时支持普通 115 播放 URL 和 ETK 虚拟播放 SHA1 URL。
- 按 Item 合并重复更新事件，并将恢复请求限制为最多 4 路并发。

## 工作方式

1. ETK 将 `p115_mediainfo_cache.mediainfo_json` 作为格式化媒体流的唯一来源，将 `media_metadata` 作为元数据来源。
2. 插件通过 `POST /Items/{Id}/ETKMediaInfo` 将媒体流和章节持久化到指定 Emby Item。
3. Emby Item 更新后，插件根据 STRM 中的 pick code 或虚拟播放 SHA1 请求 ETK 缓存并重新注入。
4. 新 Item 注入成功后，插件调用 ETK `item-ready` 接口上报 ItemID；ETK 校验 pick code/SHA1 后接管入库流程。
5. 插件全库扫描到完整的 `IntroStart` 和 `IntroEnd` 后，通知 ETK 写回 `mediainfo_json.Chapters` 并上传共享中心。
6. Emby 首次扫描或后续刷新时，`ETK Metadata` 和 `ETK Images` Provider 从 ETK 数据库恢复元数据和图片。
7. 恢复媒体信息时优先保留 Emby 已有片头或本地快照；两者都没有时，才注入 ETK 从共享中心合并的片头。

## 安装

1. 从[最新版本](https://github.com/hbq0405/etk-mediainfo-bridge/releases/latest)下载 `ETKMediaInfoBridge.dll`。
2. 将 DLL 放入 Emby 的插件目录。
3. 重启 Emby。
4. 在媒体库元数据和图片抓取器中确认 `ETK Metadata`、`ETK Images` 已启用并排在首位。

Emby 必须能够访问 STRM 文件中保存的 ETK 地址，ETK 需正确配置 Emby 管理员 API 信息才能校验片头回写。对应后端接口已包含在 ETK `dev` 分支中。

## API

认证接口接收 `p115_mediainfo_cache.mediainfo_json` 规范化后的对象：

```http
POST /Items/{Id}/ETKMediaInfo
Content-Type: application/json
X-Emby-Token: <admin-api-key>

{
  "MediaSourceInfo": { "MediaStreams": [] },
  "Chapters": [],
  "DropConflictingExternalStreams": true
}
```

每次请求只更新路径中的 Emby Item ID，先清空旧媒体流，再写入格式化媒体流和不冲突的外挂流。设置 `DropConflictingExternalStreams` 时会删除抢占内嵌索引的外挂流，后续 Emby 刷新会重新识别；刷新回补阶段则会把冲突外挂流移动到未占用索引。重复提交相同数据不会产生额外副作用。

## 构建

安装 .NET 8 SDK 后执行：

```bash
dotnet build -c Release
```

构建产物位于 `bin/Release/netstandard2.0/ETKMediaInfoBridge.dll`。
