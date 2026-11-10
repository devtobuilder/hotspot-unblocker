const net = require('node:net');
const http = require('node:http');
const {spawn} = require('node:child_process');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const automatic = process.argv.includes('--auto');
(async () => {
 const origin = http.createServer((req,res) => { assert.equal(req.headers['proxy-authorization'], undefined); let body=''; req.on('data', b=>body+=b); req.on('end', ()=>res.end('origin:'+req.url+':'+body)); });
 const echo = net.createServer(s=>s.pipe(s));
 await new Promise(r=>origin.listen(0,'127.0.0.1',r));
 await new Promise(r=>echo.listen(0,'127.0.0.1',r));
 const port = 18087;
 const child=spawn('dotnet',['bin/Release/net7.0/hotspot-unblocker.dll','--bind','127.0.0.1','--port',String(port), ...(automatic ? ['--auto'] : [])]);
 try {
  let output='';
  const password=await new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>reject(Error('Startup timeout: '+output)),10000);
   child.stdout.on('data',b=>{output+=b;const match=automatic ? (output.includes('URL: http://') ? [null, 'unused'] : null) : output.match(/Password: ([A-F0-9]+)/);if(match){clearTimeout(timer);resolve(match[1]);}});
   child.once('exit',code=>{clearTimeout(timer);reject(Error('Exited '+code));});
  });
  const auth=automatic ? '' : 'Proxy-Authorization: Basic '+Buffer.from('iphone:'+password).toString('base64')+'\r\n';
  async function exchange(text) {return new Promise((resolve,reject)=>{let out='';const s=net.connect(port,'127.0.0.1',()=>s.end(text));s.setTimeout(5000,()=>s.destroy(Error('Timeout')));s.on('data',b=>out+=b);s.on('end',()=>resolve(out));s.on('error',reject);});}
  if (!automatic) assert.match(await exchange('GET http://127.0.0.1/ HTTP/1.1\r\n\r\n'),/407/);
  const pacResponse = await exchange('GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n');
  assert.match(pacResponse, /application\/x-ns-proxy-autoconfig/);
  const pac = pacResponse.split('\r\n\r\n')[1];
  const context = {}; vm.createContext(context); vm.runInContext(pac, context);
  assert.equal(context.FindProxyForURL('https://example.com/', 'example.com'), 'PROXY 127.0.0.1:'+port);
  assert.ok(!pac.includes('DIRECT'));
  assert.ok(!pac.includes(password));
  assert.match(await exchange('GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n'), /PC proxy is reachable/);
  assert.match(await exchange('GET http://127.0.0.1:'+origin.address().port+'/check HTTP/1.1\r\n'+auth+'\r\n'),/origin:\/check:/);
  assert.match(await exchange('POST http://127.0.0.1:'+origin.address().port+'/post HTTP/1.1\r\n'+auth+'Content-Length: 4\r\n\r\ntest'),/origin:\/post:test/);
  assert.match(await exchange('CONNECT 127.0.0.1:'+echo.address().port+' HTTP/1.1\r\n'+auth+'\r\nhello-tunnel'),/200 Connection Established[\s\S]*hello-tunnel/); // half-close below
  // Exercise a live full-duplex tunnel beyond the initial response, with binary
  // data and a client half-close. Small string-only exchanges miss relay bugs.
  await new Promise((resolve, reject) => {
   const payload = require('node:crypto').randomBytes(2 * 1024 * 1024);
   const socket = net.connect(port, '127.0.0.1', () => socket.write('CONNECT 127.0.0.1:'+echo.address().port+' HTTP/1.1\r\n'+auth+'\r\n'));
   let header = Buffer.alloc(0), ready = false, chunks = [];
   socket.setTimeout(10000, () => socket.destroy(Error('Streaming tunnel timed out')));
   socket.on('error', reject);
   socket.on('data', b => {
    if (ready) { chunks.push(b); return; }
    header = Buffer.concat([header,b]); const end = header.indexOf('\r\n\r\n');
    if (end < 0) return;
    assert.match(header.subarray(0,end).toString(), /200 Connection Established/);
    chunks.push(header.subarray(end+4)); ready = true;
    setTimeout(() => { socket.write(payload.subarray(0, 1000000)); setTimeout(() => socket.end(payload.subarray(1000000)), 150); }, 150);
   });
   socket.on('end', () => { try { assert.deepEqual(Buffer.concat(chunks),payload); resolve(); } catch(e) {reject(e);} });
  });
  console.log('PASS: PAC, health, authentication, HTTP GET/POST, buffered CONNECT, delayed 2 MiB binary tunnel and half-close');
 } finally {child.kill(); origin.closeAllConnections();origin.close();echo.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});



