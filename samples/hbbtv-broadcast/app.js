document.addEventListener("keydown", function (event) {
  if (event.keyCode !== 13) {
    return;
  }
  var broadcast = document.getElementById("broadcast");
  if (broadcast && typeof broadcast.bindToCurrentChannel === "function") {
    broadcast.bindToCurrentChannel();
  }
});
