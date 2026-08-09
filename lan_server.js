const net = require('net');
const crypto = require('crypto');

const port = Number(process.env.MM_COOP_PORT || 27153);
const session = crypto.randomUUID();
const clients = new Set();
let revision = 0;
let hostClient = null;

function send(socket, message) {
  socket.write(`${JSON.stringify(message)}\n`);
}

function broadcast(message, except) {
  for (const client of clients) {
    if (client !== except && !client.destroyed) send(client, message);
  }
}

const server = net.createServer((socket) => {
  socket.setEncoding('utf8');
  socket.name = 'unknown';
  socket.buffer = '';
  clients.add(socket);

  socket.on('data', (chunk) => {
    socket.buffer += chunk;
    let newline;
    while ((newline = socket.buffer.indexOf('\n')) >= 0) {
      const line = socket.buffer.slice(0, newline).trim();
      socket.buffer = socket.buffer.slice(newline + 1);
      if (!line) continue;
      let message;
      try { message = JSON.parse(line); }
      catch { send(socket, { type: 'error', message: 'Invalid JSON' }); continue; }

      if (message.type === 'hello') {
        if (message.protocol !== 0) {
          send(socket, { type: 'error', message: 'Protocol mismatch' });
          socket.destroy();
          return;
        }
        socket.name = String(message.name || 'Player');
        const isHost = hostClient === null;
        if (isHost) hostClient = socket;
        send(socket, { type: 'welcome', protocol: 0, session,
          host: hostClient.name, role: isHost ? 'host' : 'client' });
        broadcast({ type: 'peer_joined', name: socket.name }, socket);
        continue;
      }

      if (message.type === 'action') {
        revision += 1;
        if (message.kind === 'manual_save' && socket !== hostClient) {
          send(socket, { type: 'error', message: 'Only host may save', revision });
          continue;
        }
        const action = { type: 'action', revision, from: socket.name,
          id: message.id, kind: message.kind, payload: message.payload || {},
          value: message.value };
        send(socket, { type: 'action_ack', revision, id: message.id, kind: message.kind });
        broadcast(action, socket);
        continue;
      }

      send(socket, { type: 'error', message: 'Unsupported message type' });
    }
  });

  socket.on('close', () => {
    clients.delete(socket);
    if (socket === hostClient) {
      hostClient = clients.values().next().value || null;
      if (hostClient) broadcast({ type: 'host_changed', host: hostClient.name });
    }
    broadcast({ type: 'peer_left', name: socket.name });
  });
});

server.listen(port, '0.0.0.0', () => {
  console.log(`Motorsport Manager Coop LAN host listening on port ${port}`);
});
