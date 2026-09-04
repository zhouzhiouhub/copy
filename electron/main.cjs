const {
  app,
  BrowserWindow,
  ClipboardItem,
  clipboard,
  ipcMain,
  nativeImage,
  net,
  protocol,
  screen,
  shell
} = require('electron')
const { spawn } = require('node:child_process')
const fs = require('node:fs')
const path = require('node:path')
const { pathToFileURL } = require('node:url')

const MAX_AGE = 48 * 60 * 60 * 1000
const PANEL_WIDTH_RATIO = 1 / 6
const PANEL_HEIGHT_RATIO = 1 / 3
const MIN_PANEL_WIDTH = 320
const MIN_PANEL_HEIGHT = 300
const EDGE_PEEK = 6
const EDGE_HIT = 12
const WATCH_INTERVAL = 450
const EDGE_WATCH_INTERVAL = 50
const COLLAPSE_DELAY = 160
const EXPAND_SUPPRESS_AFTER_COPY = 1400
const PASTE_SETTLE_MS = 160

const IMAGE_EXTENSIONS = new Set([
  '.apng',
  '.avif',
  '.bmp',
  '.gif',
  '.heic',
  '.heif',
  '.jpeg',
  '.jpg',
  '.png',
  '.svg',
  '.tif',
  '.tiff',
  '.webp'
])

const VIDEO_EXTENSIONS = new Set([
  '.3g2',
  '.3gp',
  '.avi',
  '.flv',
  '.m4v',
  '.mkv',
  '.mov',
  '.mp4',
  '.mpeg',
  '.mpg',
  '.mts',
  '.ogv',
  '.ts',
  '.webm',
  '.wmv'
])

let mainWindow
let entries = []
let paused = false
let dock = { side: 'right', pinned: false, expanded: true, verticalRatio: 0.5 }
let lastPollingSignature = ''
let suppressCaptureUntil = 0
let suppressExpandUntil = 0
let edgeArmed = true
let pointerSeenInWindow = false
let clipboardWatcher = null
let pollTimer = null
let edgeWatchTimer = null
let collapseTimer = null
let applyingDockBounds = false
let isQuitting = false
let pasteTarget = null
let pendingPastePoint = null
let pendingPasteSince = 0

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'atlas-media',
    privileges: {
      standard: true,
      secure: true,
      stream: true,
      supportFetchAPI: true
    }
  }
])

const dataPath = () => path.join(app.getPath('userData'), 'clipboard-history.json')

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value))
}

function normalizeVerticalRatio(value) {
  const ratio = Number(value)
  return Number.isFinite(ratio) ? clamp(ratio, 0, 1) : 0.5
}

function panelSizeForWorkArea(workArea) {
  const maxWidth = Math.max(EDGE_PEEK, workArea.width - EDGE_PEEK)
  const maxHeight = Math.max(1, workArea.height)
  const minWidth = Math.min(MIN_PANEL_WIDTH, maxWidth)
  const minHeight = Math.min(MIN_PANEL_HEIGHT, maxHeight)

  return {
    width: Math.round(clamp(workArea.width * PANEL_WIDTH_RATIO, minWidth, maxWidth)),
    height: Math.round(clamp(workArea.height * PANEL_HEIGHT_RATIO, minHeight, maxHeight))
  }
}

function dockYForWorkArea(workArea, height) {
  const availableY = Math.max(0, workArea.height - height)
  return Math.round(workArea.y + availableY * normalizeVerticalRatio(dock.verticalRatio))
}

function mediaUrlFor(filePath) {
  return `atlas-media://local/${Buffer.from(filePath, 'utf8').toString('base64url')}`
}

