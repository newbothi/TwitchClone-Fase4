// Fase 4 (c): gráficos del Dashboard con Chart.js (wwwroot/lib/chart.js).
// Los datos vienen de /Channel/Analytics?days=N, que lee analytics.* (lo llena python/analytics).
//
// Reglas de diseño que sigue este archivo:
//  • Un gráfico = una serie y un solo eje Y (nada de dos escalas en el mismo gráfico).
//  • Un solo color de datos (el morado de la app); en "mejores horas" se resaltan las 3
//    mejores y el resto queda en gris.
//  • El valor exacto siempre está en el tooltip y en la tabla (no solo en el color).
//  • Los textos de los datos se insertan con textContent, nunca con innerHTML.
(function () {
    "use strict";

    const root = document.getElementById("analytics");
    if (!root || typeof Chart === "undefined") return;

    const COLORS = {
        accent: "#9147ff",
        accentWash: "rgba(145, 71, 255, 0.10)",
        muted: "#5c5c66",
        text: "#efeff1",
        textMuted: "#adadb8",
        grid: "#2a2a2e",
        surface: "#0e0e10"
    };

    const number = new Intl.NumberFormat("es-CL", { maximumFractionDigits: 1 });
    const twoDecimals = new Intl.NumberFormat("es-CL", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const compact = new Intl.NumberFormat("es-CL", { notation: "compact", maximumFractionDigits: 1 });
    const dayLabel = new Intl.DateTimeFormat("es-CL", { day: "numeric", month: "short" });
    const longDay = new Intl.DateTimeFormat("es-CL", { weekday: "short", day: "numeric", month: "short" });
    const timeLabel = new Intl.DateTimeFormat("es-CL", { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
    const dateTime = new Intl.DateTimeFormat("es-CL", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", hourCycle: "h23" });

    // "2026-10-16" → fecha LOCAL (new Date("2026-10-16") la tomaría como UTC y mostraría el 15).
    const parseDay = s => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d); };

    const status = document.getElementById("analytics-status");
    const body = document.getElementById("analytics-body");
    const charts = {};
    let currentDays = 30;

    Chart.defaults.color = COLORS.textMuted;
    Chart.defaults.font.family = 'system-ui, -apple-system, "Segoe UI", sans-serif';
    Chart.defaults.font.size = 11;

    // Línea vertical que sigue al puntero en los gráficos de líneas (encuentra el día, no la línea).
    const crosshair = {
        id: "crosshair",
        afterDatasetsDraw(chart) {
            const active = chart.tooltip?.getActiveElements?.() ?? [];
            if (!active.length || chart.config.type !== "line") return;
            const x = active[0].element.x;
            const { top, bottom } = chart.chartArea;
            const ctx = chart.ctx;
            ctx.save();
            ctx.strokeStyle = COLORS.textMuted;
            ctx.globalAlpha = 0.5;
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.moveTo(x, top);
            ctx.lineTo(x, bottom);
            ctx.stroke();
            ctx.restore();
        }
    };

    function baseOptions(valueLabel, formatTitle) {
        return {
            responsive: true,
            maintainAspectRatio: false,
            animation: { duration: 250 },
            interaction: { mode: "index", intersect: false },
            plugins: {
                legend: { display: false },   // una sola serie: el título del gráfico ya dice qué es
                tooltip: {
                    backgroundColor: COLORS.surface,
                    borderColor: COLORS.grid,
                    borderWidth: 1,
                    titleColor: COLORS.textMuted,
                    titleFont: { weight: "normal" },
                    bodyColor: COLORS.text,
                    bodyFont: { weight: "bold", size: 13 },
                    displayColors: false,
                    padding: 10,
                    callbacks: {
                        title: items => formatTitle(items[0].dataIndex),
                        label: item => `${number.format(item.parsed.y)} ${valueLabel}`
                    }
                }
            },
            scales: {
                x: {
                    grid: { display: false },
                    border: { color: COLORS.grid },
                    ticks: { maxRotation: 0, autoSkip: true, autoSkipPadding: 12 }
                },
                y: {
                    beginAtZero: true,
                    grid: { color: COLORS.grid, lineWidth: 1 },
                    border: { display: false },
                    ticks: { precision: 0, maxTicksLimit: 5, callback: v => compact.format(v) }
                }
            }
        };
    }

    function lineDataset(values, fill) {
        return {
            data: values,
            borderColor: COLORS.accent,
            backgroundColor: fill ? COLORS.accentWash : COLORS.accent,
            fill: fill ? "origin" : false,
            borderWidth: 2,
            borderJoinStyle: "round",
            borderCapStyle: "round",
            tension: 0.25,
            pointRadius: 0,
            pointHoverRadius: 5,
            pointHoverBorderWidth: 2,
            pointHoverBorderColor: COLORS.surface,
            pointHoverBackgroundColor: COLORS.accent
        };
    }

    function barDataset(values, colors) {
        return {
            data: values,
            backgroundColor: colors ?? COLORS.accent,
            hoverBackgroundColor: colors ? colors.map(c => (c === COLORS.accent ? "#a970ff" : "#73737d")) : "#a970ff",
            maxBarThickness: 24,
            borderRadius: 4,
            borderSkipped: "start"   // redondeado solo en la punta, recto en la base
        };
    }

    function upsertChart(id, type, labels, dataset, options) {
        if (charts[id]) {
            charts[id].data.labels = labels;
            charts[id].data.datasets = [dataset];
            charts[id].options = options;
            charts[id].update();
            return;
        }
        const canvas = document.getElementById(id);
        charts[id] = new Chart(canvas, { type, data: { labels, datasets: [dataset] }, options, plugins: [crosshair] });
    }

    // ── KPIs ────────────────────────────────────────────────────────────

    function kpi(label, value, delta) {
        const box = document.createElement("div");
        box.className = "kpi";
        const l = document.createElement("span");
        l.className = "kpi-label";
        l.textContent = label;
        const v = document.createElement("span");
        v.className = "kpi-value";
        v.textContent = value;
        box.append(l, v);
        if (delta) {
            const d = document.createElement("span");
            d.className = "kpi-delta" + (delta.direction ? " " + delta.direction : "");
            d.textContent = delta.text;
            box.append(d);
        }
        return box;
    }

    // Flecha + texto: el color nunca es la única pista.
    function growth(pct, days) {
        const period = `vs. los ${days} días anteriores`;
        if (pct === null || pct === undefined) return { text: `sin datos para comparar`, direction: "" };
        if (pct === 0) return { text: `= igual ${period}`, direction: "" };
        const up = pct > 0;
        return { text: `${up ? "▲ +" : "▼ "}${number.format(pct)} % ${period}`, direction: up ? "up" : "down" };
    }

    function renderKpis(data) {
        const k = data.kpis;
        const days = data.days;
        const row = document.getElementById("analytics-kpis");
        row.replaceChildren(
            kpi("Seguidores", number.format(k.totalFollowers), { text: `+${number.format(k.newFollowers)} en ${days} días`, direction: "" }),
            kpi("Nuevos seguidores", number.format(k.newFollowers), growth(k.followersGrowthPct, days)),
            kpi("Promedio de espectadores", number.format(k.avgViewers), growth(k.avgViewersGrowthPct, days)),
            kpi("Pico de espectadores", number.format(k.peakViewers)),
            kpi("Horas en vivo", number.format(k.hoursLive), { text: `${number.format(k.hoursWatched)} horas vistas en total`, direction: "" }),
            kpi("Mensajes en el chat", compact.format(k.chatMessages), { text: `${number.format(k.messagesPerHourLive)} por hora en vivo`, direction: "" })
        );
    }

    // ── Gráficos ────────────────────────────────────────────────────────

    function renderCharts(data) {
        const days = data.daily.map(d => parseDay(d.day));
        const dayLabels = days.map(d => dayLabel.format(d));
        const dayTitle = i => longDay.format(days[i]);

        const viewerTimes = data.viewers.map(p => new Date(p.timeUtc.endsWith("Z") ? p.timeUtc : p.timeUtc + "Z"));
        upsertChart("chart-viewers", "line",
            viewerTimes.map(t => timeLabel.format(t)),
            lineDataset(data.viewers.map(p => p.avgViewers), true),
            baseOptions("espectadores", i => dateTime.format(viewerTimes[i])));

        // El total de seguidores no parte de cero: el eje se ajusta para que se vea la pendiente.
        const followerOptions = baseOptions("seguidores", dayTitle);
        followerOptions.scales.y.beginAtZero = false;
        upsertChart("chart-followers", "line", dayLabels,
            lineDataset(data.daily.map(d => d.totalFollowers), false),
            followerOptions);

        upsertChart("chart-new-followers", "bar", dayLabels,
            barDataset(data.daily.map(d => d.newFollowers)),
            baseOptions("nuevos seguidores", dayTitle));

        upsertChart("chart-avg-viewers", "bar", dayLabels,
            barDataset(data.daily.map(d => d.minutesLive > 0 ? d.avgViewers : null)),
            baseOptions("espectadores en promedio", dayTitle));

        upsertChart("chart-chat", "bar", dayLabels,
            barDataset(data.daily.map(d => d.chatMessages)),
            baseOptions("mensajes", dayTitle));

        // Mejores horas: las 3 con más espectadores en morado, el resto en gris (énfasis).
        const ranked = data.hours.filter(h => h.avgViewers > 0).sort((a, b) => b.avgViewers - a.avgViewers).slice(0, 3).map(h => h.hour);
        upsertChart("chart-hours", "bar",
            data.hours.map(h => String(h.hour).padStart(2, "0")),
            barDataset(data.hours.map(h => h.avgViewers), data.hours.map(h => (ranked.includes(h.hour) ? COLORS.accent : COLORS.muted))),
            baseOptions("espectadores en promedio", i => `${String(data.hours[i].hour).padStart(2, "0")}:00 – ${String(data.hours[i].hour).padStart(2, "0")}:59 · ${number.format(data.hours[i].chatMessages)} mensajes`));

        const trend = document.getElementById("trend-text");
        if (data.trend) {
            const t = data.trend;
            const sign = t.followersPerDay > 0 ? "+" : "";
            trend.textContent = `Tendencia (${t.windowDays} días): ${sign}${number.format(t.followersPerDay)} por día · ` +
                `R² ${twoDecimals.format(t.followersR2)} · en 7 días ≈ ${number.format(t.projectedFollowers7d)}`;
        } else {
            trend.textContent = "total al final de cada día";
        }
    }

    function renderTable(data) {
        const tbody = document.getElementById("analytics-rows");
        const rows = [...data.daily].reverse().map(d => {
            const tr = document.createElement("tr");
            [longDay.format(parseDay(d.day)), number.format(d.totalFollowers), number.format(d.newFollowers), number.format(d.minutesLive),
             d.minutesLive > 0 ? number.format(d.avgViewers) : "—", number.format(d.peakViewers), number.format(d.chatMessages), number.format(d.uniqueChatters)]
                .forEach(value => {
                    const td = document.createElement("td");
                    td.textContent = value;
                    tr.append(td);
                });
            return tr;
        });
        tbody.replaceChildren(...rows);
    }

    function setStatus(text, warn) {
        status.textContent = text;
        status.classList.toggle("warn", !!warn);
    }

    // ── Carga ───────────────────────────────────────────────────────────

    async function load(days) {
        currentDays = days;
        root.querySelectorAll(".period-filter button").forEach(b => b.setAttribute("aria-pressed", String(Number(b.dataset.days) === days)));
        body.classList.add("loading");   // se mantiene el gráfico anterior, atenuado, mientras carga

        try {
            const response = await fetch(`${root.dataset.url}?days=${days}`, { credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const data = await response.json();
            if (days !== currentDays) return; // llegó tarde: el usuario ya eligió otro período

            if (!data.available) {
                body.hidden = true;
                setStatus(data.message || "Las estadísticas no están disponibles.", true);
                return;
            }

            body.hidden = false;
            renderKpis(data);
            renderCharts(data);
            renderTable(data);

            const updated = data.lastUpdatedUtc ? `Actualizado: ${dateTime.format(new Date(data.lastUpdatedUtc.endsWith("Z") ? data.lastUpdatedUtc : data.lastUpdatedUtc + "Z"))}` : "";
            const lastError = data.lastError ? ` Último error del ETL: ${data.lastError}` : "";
            setStatus(data.message ? `${data.message} ${updated}.${lastError}`.trim() : updated, !!data.message);
        } catch (error) {
            setStatus("No se pudieron cargar las estadísticas. Intenta de nuevo en un momento.", true);
            console.warn("Estadísticas:", error);
        } finally {
            body.classList.remove("loading");
        }
    }

    root.querySelectorAll(".period-filter button").forEach(button =>
        button.addEventListener("click", () => load(Number(button.dataset.days))));

    load(currentDays);
})();
