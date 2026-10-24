const fs = require("fs");
const ws = new WebSocket(process.argv[2]);
const expr = process.argv[3].endsWith(".js") ? fs.readFileSync(process.argv[3], "utf8") : process.argv[3];
ws.addEventListener('open', () => {
  ws.send(JSON.stringify({
    id: 1,
    method: 'Runtime.evaluate',
    params: { expression: expr, returnByValue: true, awaitPromise: true }
  }));
});
ws.addEventListener('message', (event) => {
  process.stdout.write(String(event.data));
  process.exit(0);
});
setTimeout(() => process.exit(1), 4000);
