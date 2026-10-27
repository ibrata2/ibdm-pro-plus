// Test server for the engine tests: byte ranges, throttling, a server without ranges, Arabic Content-Disposition,
// and a web page with an HTML5 video for yt-dlp. Usage: node server.js <port> <dataDir>
const http = require('http');
const fs = require('fs');
const path = require('path');
const port = +process.argv[2];
const dir = process.argv[3];
const RATE = 3 * 1024 * 1024; // bytes per second per connection

function sendFile(req, res, file, { ranges = true, throttle = true, headers = {} } = {}) {
  const size = fs.statSync(file).size;
  let start = 0, end = size - 1, code = 200;
  const m = ranges && /bytes=(\d*)-(\d*)/.exec(req.headers.range || '');
  if (m) {
    start = m[1] ? +m[1] : size - +m[2];
    end = m[2] && m[1] ? Math.min(+m[2], size - 1) : size - 1;
    code = 206;
  }
  const h = { 'Content-Length': end - start + 1, 'Content-Type': 'application/octet-stream', ...headers };
  if (ranges) h['Accept-Ranges'] = 'bytes';
  if (code === 206) h['Content-Range'] = `bytes ${start}-${end}/${size}`;
  res.writeHead(code, h);
  if (req.method === 'HEAD') return res.end();
  const stream = fs.createReadStream(file, { start, end, highWaterMark: 64 * 1024 });
  if (!throttle) return stream.pipe(res);
  stream.on('data', chunk => {
    stream.pause();
    res.write(chunk);
    setTimeout(() => stream.resume(), (chunk.length / RATE) * 1000);
  });
  stream.on('end', () => res.end());
  req.on('close', () => stream.destroy());
}

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/file.bin') return sendFile(req, res, path.join(dir, 'file.bin'));
  if (url.pathname === '/noranges.bin') return sendFile(req, res, path.join(dir, 'small.bin'), { ranges: false });
  if (url.pathname === '/dl') return sendFile(req, res, path.join(dir, 'small.bin'), {
    headers: { 'Content-Disposition': "attachment; filename=\"x.zip\"; filename*=UTF-8''" + encodeURIComponent('ملف تجربة.zip') },
  });
  if (url.pathname === '/files/archive.zip') return sendFile(req, res, path.join(dir, 'small.bin'), { throttle: false, headers: { 'Content-Type': 'application/zip' } });
  if (url.pathname === '/redirect') { res.writeHead(302, { Location: '/file.bin' }); return res.end(); }
  if (url.pathname === '/needcookie.bin') {
    if (req.headers.cookie !== 'session=abc') { res.writeHead(403); return res.end('no'); }
    return sendFile(req, res, path.join(dir, 'small.bin'), { throttle: false });
  }
  if (url.pathname === '/big.mp4') return sendFile(req, res, path.join(dir, 'file.bin'), { headers: { 'Content-Type': 'video/mp4' } });
  if (url.pathname === '/clip.mp4') return sendFile(req, res, path.join(dir, 'clip.mp4'), { throttle: false, headers: { 'Content-Type': 'video/mp4' } });
  if (url.pathname === '/links.html') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Set-Cookie': 'session=abc; Path=/' });
    return res.end('<html><body><a id="zip" href="/files/archive.zip">zip</a> <a id="txt" href="/note.txt" download>txt</a></body></html>');
  }
  if (url.pathname === '/note.txt') { res.writeHead(200, { 'Content-Type': 'text/plain', 'Content-Disposition': 'attachment; filename="note.txt"' }); return res.end('hello'); }
  if (url.pathname === '/page.html') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    return res.end('<html><head><title>فيديو تجربة</title></head><body><video controls width="640" height="360" src="/clip.mp4"></video></body></html>');
  }
  res.writeHead(404); res.end();
}).listen(port, '127.0.0.1', () => console.log('ready'));