function resolveMediaUrl(url) {
  try {
    const encoded = new URL(url).pathname.replace(/^\//, '')
    return Buffer.from(encoded, 'base64url').toString('utf8')
  } catch {
    return ''
  }
}

function readStore() {
  try {
    const store = JSON.parse(fs.readFileSync(dataPath(), 'utf8'))
    entries = Array.isArray(store.entries) ? store.entries : []
    paused = Boolean(store.paused)
    dock = {
      side: store.dock?.side === 'left' ? 'left' : 'right',
      pinned: Boolean(store.dock?.pinned),
      expanded: Boolean(store.dock?.pinned),
      verticalRatio: normalizeVerticalRatio(store.dock?.verticalRatio)
    }
  } catch {
    entries = []
  }
  pruneEntries()
  coalesceDuplicates()
}

function writeStore() {
  fs.mkdirSync(path.dirname(dataPath()), { recursive: true })
  fs.writeFileSync(dataPath(), JSON.stringify({ entries, paused, dock }, null, 2), 'utf8')
}

function pruneEntries() {
  const threshold = Date.now() - MAX_AGE
  entries = entries.filter((entry) => entry.locked || entry.createdAt > threshold)
}

function coalesceDuplicates() {
  const seen = new Map()
  const next = []

  for (const entry of entries) {
    const signature = signatureFor(entry)
    const existingIndex = seen.get(signature)

    if (existingIndex == null) {
      seen.set(signature, next.length)
      next.push(entry)
      continue
    }

    const existing = next[existingIndex]
    const newer = entry.createdAt >= existing.createdAt ? entry : existing
    const older = newer === entry ? existing : entry
    next[existingIndex] = {
      ...older,
      ...newer,
      id: older.id,
      locked: Boolean(existing.locked || entry.locked),
      createdAt: Math.max(existing.createdAt || 0, entry.createdAt || 0)
    }
  }

  entries = next.sort((left, right) => (right.createdAt || 0) - (left.createdAt || 0))
}

function notifyHistoryChanged() {
  if (!mainWindow || mainWindow.isDestroyed()) return
  mainWindow.webContents.send('history:updated', entries)
}

function notifyDockChanged() {
  if (!mainWindow || mainWindow.isDestroyed()) return
  mainWindow.webContents.send('dock:updated', dock)
}

function getDisplayWorkArea() {
  const cursorDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint())
  if (!mainWindow || mainWindow.isDestroyed() || !dock.expanded) return cursorDisplay.workArea
  return screen.getDisplayMatching(mainWindow.getBounds()).workArea
}

function dockBounds(expanded = dock.expanded) {
  const workArea = getDisplayWorkArea()
  const { width, height } = panelSizeForWorkArea(workArea)
  const y = dockYForWorkArea(workArea, height)
  const shownX = dock.side === 'left' ? workArea.x : workArea.x + workArea.width - width
  const hiddenX = dock.side === 'left' ? workArea.x - width + EDGE_PEEK : workArea.x + workArea.width - EDGE_PEEK

  return {
    x: expanded ? shownX : hiddenX,
    y,
    width,
    height
  }
}

function applyDockBounds(animated = true) {
  if (!mainWindow || mainWindow.isDestroyed()) return
  applyingDockBounds = true
  mainWindow.setBounds(dockBounds(), animated)
  mainWindow.setAlwaysOnTop(true, 'floating')
  try {
    mainWindow.setIgnoreMouseEvents(!dock.expanded, { forward: true })
  } catch {
    mainWindow.setIgnoreMouseEvents(!dock.expanded)
  }
  setTimeout(() => {
    applyingDockBounds = false
  }, 250)
}

