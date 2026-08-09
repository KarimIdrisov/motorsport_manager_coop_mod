const net = require('net');
const { spawn } = require('child_process');

const testPort = Number(process.env.MM_COOP_PORT || 27154);
const server = spawn(process.execPath, ['lan_server.js'], { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, MM_COOP_PORT: String(testPort) } });
const wait = (ms) => new Promise(resolve => setTimeout(resolve, ms));

function client(name) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: '127.0.0.1', port: testPort });
    socket.setEncoding('utf8');
    let buffer = '';
    const messages = [];
    socket.on('data', chunk => {
      buffer += chunk;
      let end;
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (line) messages.push(JSON.parse(line));
      }
    });
    socket.on('connect', () => {
      socket.write(JSON.stringify({ type: 'hello', protocol: 0, name }) + '\n');
      resolve({ socket, messages });
    });
    socket.on('error', reject);
  });
}

async function main() {
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('LAN server startup timeout')), 3000);
    server.stdout.on('data', chunk => {
      if (String(chunk).includes('listening')) { clearTimeout(timer); resolve(); }
    });
    server.once('error', reject);
  });
  const host = await client('Test Host');
  const guest = await client('Test Guest');
  await wait(100);
  if (host.messages[0].role !== 'host') throw new Error('first client is not host');
  if (guest.messages[0].role !== 'client') throw new Error('second client is not client');

  host.socket.write(JSON.stringify({ type: 'action', id: 'a1', kind: 'play_skip_sim' }) + '\n');
  await wait(100);
  if (!host.messages.some(m => m.type === 'action_ack' && m.revision === 1)) throw new Error('missing host ack');
  if (!guest.messages.some(m => m.type === 'action' && m.kind === 'play_skip_sim')) throw new Error('missing guest broadcast');

  host.socket.write(JSON.stringify({ type: 'action', id: 'race1', kind: 'pit_tyres', target: 42, value: 2, aux: 1, flag: 1 }) + '\n');
  await wait(100);
  if (!guest.messages.some(m => m.type === 'action' && m.kind === 'pit_tyres' && m.target === 42 && m.value === 2 && m.aux === 1 && m.flag === 1))
    throw new Error('race target payload was not preserved');

  guest.socket.write(JSON.stringify({ type: 'action', id: 'save1', kind: 'manual_save' }) + '\n');
  await wait(100);
  if (!guest.messages.some(m => m.type === 'error' && m.message === 'Only host may save')) throw new Error('guest save was not rejected');

  host.socket.destroy(); guest.socket.destroy(); server.kill();
  console.log('LAN TEST PASS: roles, revisions, targeted race payload, host-only save');
}

main().catch(error => {
  server.kill();
  console.error(`LAN TEST FAIL: ${error.message}`);
  process.exitCode = 1;
});
