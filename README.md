# Super Http File Server (SHFS)

一个基于 WPF 的轻量级 HTTP 文件服务器，支持文件浏览、上传、下载、预览、用户认证和 SSL。

## 功能特性

- **文件管理** — 浏览、上传、下载、删除、重命名、移动
- **在线预览** — 图片/文本/PDF/视频/音频/Office 文档
- **用户认证** — 多用户、分组权限、会话超时、防暴力破解
- **SSL 支持** — HTTPS 反向代理，PEM/PFX 证书
- **ZIP 打包** — 目录一键打包下载
- **单文件部署** — Costura.Fody 嵌入所有依赖，一个 exe 即可运行

## 支持的预览格式

| 类别 | 格式 |
|------|------|
| 图片 | png jpg jpeg gif svg bmp webp ico tiff avif |
| 视频 | mp4 webm avi mkv mov wmv |
| 音频 | mp3 wav ogg flac m4a aac |
| 文档 | pdf |
| 文本 | txt log md csv ini json xml yaml yml |
| 代码 | css js html htm c cpp h cs py java go rs ts sh bat ps1 |
| Office | doc docx xls xlsx ppt pptx |

## 快速开始

1. 下载 [最新版本](https://github.com/YunCent/SuperHttpFileServer/releases)
2. 运行 `SHFS.exe`
3. 设置共享目录和监听端口
4. 点击启动，浏览器访问显示的地址

## 系统要求

- Windows 7 SP1 及以上
- .NET Framework 4.8

## 构建

```bash
dotnet restore SHFS.csproj
dotnet build SHFS.csproj -c Release
```

输出：`bin\Release\net48\SHFS.exe`

## 版本

当前版本：**1.0.0**

## 许可证

MIT License
