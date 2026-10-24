(function () {
  var x = Math.floor(window.innerWidth / 2);
  var y = Math.floor(window.innerHeight / 2);
  var list = document.elementsFromPoint(x, y);
  var parts = [];
  var i;
  for (i = 0; i < list.length && i < 12; i++) {
    var n = list[i];
    var s = getComputedStyle(n);
    var before = getComputedStyle(n, "::before");
    var id = n.id ? "#" + n.id : "";
    var cls = "";
    if (n.className && typeof n.className === "string") cls = "." + n.className.split(" ")[0];
    parts.push(n.tagName + id + cls + " bg=" + s.backgroundColor + " img=" + (s.backgroundImage === "none" ? "0" : "1") + " op=" + s.opacity + " before=" + before.backgroundColor);
  }
  return parts.join(" || ");
})()
