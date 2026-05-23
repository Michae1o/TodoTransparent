const { app, BrowserWindow, ipcMain, screen } = require('electron');
const path = require('path');
const fs = require('fs');

let DATA_PATH;
let mainWindow;

function getDataPath() {
  if (!DATA_PATH) {
    DATA_PATH = path.join(app.getPath('userData'), 'todos.json');
  }
  return DATA_PATH;
}

function loadTodos() {
  try {
    const p = getDataPath();
    if (fs.existsSync(p)) {
      return JSON.parse(fs.readFileSync(p, 'utf8'));
    }
  } catch (e) {
    console.error('Failed to load todos:', e);
  }
  return [];
}

function saveTodos(todos) {
  try {
    fs.writeFileSync(getDataPath(), JSON.stringify(todos, null, 2));
  } catch (e) {
    console.error('Failed to save todos:', e);
  }
}

function createWindow() {
  const { width, height } = screen.getPrimaryDisplay().workAreaSize;

  mainWindow = new BrowserWindow({
    width: 320,
    height: 500,
    x: width - 340,
    y: 100,
    transparent: true,
    frame: false,
    alwaysOnTop: true,
    skipTaskbar: false,
    resizable: true,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
      preload: path.join(__dirname, 'preload.js')
    }
  });

  mainWindow.loadFile('index.html');

  // 开发时打开开发者工具
  // mainWindow.webContents.openDevTools({ mode: 'detach' });
}

app.whenReady().then(createWindow);

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});

// IPC
ipcMain.handle('load-todos', () => loadTodos());
ipcMain.handle('save-todos', (event, todos) => saveTodos(todos));

ipcMain.on('set-ignore-mouse-events', (event, ignore, options) => {
  if (mainWindow) {
    mainWindow.setIgnoreMouseEvents(ignore, options);
  }
});

ipcMain.on('window-move', (event, dx, dy) => {
  if (mainWindow) {
    const pos = mainWindow.getPosition();
    mainWindow.setPosition(pos[0] + dx, pos[1] + dy);
  }
});

ipcMain.on('window-resize', (event, w, h) => {
  if (mainWindow && w > 200 && h > 150) {
    mainWindow.setSize(Math.round(w), Math.round(h));
  }
});

ipcMain.on('minimize-window', () => {
  if (mainWindow) mainWindow.minimize();
});

ipcMain.on('close-window', () => {
  if (mainWindow) mainWindow.close();
});
