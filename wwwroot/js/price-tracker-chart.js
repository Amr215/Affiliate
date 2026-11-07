(function () {
    var dataEl = document.getElementById("price-chart-data");
    var canvas = document.getElementById("price-chart");
    if (!dataEl || !canvas) return;

    var wrap = canvas.parentElement;
    if (typeof Chart === "undefined") {
        if (wrap) {
            wrap.innerHTML = "<p class=\"tracker-chart-empty\">"
                + (wrap.getAttribute("data-error-load") || "Could not load the chart.")
                + "</p>";
        }
        return;
    }

    var data;
    try {
        data = JSON.parse(dataEl.textContent);
    } catch (e) {
        if (wrap) {
            wrap.innerHTML = "<p class=\"tracker-chart-empty\">"
                + (wrap.getAttribute("data-error-load") || "Could not load the chart.")
                + "</p>";
        }
        return;
    }

    var labels = data.labels || [];
    var prices = data.prices || [];
    var currency = data.currency || "EGP";
    var seriesLabel = data.seriesLabel || "Price";

    var soft = getComputedStyle(document.documentElement).getPropertyValue("--ink-soft").trim() || "#5d5850";
    var accent = getComputedStyle(document.documentElement).getPropertyValue("--accent").trim() || "#e4572e";
    var line = getComputedStyle(document.documentElement).getPropertyValue("--line").trim() || "rgba(127,127,127,0.2)";

    new Chart(canvas, {
        type: "line",
        data: {
            labels: labels,
            datasets: [{
                label: seriesLabel,
                data: prices,
                borderColor: accent,
                backgroundColor: "rgba(228, 87, 46, 0.14)",
                borderWidth: 2,
                fill: true,
                tension: 0.25,
                pointRadius: labels.length > 40 ? 0 : 3,
                pointHoverRadius: 5
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: "index", intersect: false },
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        label: function (ctx) {
                            return ctx.parsed.y.toLocaleString(undefined, { maximumFractionDigits: 2 }) + " " + currency;
                        }
                    }
                }
            },
            scales: {
                x: {
                    ticks: { color: soft, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 },
                    grid: { color: line }
                },
                y: {
                    ticks: { color: soft },
                    grid: { color: line },
                    beginAtZero: false
                }
            }
        }
    });
})();
