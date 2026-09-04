# 复制档案 Clipboard Atlas

一个本机 Windows 剪贴板历史看板，使用 Electron 和 React 构建。

## 功能

- 自动记录最近 48 小时内复制过的内容，过期记录会自动清理。
- 支持文本、剪贴板图片，以及从资源管理器复制的视频/图片文件。
- 左侧按具体时间点显示复制时间轴，右侧预览选中的内容。
- 可停靠在屏幕左侧或右侧，收起后露出边缘把手，鼠标移到边缘会展开。
- 支持暂停记录、隐藏内容、固定展开、清空历史和复制回剪贴板。

## 开发运行

```bash
npm install
npm run dev
```

## 构建

```bash
npm run build
```

## 打包 Windows 便携版

```bash
npm run package
```
