const net = require('net');

const host = process.argv[2] || '127.0.0.1';
const port = Number(process.argv[3] || 27153);
const name = process.argv[4] || 'Player 2';
const socket = net.createConnection({ host, port });
socket.setEncoding('utf8');
let buffer = '';

function send(message) { socket.write(`${JSON.stringify(message)}\n`); }

socket.on('connect', () => {
  console.log(`Connected to ${host}:${port}`);
  send({ type: 'hello', protocol: 0, name });
  setTimeout(() => send({ type: 'action', id: `test-${Date.now()}`,
    kind: 'prototype_ping', payload: { from: name } }), 250);
});

socket.on('data', (chunk) => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (line) console.log(line);
  }
});

socket.on('error', (error) => { console.error(error.message); process.exitCode = 1; });
