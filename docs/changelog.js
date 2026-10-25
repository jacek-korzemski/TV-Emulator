(function () {
  var list = document.getElementById("changelog-list");
  if (!list) return;

  fetch("changelog.json")
    .then(function (response) {
      if (!response.ok) throw new Error("HTTP " + response.status);
      return response.json();
    })
    .then(function (data) {
      var entries = (data && data.entries) || [];
      if (!entries.length) {
        list.innerHTML = "<li>No entries yet.</li>";
        return;
      }
      list.innerHTML = entries
        .map(function (entry) {
          var items = (entry.items || [])
            .map(function (item) {
              return "<li>" + escapeHtml(item) + "</li>";
            })
            .join("");
          return (
            "<li class=\"entry\">" +
            "<time datetime=\"" + escapeHtml(entry.date || "") + "\">" + escapeHtml(entry.date || "") + "</time>" +
            "<h3>" + escapeHtml(entry.title || "") + "</h3>" +
            "<ul>" + items + "</ul>" +
            "</li>"
          );
        })
        .join("");
    })
    .catch(function () {
      list.innerHTML = "<li>Changelog could not be loaded.</li>";
    });

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }
})();
