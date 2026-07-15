# ETK MediaInfo Bridge

![ETK MediaInfo Bridge](logo.png)

ETK MediaInfo Bridge 是适用于 Emby 4.9.x 的媒体信息桥接插件。它将
[Emby ToolKit](https://github.com/hbq0405/emby-toolkit) 生成的格式化媒体信息直接写入 Emby，
无需 Emby 远程探测，也不生成或读取 `*-mediainfo.json` 旁路文件。

## 功能

- 将 ETK 格式化媒体流和章节精确写入对应的 Emby Item。
- 覆盖内嵌媒体流时保留 Emby 已识别的外挂字幕流。
- Emby 手动刷新或任务刷新后，自动从 ETK 恢复媒体信息；只产生剧集或季父级更新事件时，也会回补其下分集。
- Emby 检测到完整片头章节后，在恢复前通知 ETK 写回 `mediainfo_json.Chapters` 并上传共享中心。
- 同时支持普通 115 播放 URL 和 ETK 虚拟播放 SHA1 URL。
- 按 Item 合并重复更新事件，并将恢复请求限制为最多 4 路并发。

## 工作方式

1. ETK 将 `p115_mediainfo_cache.mediainfo_json` 作为格式化媒体信息的唯一来源。
2. 插件通过 `POST /Items/{Id}/ETKMediaInfo` 将媒体流和章节持久化到指定 Emby Item。
3. Emby Item 更新后，插件根据 STRM 中的 pick code 或虚拟播放 SHA1 请求 ETK 缓存并重新注入。
4. 恢复请求会携带 Emby ItemId。ETK 使用自身 Emby 凭据读取章节，并校验 Item 与播放身份一致。
5. 检测到完整的 `IntroStart` 和 `IntroEnd` 后，ETK 只更新 `mediainfo_json.Chapters`；共享片头仍保存于中心独立索引，不写入 `raw_ffprobe_json`。

## 安装

1. 从[最新版本](https://github.com/hbq0405/etk-mediainfo-bridge/releases/latest)下载 `ETKMediaInfoBridge.dll`。
2. 将 DLL 放入 Emby 的插件目录。
3. 重启 Emby。

Emby 必须能够访问 STRM 文件中保存的 ETK 地址，并且 ETK 需要正确配置当前 Emby 服务器地址、管理员 API Key 和用户 ID。对应后端接口已包含在 ETK `dev` 分支中。

## API

认证接口接收 `p115_mediainfo_cache.mediainfo_json` 规范化后的对象：

```http
POST /Items/{Id}/ETKMediaInfo
Content-Type: application/json
X-Emby-Token: <admin-api-key>

{
  "MediaSourceInfo": { "MediaStreams": [] },
  "Chapters": []
}
```

每次请求只更新路径中的 Emby Item ID，替换其内嵌媒体流和章节，同时保留外挂流。重复提交相同数据不会产生额外副作用。

## 构建

安装 .NET 8 SDK 后执行：

```bash
dotnet build -c Release
```

构建产物位于 `bin/Release/netstandard2.0/ETKMediaInfoBridge.dll`。
