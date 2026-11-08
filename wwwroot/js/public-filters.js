(function () {
    function updateOverflow(wrap, rail) {
        if (!wrap || !rail) return;
        var max = rail.scrollWidth - rail.clientWidth;
        if (max <= 1) {
            wrap.classList.remove("is-overflow-start", "is-overflow-end");
            return;
        }
        var start = rail.scrollLeft;
        // RTL browsers may report negative or inverted scrollLeft.
        var atStart = Math.abs(start) <= 1;
        var atEnd = Math.abs(start) >= max - 1;
        wrap.classList.toggle("is-overflow-start", !atStart);
        wrap.classList.toggle("is-overflow-end", !atEnd);
    }

    function bindRail(wrap) {
        var rail = wrap.querySelector(".filter-rail");
        if (!rail) return;

        var active = rail.querySelector(".filter-chip.is-active");
        if (active && typeof active.scrollIntoView === "function") {
            try {
                active.scrollIntoView({ inline: "center", block: "nearest", behavior: "smooth" });
            } catch (e) {
                active.scrollIntoView(false);
            }
        }

        var sync = function () { updateOverflow(wrap, rail); };
        rail.addEventListener("scroll", sync, { passive: true });
        window.addEventListener("resize", sync, { passive: true });
        sync();
        requestAnimationFrame(sync);
    }

    function init() {
        document.querySelectorAll(".filter-rail-wrap").forEach(bindRail);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
