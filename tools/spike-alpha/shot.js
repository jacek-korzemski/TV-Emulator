const fs = require("fs");
const ws = new WebSocket(process.argv[2]);
const out = process.argv[3];
ws.addEventListener("open", () => {
  ws.send(JSON.stringify({
    id: 1,
    method: "Page.captureScreenshot",
    params: { format: "png" }
  }));
});
ws.addEventListener("message", (event) => {
  const msg = JSON.parse(String(event.data));
  if (!msg.result || !msg.result.data) return;
  fs.writeFileSync(out, Buffer.from(msg.result.data, "base64"));
  process.exit(0);
});
setTimeout(() => process.exit(1), 8000);
