const ws = new WebSocket(process.argv[2]);
const url = process.argv[3];
ws.addEventListener("open", () => {
  ws.send(JSON.stringify({ id: 1, method: "Page.navigate", params: { url } }));
});
ws.addEventListener("message", (event) => {
  const msg = JSON.parse(String(event.data));
  if (msg.id === 1) {
    process.stdout.write(JSON.stringify(msg.result || msg.error || {}));
    process.exit(0);
  }
});
setTimeout(() => process.exit(1), 8000);
