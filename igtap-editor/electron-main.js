const { app, BrowserWindow } = require('electron');
const http = require('http');

let win;

// Poll until the Express server is accepting connections, then open the window
function waitReady(cb, deadline) {
  if (Date.now() > deadline) { cb(); return; }
  http.get('http://localhost:3000/', cb)
      .on('error', () => setTimeout(() => waitReady(cb, deadline), 80));
}

app.whenReady().then(() => {
  require('./server.js');   // starts Express (app.listen is non-blocking)

  waitReady(() => {
    win = new BrowserWindow({
      width:  1600,
      height: 960,
      minWidth:  900,
      minHeight: 600,
      title:  'IGTAP Map Editor',
      backgroundColor: '#0f172a',
      autoHideMenuBar: true,
      webPreferences: {
        nodeIntegration:  false,
        contextIsolation: true,
      },
    });

    win.loadURL('http://localhost:3000/');
    win.on('closed', () => { win = null; });
  }, Date.now() + 10000);
});

app.on('window-all-closed', () => app.quit());
