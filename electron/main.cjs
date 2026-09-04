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
const WINDOW_WIDTH = 620
const EDGE_PEEK = 14
const WATCH_INTERVAL = 450

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
let dock = { side: 'right', pinned: false, expanded: true }
let lastPollingSignature = ''
let suppressCaptureUntil = 0
let clipboardWatcher = null
let pollTimer = null
let isQuitting = false

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
      expanded: Boolean(store.dock?.pinned)
    }
  } catch {
    entries = []
  }
  pruneEntries()
}

function writeStore() {
  fs.mkdirSync(path.dirname(dataPath()), { recursive: true })
  fs.writeFileSync(dataPath(), JSON.stringify({ entries, paused, dock }, null, 2), 'utf8')
}

function pruneEntries() {
  const threshold = Date.now() - MAX_AGE
  entries = entries.filter((entry) => entry.createdAt > threshold)
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
  if (!mainWindow || mainWindow.isDestroyed()) return screen.getPrimaryDisplay().workArea
  return screen.getDisplayMatching(mainWindow.getBounds()).workArea
}

function dockBounds(expanded = dock.expanded) {
  const workArea = getDisplayWorkArea()
  const height = workArea.height
  const width = Math.min(WINDOW_WIDTH, Math.max(440, workArea.width - EDGE_PEEK))
  const shownX = dock.side === 'left' ? workArea.x : workArea.x + workArea.width - width
  const hiddenX = dock.side === 'left' ? workArea.x - width + EDGE_PEEK : workArea.x + workArea.width - EDGE_PEEK

  return {
    x: expanded ? shownX : hiddenX,
    y: workArea.y,
    width,
    height
  }
}

function applyDockBounds(animated = true) {
  if (!mainWindow || mainWindow.isDestroyed()) return
  mainWindow.setBounds(dockBounds(), animated)
  mainWindow.setAlwaysOnTop(true, 'floating')
}

function setDockExpanded(expanded, force = false) {
  if (dock.pinned && !expanded && !force) return dock
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

  if (process.platform === 'win32' && snapshot.files.length === 0) {
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
  if (entry.files?.length) return `files:${entry.files.map((file) => file.path).join('|')}`
  if (entry.type === 'image') return `image:${(entry.dataUrl || entry.value || '').slice(0, 500)}`
  return `text:${entry.text || entry.value || entry.preview || ''}`
}

async function captureClipboard({ allowDuplicate = false } = {}) {
  if (paused || Date.now() < suppressCaptureUntil) return

  const snapshot = await readClipboardSnapshot()
  const entry = buildEntry(snapshot)
  if (!entry) return

  const signature = signatureFor(entry)
  if (!allowDuplicate && signature === lastPollingSignature) return
  lastPollingSignature = signature

  entries = [
    {
      id: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
      createdAt: Date.now(),
      ...entry
    },
    ...entries
  ]

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
    captureClipboard({ allowDuplicate: false })
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
    captureClipboard({ allowDuplicate: true })
  })

  clipboardWatcher.on('error', startPollingWatcher)
  clipboardWatcher.on('exit', startPollingWatcher)
}

async function writeTextToClipboard(text) {
  if (typeof clipboard.writeText === 'function') {
    await maybeAwait(clipboard.writeText(text))
  }
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
  captureClipboard({ allowDuplicate: false })

  setInterval(() => {
    pruneEntries()
    writeStore()
    notifyHistoryChanged()
  }, 60 * 60 * 1000)
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow()
  setDockExpanded(true)
})

app.on('before-quit', () => {
  isQuitting = true
  if (clipboardWatcher) clipboardWatcher.kill()
})

app.on('window-all-closed', () => {})

ipcMain.handle('history:get', () => ({ entries, paused, dock }))

ipcMain.handle('history:pause', (_event, value) => {
  paused = Boolean(value)
  writeStore()
  return paused
})

ipcMain.handle('history:clear', () => {
  entries = []
  writeStore()
  notifyHistoryChanged()
  return entries
})

ipcMain.handle('history:copy', async (_event, id) => {
  const entry = entries.find((item) => item.id === id)
  if (!entry) return false

  suppressCaptureUntil = Date.now() + 900

  let copied = false
  if (entry.files?.length) copied = await writeFilesToClipboard(entry.files.map((file) => file.path))
  else if (entry.type === 'image') copied = await writeImageToClipboard(entry)
  else copied = await writeTextToClipboard(entry.text || entry.value || entry.preview || '').then(() => true)

  if (copied) {
    entries = [entry, ...entries.filter((item) => item.id !== id)]
    writeStore()
    notifyHistoryChanged()
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
ipcMain.handle('dock:collapse', () => setDockExpanded(false))
ipcMain.handle('dock:set-side', (_event, side) => setDockSide(side))
ipcMain.handle('dock:set-pinned', (_event, pinned) => setDockPinned(pinned))
ipcMain.handle('app:quit', () => app.quit())
