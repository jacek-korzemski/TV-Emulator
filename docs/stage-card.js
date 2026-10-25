(function () {
  var card = document.querySelector(".stage-card");
  if (!card) return;
  if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

  var glare = card.querySelector(".stage-glare");

  card.addEventListener("pointermove", function (event) {
    var rect = card.getBoundingClientRect();
    var x = (event.clientX - rect.left) / rect.width;
    var y = (event.clientY - rect.top) / rect.height;
    var tiltY = (x - 0.5) * 12;
    var tiltX = (0.5 - y) * 9;
    card.classList.add("is-tilting");
    card.style.setProperty("--rx", tiltX.toFixed(2) + "deg");
    card.style.setProperty("--ry", tiltY.toFixed(2) + "deg");
    if (glare) {
      glare.style.opacity = "1";
      glare.style.background =
        "radial-gradient(420px circle at " +
        (x * 100).toFixed(1) + "% " + (y * 100).toFixed(1) +
        "%, rgba(255,255,255,0.72), transparent 46%)";
    }
  });

  card.addEventListener("pointerleave", function () {
    card.classList.remove("is-tilting");
    card.style.setProperty("--rx", "0deg");
    card.style.setProperty("--ry", "0deg");
    if (glare) glare.style.opacity = "0";
  });
})();
