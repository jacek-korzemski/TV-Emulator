(function () {
  var x = Math.floor(window.innerWidth / 2);
  var y = Math.floor(window.innerHeight / 2);
  var el = document.elementFromPoint(x, y);
  var objects = document.getElementsByTagName("object");
  var o = null;
  var i;
  for (i = 0; i < objects.length; i++) {
    var type = String(objects[i].getAttribute("type") || "");
    if (type.toLowerCase() === "application/avplayer") o = objects[i];
  }
  function info(n) {
    if (!n) return "none";
    var s = getComputedStyle(n);
    var r = n.getBoundingClientRect();
    var id = n.id ? "#" + n.id : "";
    var cls = "";
    if (n.className && typeof n.className === "string") cls = "." + n.className.split(" ")[0];
    return n.tagName + id + cls + " " + Math.round(r.left) + "," + Math.round(r.top) + " " + Math.round(r.width) + "x" + Math.round(r.height) + " bg=" + s.backgroundColor + " op=" + s.opacity + " blend=" + s.mixBlendMode + " pos=" + s.position + " z=" + s.zIndex;
  }
  var chain = [];
  var n = el;
  var k = 0;
  while (n && k < 8) {
    chain.push(info(n));
    n = n.parentElement;
    k++;
  }
  var body = getComputedStyle(document.body);
  var html = getComputedStyle(document.documentElement);
  return "html=" + html.backgroundColor + " body=" + body.backgroundColor + " OBJ " + info(o) + " HIT " + chain.join(" || ");
})()
