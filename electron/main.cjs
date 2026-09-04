const { app, BrowserWindow, clipboard, nativeImage, ipcMain, screen } = require('electron')
const fs = require('node:fs')
const path = require('node:path')

const MAX_AGE = 48 * 60 * 60 * 1000
let mainWindow
let entries = []
let paused = false
let lastFingerprint = ''

const dataPath = () => path.join(app.getPath('userData'), 'clipboard-history.json')

function readStore() {
  try {
    const store = JSON.parse(fs.readFileSync(dataPath(), 'utf8'))
    entries = Array.isArray(store.entries) ? store.entries : []
    paused = Boolean(store.paused)
  } catch {
    entries = []
  }
  pruneEntries()
}

function writeStore() {
  fs.mkdirSync(path.dirname(dataPath()), { recursive: true })
  fs.writeFileSync(dataPath(), JSON.stringify({ entries, paused }, null, 2))
}

function pruneEntries() {
  const threshold = Date.now() - MAX_AGE
  entries = entries.filter((entry) => entry.createdAt > threshold)
}

function fingerprintFor(type, value) {
  return `${type}:${value}`
}

function captureClipboard() {
  if (paused || !mainWindow || mainWindow.isDestroyed()) return
  let type = 'text'
  let value = clipboard.readText()
  let preview = value
  const image = clipboard.readImage()

  if (!value && !image.isEmpty()) {
    type = 'image'
    value = image.toDataURL()
    preview = '剪贴板图片'
  }

  if (!value) return
  const fingerprint = fingerprintFor(type, type === 'image' ? value.slice(0, 160) : value)
  if (fingerprint === lastFingerprint) return
  lastFingerprint = fingerprint

  const entry = {
    id: `${Date.now()}-${Math.random().toString(16).slice(2)}`,
    type,
    value,
    preview,
    createdAt: Date.now(),
    source: '系统剪贴板'
  }
  entries = [entry, ...entries]
  pruneEntries()
  writeStore()
  mainWindow.webContents.send('history:updated', entries)
}

function createWindow() {
  const { width } = screen.getPrimaryDisplay().workAreaSize
  mainWindow = new BrowserWindow({
    width: 440,
    height: 860,
    minWidth: 380,
    minHeight: 600,
    x: width - 440,
    y: 70,
    frame: false,
    resizable: true,
    show: false,
    skipTaskbar: false,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false }
  })

  const devUrl = process.env.VITE_DEV_SERVER_URL || 'http://127.0.0.1:5173'
  mainWindow.loadURL(devUrl)
  mainWindow.once('ready-to-show', () => mainWindow.show())
  mainWindow.on('blur', () => {
    if (!mainWindow.isDestroyed()) mainWindow.hide()
  })
}

app.whenReady().then(() => {
  readStore()
  createWindow()
  setInterval(captureClipboard, 500)
  setInterval(() => { pruneEntries(); writeStore() }, 60 * 60 * 1000)
})

ipcMain.handle('history:get', () => ({ entries, paused }))
ipcMain.handle('history:pause', (_event, value) => {
  paused = Boolean(value)
  writeStore()
  return paused
})
ipcMain.handle('history:clear', () => {
  entries = []
  writeStore()
  return entries
})
ipcMain.handle('history:copy', (_event, id) => {
  const entry = entries.find((item) => item.id === id)
  if (!entry) return false
  if (entry.type === 'image') clipboard.writeImage(nativeImage.createFromDataURL(entry.value))
  else clipboard.writeText(entry.value)
  entries = [entry, ...entries.filter((item) => item.id !== id)]
  lastFingerprint = fingerprintFor(entry.type, entry.type === 'image' ? entry.value.slice(0, 160) : entry.value)
  writeStore()
  mainWindow.webContents.send('history:updated', entries)
  return true
})

app.on('window-all-closed', (event) => event.preventDefault())
