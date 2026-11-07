(function () {
    var root = document.documentElement;
    var meta = document.querySelector('meta[name="theme-color"]');
    var btn = document.getElementById("theme-toggle");

    function isNight() {
        return root.classList.contains("theme-night");
    }

    function applyMeta() {
        if (!meta) return;
        var styles = getComputedStyle(root);
        var color = styles.getPropertyValue("--theme-meta").trim() || (isNight() ? "#12161c" : "#e8eef2");
        meta.setAttribute("content", color);
    }

    function syncButton() {
        if (!btn) return;
        var night = isNight();
        var label = night
            ? (btn.getAttribute("data-label-day") || "Day mode")
            : (btn.getAttribute("data-label-night") || "Night mode");
        btn.setAttribute("title", label);
        btn.setAttribute("aria-label", label);
        btn.setAttribute("aria-pressed", night ? "true" : "false");
    }

    function setNight(night) {
        root.classList.toggle("theme-night", night);
        try {
            localStorage.setItem("pub-theme", night ? "night" : "day");
        } catch (e) { }
        applyMeta();
        syncButton();
    }

    applyMeta();
    syncButton();

    if (btn) {
        btn.addEventListener("click", function () {
            setNight(!isNight());
        });
    }
})();