function hideAfterCopy() {
  suppressExpandUntil = Date.now() + EXPAND_SUPPRESS_AFTER_COPY
  edgeArmed = false
  if (collapseTimer) {
    clearTimeout(collapseTimer)
    collapseTimer = null
  }
  setDockExpanded(false, true)
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function rememberPasteTarget(point) {
  const display = screen.getDisplayNearestPoint(point)
  if (cursorEdgeSide(point, display)) return
  if (dock.expanded && isPointInWindow(point)) return

  const same =
    pendingPastePoint &&
    Math.abs(pendingPastePoint.x - point.x) < 28 &&
    Math.abs(pendingPastePoint.y - point.y) < 28

  if (!same) {
    pendingPastePoint = { x: point.x, y: point.y }
    pendingPasteSince = Date.now()
    return
  }

  if (Date.now() - pendingPasteSince >= 250) {
    pasteTarget = { x: Math.round(point.x), y: Math.round(point.y) }
  }
}

function pasteAtSavedPoint() {
  if (process.platform !== 'win32' || !pasteTarget) return Promise.resolve(false)

  const physical = screen.dipToScreenPoint(pasteTarget)
  const x = Math.round(physical.x)
  const y = Math.round(physical.y)

  const script = `
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class AtlasPaste {
  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT Point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static void Run(int x, int y) {
    POINT p; p.X = x; p.Y = y;
    IntPtr hit = WindowFromPoint(p);
    IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2);
    if (root == IntPtr.Zero) root = hit;
    if (root != IntPtr.Zero) {
      IntPtr fg = GetForegroundWindow();
      uint dummy;
      uint cur = GetCurrentThreadId();
      uint fgT = GetWindowThreadProcessId(fg, out dummy);
      uint tgT = GetWindowThreadProcessId(root, out dummy);
      AttachThreadInput(cur, fgT, true);
      AttachThreadInput(cur, tgT, true);
      ShowWindow(root, 9);
      BringWindowToTop(root);
      SetForegroundWindow(root);
      AttachThreadInput(cur, fgT, false);
      AttachThreadInput(cur, tgT, false);
    }
    SetCursorPos(x, y);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(50);
    keybd_event(0x11, 0, 0, UIntPtr.Zero);
    keybd_event(0x56, 0, 0, UIntPtr.Zero);
    keybd_event(0x56, 0, 2, UIntPtr.Zero);
    keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
"@
[AtlasPaste]::Run(${x}, ${y})
`

  return new Promise((resolve) => {
    const child = spawnPowerShell(script)
    const timer = setTimeout(() => {
      try {
        child.kill()
      } catch {
        // Ignore kill races if the helper already exited.
      }
      resolve(false)
    }, 4000)

    child.on('error', () => {
      clearTimeout(timer)
      resolve(false)
    })
    child.on('close', (code) => {
      clearTimeout(timer)
      resolve(code === 0)
    })
  })
}

async function hideAndPaste() {
  hideAfterCopy()
  await delay(PASTE_SETTLE_MS)
  if (mainWindow && !mainWindow.isDestroyed()) mainWindow.blur()
  await pasteAtSavedPoint()
}

function cursorEdgeSide(point, display) {
  const { height } = panelSizeForWorkArea(display.workArea)
  const triggerY = dockYForWorkArea(display.workArea, height)
  if (point.y < triggerY || point.y > triggerY + height) return null

  const { x, width } = display.bounds
  if (point.x <= x + EDGE_HIT) return 'left'
  if (point.x >= x + width - EDGE_HIT) return 'right'
  return null
}

function isPointInWindow(point) {
  if (!mainWindow || mainWindow.isDestroyed()) return false
  const bounds = mainWindow.getBounds()
  return (
    point.x >= bounds.x &&
    point.x <= bounds.x + bounds.width &&
    point.y >= bounds.y &&
    point.y <= bounds.y + bounds.height
  )
}

function tickEdgeWatch() {
  if (!mainWindow || mainWindow.isDestroyed()) return

  const point = screen.getCursorScreenPoint()
  rememberPasteTarget(point)
  const display = screen.getDisplayNearestPoint(point)
  const edgeSide = cursorEdgeSide(point, display)

  if (!edgeSide) edgeArmed = true

  if (!dock.expanded) {
    if (Date.now() < suppressExpandUntil || !edgeArmed || !edgeSide) return
    if (dock.side !== edgeSide) {
      setDockSide(edgeSide)
      return
    }
    setDockExpanded(true)
    return
  }

  if (dock.pinned) return
  if (isPointInWindow(point)) pointerSeenInWindow = true
  if (!pointerSeenInWindow) return

  const stayOpen = isPointInWindow(point) || edgeSide === dock.side
  if (stayOpen) {
    if (collapseTimer) {
      clearTimeout(collapseTimer)
      collapseTimer = null
    }
    return
  }

  if (!collapseTimer) {
    collapseTimer = setTimeout(() => {
      collapseTimer = null
      if (!dock.pinned) setDockExpanded(false)
    }, COLLAPSE_DELAY)
  }
}

function startEdgeWatcher() {
  if (edgeWatchTimer) return
  edgeWatchTimer = setInterval(tickEdgeWatch, EDGE_WATCH_INTERVAL)
}

function saveWindowDockPosition() {
  if (applyingDockBounds || !mainWindow || mainWindow.isDestroyed()) return

  const bounds = mainWindow.getBounds()
  const display = screen.getDisplayMatching(bounds)
  const workArea = display.workArea
  const availableY = Math.max(1, workArea.height - bounds.height)
  const verticalRatio = normalizeVerticalRatio((bounds.y - workArea.y) / availableY)
  const leftDistance = Math.abs(bounds.x - workArea.x)
  const rightDistance = Math.abs(workArea.x + workArea.width - (bounds.x + bounds.width))
  const side = leftDistance <= rightDistance ? 'left' : 'right'

  if (side === dock.side && Math.abs(verticalRatio - normalizeVerticalRatio(dock.verticalRatio)) < 0.01) {
    applyDockBounds(false)
    return
  }

  dock = {
    ...dock,
    side,
    verticalRatio
  }
  writeStore()
  notifyDockChanged()
  applyDockBounds(false)
}

function setDockExpanded(expanded, force = false) {
  if (dock.pinned && !expanded && !force) return dock
  if (expanded) pointerSeenInWindow = false
  dock = { ...dock, expanded }
  applyDockBounds()
  writeStore()
  notifyDockChanged()
  return dock
}

function setDockSide(side) {
  dock = { ...dock, side: side === 'left' ? 'left' : 'right', expanded: true }
  applyDockBounds()
  writeStore()
  notifyDockChanged()
  return dock
}

function setDockPinned(pinned) {
  dock = { ...dock, pinned: Boolean(pinned), expanded: Boolean(pinned) || dock.expanded }
  applyDockBounds()
  writeStore()
  notifyDockChanged()
  return dock
}

function isVideoPath(filePath) {
  return VIDEO_EXTENSIONS.has(path.extname(filePath).toLowerCase())
}

function isImagePath(filePath) {
  return IMAGE_EXTENSIONS.has(path.extname(filePath).toLowerCase())
}

function fileKind(filePath) {
  if (isVideoPath(filePath)) return 'video'
  if (isImagePath(filePath)) return 'image'
  return fs.existsSync(filePath) && fs.statSync(filePath).isDirectory() ? 'folder' : 'file'
}

function normalizeFilePath(filePath) {
  if (!filePath || typeof filePath !== 'string') return ''
  const trimmed = filePath.trim().replace(/^["']|["']$/g, '')
  if (!trimmed) return ''

  if (trimmed.startsWith('file://')) {
    try {
      return decodeURIComponent(new URL(trimmed).pathname).replace(/^\/([A-Za-z]:\/)/, '$1').replace(/\//g, path.sep)
    } catch {
      return ''
    }
  }

  return trimmed
}

function uniqueExistingPaths(paths) {
  const seen = new Set()
  return paths
    .map(normalizeFilePath)
    .filter((filePath) => {
      if (!filePath || seen.has(filePath.toLowerCase())) return false
      try {
        if (!fs.existsSync(filePath)) return false
      } catch {
        return false
      }
      seen.add(filePath.toLowerCase())
      return true
    })
}

function parseUriList(text = '') {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line && !line.startsWith('#'))
}

function extractPathsFromText(text = '') {
  const lines = text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)

  if (!lines.length || lines.length > 50) return []
  return uniqueExistingPaths(lines)
}

function decodeZeroTerminatedList(buffer, encoding) {
  const raw = buffer.toString(encoding).replace(/\u0000+$/g, '')
  return raw.split('\u0000').filter(Boolean)
}

function parseHDrop(buffer) {
  if (buffer.length < 20) return []
  const offset = buffer.readUInt32LE(0)
  const wide = buffer.readUInt32LE(16) !== 0
  if (offset >= buffer.length) return []
  return decodeZeroTerminatedList(buffer.subarray(offset), wide ? 'utf16le' : 'latin1')
}

function osFormatName(type) {
  const match = type.match(/^electron application\/osclipboard;format="(.+)"$/)
  return match ? match[1] : type
}

function hasFileFormatHint(formats) {
  return formats.some((type) => {
    const lowerFormat = osFormatName(type).toLowerCase()
    return lowerFormat.includes('filename') || lowerFormat.includes('hdrop') || lowerFormat.includes('filedrop') || type === 'text/uri-list'
  })
}

async function maybeAwait(value) {
  return value && typeof value.then === 'function' ? value : Promise.resolve(value)
}

async function blobToBuffer(blob) {
  const arrayBuffer = await blob.arrayBuffer()
  return Buffer.from(arrayBuffer)
}

async function readClipboardText() {
  try {
    if (typeof clipboard.readText === 'function') return await maybeAwait(clipboard.readText())
  } catch {
    return ''
  }
  return ''
}

async function readClipboardHtml() {
  try {
    if (typeof clipboard.readHTML === 'function') return await maybeAwait(clipboard.readHTML())
  } catch {
    return ''
  }
  return ''
}

async function readPowerShellFileDropList() {
  if (process.platform !== 'win32') return []

  const script = `
Add-Type -AssemblyName System.Windows.Forms
$files = [System.Windows.Forms.Clipboard]::GetFileDropList()
foreach ($file in $files) { [Console]::Out.WriteLine($file) }
`

  return new Promise((resolve) => {
    const child = spawnPowerShell(script, { sta: true })
    let output = ''
    child.stdout.on('data', (chunk) => {
      output += chunk.toString('utf8')
    })
    child.on('error', () => resolve([]))
    child.on('close', () => {
      resolve(uniqueExistingPaths(output.split(/\r?\n/).filter(Boolean)))
    })
  })
}

async function readClipboardSnapshot() {
  const snapshot = {
    text: '',
    html: '',
    imageDataUrl: '',
    files: [],
    formats: []
  }

  snapshot.text = await readClipboardText()
  snapshot.html = await readClipboardHtml()
  snapshot.files.push(...extractPathsFromText(snapshot.text), ...parseUriList(snapshot.text))

  if (typeof clipboard.read === 'function') {
    try {
      const items = await clipboard.read()
      for (const item of items || []) {
        for (const type of item.types || []) {
          snapshot.formats.push(type)
          const formatName = osFormatName(type)
          const lowerFormat = formatName.toLowerCase()

          try {
            const blob = await item.getType(type)

            if (!snapshot.text && type === 'text/plain') snapshot.text = await blob.text()
            if (!snapshot.html && type === 'text/html') snapshot.html = await blob.text()
            if (type === 'text/uri-list') snapshot.files.push(...parseUriList(await blob.text()))

            if (!snapshot.imageDataUrl && type.startsWith('image/')) {
              const imageBuffer = await blobToBuffer(blob)
              const image = nativeImage.createFromBuffer(imageBuffer)
              if (!image.isEmpty()) snapshot.imageDataUrl = image.toDataURL()
            }

            if (type.startsWith('electron application/osclipboard') || /filename|hdrop|drop/i.test(lowerFormat)) {
              const rawBuffer = await blobToBuffer(blob)
              if (lowerFormat.includes('hdrop')) snapshot.files.push(...parseHDrop(rawBuffer))
              if (lowerFormat.includes('filenamew')) snapshot.files.push(...decodeZeroTerminatedList(rawBuffer, 'utf16le'))
              if (lowerFormat === 'filename') snapshot.files.push(...decodeZeroTerminatedList(rawBuffer, 'latin1'))
            }
          } catch {
            // Some clipboard formats are intentionally unreadable by Chromium.
          }
        }
      }
    } catch {
      // Older Electron builds can still expose the legacy clipboard helpers below.
    }
  }

  if (!snapshot.imageDataUrl && typeof clipboard.readImage === 'function') {
    try {
      const image = clipboard.readImage()
      if (image && !image.isEmpty()) snapshot.imageDataUrl = image.toDataURL()
    } catch {
      // Ignore legacy image read errors and continue with text/file content.
    }
  }

  if (snapshot.html) {
    const fileUrls = [...snapshot.html.matchAll(/file:\/\/[^"'<>\s)]+/gi)].map((match) => match[0])
    snapshot.files.push(...fileUrls)
  }

  snapshot.files = uniqueExistingPaths(snapshot.files)

  if (process.platform === 'win32' && snapshot.files.length === 0 && (snapshot.formats.length === 0 || hasFileFormatHint(snapshot.formats))) {
    snapshot.files = await readPowerShellFileDropList()
  }

  return snapshot
}

function fileRecord(filePath) {
  let stats
  try {
    stats = fs.statSync(filePath)
  } catch {
    stats = null
  }

  const kind = fileKind(filePath)
  return {
    path: filePath,
    name: path.basename(filePath),
    extension: path.extname(filePath).replace('.', '').toUpperCase(),
    kind,
    size: stats?.isFile() ? stats.size : 0,
    mediaUrl: kind === 'image' || kind === 'video' ? mediaUrlFor(filePath) : ''
  }
}

function buildFileEntry(snapshot) {
  const files = snapshot.files.map(fileRecord)
  const hasVideo = files.some((file) => file.kind === 'video')
  const allImages = files.length > 0 && files.every((file) => file.kind === 'image')
  const primary = files.find((file) => file.kind === 'video') || files.find((file) => file.kind === 'image') || files[0]
  const type = hasVideo ? 'video' : allImages ? 'image' : 'file'

  return {
    type,
    title: primary?.name || '文件',
    preview: files.length > 1 ? `${primary?.name || '文件'} 等 ${files.length} 个文件` : primary?.name || '文件',
    text: snapshot.files.join('\n'),
    files,
    mediaUrl: primary?.mediaUrl || '',
    value: snapshot.files.join('\n'),
    source: '文件剪贴板',
    formats: snapshot.formats
  }
}

function buildImageEntry(snapshot) {
  return {
    type: 'image',
    title: '剪贴板图片',
    preview: '图片内容',
    dataUrl: snapshot.imageDataUrl,
    value: snapshot.imageDataUrl,
    source: '系统剪贴板',
    formats: snapshot.formats
  }
}

function buildTextEntry(snapshot) {
  const text = snapshot.text || ''
  const firstLine = text.split(/\r?\n/).find((line) => line.trim()) || text

  return {
    type: 'text',
    title: firstLine.slice(0, 48) || '文本内容',
    preview: text,
    text,
    html: snapshot.html || '',
    value: text,
    source: '系统剪贴板',
    formats: snapshot.formats
  }
}

function buildEntry(snapshot) {
  if (snapshot.files.length) return buildFileEntry(snapshot)
  if (snapshot.imageDataUrl) return buildImageEntry(snapshot)
  if (snapshot.text && snapshot.text.trim()) return buildTextEntry(snapshot)
  return null
}

function signatureFor(entry) {
  if (!entry) return ''
  if (entry.files?.length) return `files:${entry.files.map((file) => `${file.path}`.toLowerCase()).join('|')}`
  if (entry.type === 'image') {
    const data = entry.dataUrl || entry.value || ''
    return `image:${data.length}:${data.slice(0, 160)}:${data.slice(-160)}`
  }
  return `text:${entry.text || entry.value || entry.preview || ''}`
}

function upsertEntry(entry) {
  const signature = signatureFor(entry)
  const existingIndex = entries.findIndex((item) => signatureFor(item) === signature)
  const createdAt = Date.now()

  if (existingIndex >= 0) {
    const existing = entries[existingIndex]
    const merged = {
      ...existing,
      ...entry,
      id: existing.id,
      locked: Boolean(existing.locked),
      createdAt
    }
    entries = [merged, ...entries.filter((_, index) => index !== existingIndex)]
    return merged
  }

  const created = {
    ...entry,
    id: `${createdAt}-${Math.random().toString(16).slice(2)}`,
    createdAt,
    locked: false
  }
  entries = [created, ...entries]
  return created
}

async function captureClipboard({ fromSequenceChange = false } = {}) {
  if (paused || Date.now() < suppressCaptureUntil) return

  const snapshot = await readClipboardSnapshot()
  const entry = buildEntry(snapshot)
  if (!entry) return

  const signature = signatureFor(entry)
  if (!fromSequenceChange && signature === lastPollingSignature) return
  lastPollingSignature = signature

  upsertEntry(entry)
  pruneEntries()
  writeStore()
  notifyHistoryChanged()
}

function spawnPowerShell(script, { sta = false } = {}) {
  const encoded = Buffer.from(script, 'utf16le').toString('base64')
  const args = [
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-WindowStyle',
    'Hidden'
  ]

  if (sta) args.push('-STA')
  args.push('-EncodedCommand', encoded)

  return spawn('powershell.exe', args, {
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'ignore']
  })
}

function startPollingWatcher() {
  if (pollTimer) return
  pollTimer = setInterval(() => {
    captureClipboard({ fromSequenceChange: false })
  }, WATCH_INTERVAL)
}

function startClipboardWatcher() {
  if (process.platform !== 'win32') {
    startPollingWatcher()
    return
  }

  const script = `
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ClipboardNative {
  [DllImport("user32.dll")]
  public static extern uint GetClipboardSequenceNumber();
}
"@
$last = [ClipboardNative]::GetClipboardSequenceNumber()
while ($true) {
  Start-Sleep -Milliseconds 250
  $next = [ClipboardNative]::GetClipboardSequenceNumber()
  if ($next -ne $last) {
    $last = $next
    [Console]::Out.WriteLine($next)
    [Console]::Out.Flush()
  }
}
`

  clipboardWatcher = spawnPowerShell(script)

  clipboardWatcher.stdout.on('data', () => {
    captureClipboard({ fromSequenceChange: true })
  })

  clipboardWatcher.on('error', startPollingWatcher)
  clipboardWatcher.on('exit', startPollingWatcher)
}

async function writeTextToClipboard(entry) {
  const text = typeof entry === 'string' ? entry : entry.text || entry.value || entry.preview || ''
  const html = typeof entry === 'string' ? '' : entry.html || ''

  if (ClipboardItem && typeof clipboard.write === 'function') {
    try {
      const items = {
        'text/plain': new Blob([text], { type: 'text/plain' })
      }
      if (html) items['text/html'] = new Blob([html], { type: 'text/html' })
      await clipboard.write([new ClipboardItem(items)])
      return true
    } catch {
      // Fall through to the legacy text helper.
    }
  }

  if (typeof clipboard.writeText === 'function') {
    await maybeAwait(clipboard.writeText(text))
    return true
  }

  return Boolean(text)
}

async function writeImageToClipboard(entry) {
  const image = nativeImage.createFromDataURL(entry.dataUrl || entry.value || '')
  if (image.isEmpty()) return false

  if (typeof clipboard.writeImage === 'function') {
    await maybeAwait(clipboard.writeImage(image))
    return true
  }

  if (typeof clipboard.write === 'function' && ClipboardItem) {
    const png = image.toPNG()
    await clipboard.write([
      new ClipboardItem({
        'image/png': new Blob([png], { type: 'image/png' })
      })
    ])
    return true
  }

  return false
}

async function writeFilesToClipboard(filePaths) {
  const existing = uniqueExistingPaths(filePaths)
  if (!existing.length) return false

  if (process.platform !== 'win32') {
    await writeTextToClipboard(existing.join('\n'))
    return true
  }

  const jsonPaths = JSON.stringify(existing)
  const script = `
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Collections.Specialized
$paths = ConvertFrom-Json @'
${jsonPaths}
'@
$collection = New-Object System.Collections.Specialized.StringCollection
foreach ($path in $paths) { [void]$collection.Add([string]$path) }
[System.Windows.Forms.Clipboard]::SetFileDropList($collection)
`

  return new Promise((resolve) => {
    const child = spawnPowerShell(script, { sta: true })
    child.on('error', () => resolve(false))
    child.on('close', (code) => resolve(code === 0))
  })
}

function createWindow() {
  mainWindow = new BrowserWindow({
    ...dockBounds(dock.expanded),
    frame: false,
    resizable: false,
    show: false,
    skipTaskbar: false,
    title: '复制档案',
    backgroundColor: '#f7f8f5',
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  })

  mainWindow.setMenuBarVisibility(false)
  mainWindow.setAlwaysOnTop(true, 'floating')

  if (app.isPackaged) {
    mainWindow.loadFile(path.join(__dirname, '..', 'dist', 'index.html'))
  } else {
    mainWindow.loadURL(process.env.VITE_DEV_SERVER_URL || 'http://127.0.0.1:5173')
  }

  mainWindow.once('ready-to-show', () => {
    mainWindow.show()
    applyDockBounds(false)
  })

  mainWindow.on('blur', () => {
    if (!dock.pinned) setDockExpanded(false)
  })

  mainWindow.on('moved', saveWindowDockPosition)

  mainWindow.on('close', (event) => {
    if (isQuitting) return
    event.preventDefault()
    setDockExpanded(false, true)
  })
}

app.whenReady().then(() => {
  protocol.handle('atlas-media', (request) => {
    const filePath = resolveMediaUrl(request.url)
    if (!filePath || !fs.existsSync(filePath)) return new Response('Not found', { status: 404 })
    return net.fetch(pathToFileURL(filePath).toString())
  })

  readStore()
  createWindow()
  startClipboardWatcher()
  startEdgeWatcher()
  captureClipboard({ fromSequenceChange: false })

  setInterval(() => {
    pruneEntries()
    coalesceDuplicates()
    writeStore()
    notifyHistoryChanged()
  }, 60 * 60 * 1000)

  screen.on('display-metrics-changed', () => applyDockBounds(false))
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow()
  setDockExpanded(true)
})

app.on('before-quit', () => {
  isQuitting = true
  if (clipboardWatcher) clipboardWatcher.kill()
  if (pollTimer) clearInterval(pollTimer)
  if (edgeWatchTimer) clearInterval(edgeWatchTimer)
  if (collapseTimer) clearTimeout(collapseTimer)
})

app.on('window-all-closed', () => {})

ipcMain.handle('history:get', () => ({ entries, paused, dock }))

ipcMain.handle('history:pause', (_event, value) => {
  paused = Boolean(value)
  writeStore()
  return paused
})

ipcMain.handle('history:clear', () => {
  entries = entries.filter((entry) => entry.locked)
  writeStore()
  notifyHistoryChanged()
  return entries
})

ipcMain.handle('history:toggle-lock', (_event, id) => {
  entries = entries.map((item) => (item.id === id ? { ...item, locked: !item.locked } : item))
  writeStore()
  notifyHistoryChanged()
  return entries.find((item) => item.id === id) || null
})

ipcMain.handle('history:copy', async (_event, id) => {
  const entry = entries.find((item) => item.id === id)
  if (!entry) return false

  suppressCaptureUntil = Date.now() + 1600
  lastPollingSignature = signatureFor(entry)

  let copied = false
  if (entry.files?.length) copied = await writeFilesToClipboard(entry.files.map((file) => file.path))
  else if (entry.type === 'image') copied = await writeImageToClipboard(entry)
  else copied = await writeTextToClipboard(entry)

  if (copied) {
    entries = [{ ...entry, createdAt: Date.now() }, ...entries.filter((item) => item.id !== id)]
    writeStore()
    notifyHistoryChanged()
    hideAndPaste()
  }

  return copied
})

ipcMain.handle('history:open-path', async (_event, filePath) => {
  const normalized = normalizeFilePath(filePath)
  if (!normalized || !fs.existsSync(normalized)) return false
  await shell.showItemInFolder(normalized)
  return true
})

ipcMain.handle('dock:get', () => dock)
ipcMain.handle('dock:expand', () => setDockExpanded(true))
ipcMain.handle('dock:collapse', () => setDockExpanded(false, true))
ipcMain.handle('dock:set-side', (_event, side) => setDockSide(side))
ipcMain.handle('dock:set-pinned', (_event, pinned) => setDockPinned(pinned))
ipcMain.handle('app:quit', () => app.quit())
