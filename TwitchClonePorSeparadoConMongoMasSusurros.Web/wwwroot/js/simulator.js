// Panel del simulador (Views/Simulator/Index.cshtml).
// Llama a SimulatorController con fetch y refresca el estado cada 2 s.
(function () {
    "use strict";

    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    const toast = document.getElementById("sim-toast");
    const STREAM_STATES = {
        "iniciando": ["wait", "Iniciando"],
        "transmitiendo": ["run", "Transmitiendo"],
        "reconectando": ["wait", "Reconectando"],
        "error": ["err", "Error"],
        "detenido": ["off", "Detenido"]
    };

    const REJECTIONS = {
        slow_mode: "modo lento", rate_limited: "límite de mensajes", followers_only: "solo seguidores",
        emote_only: "solo emotes", links_blocked: "enlace bloqueado", blocked_term: "palabra bloqueada",
        duplicate: "mensaje repetido", timed_out: "silenciado", banned: "baneado", error: "error interno"
    };

    function rejectionLabel(text) {
        const match = /^(\w+)\s*\((\d+)\)$/.exec(text || "");
        return match ? (REJECTIONS[match[1]] || match[1]) + " (" + match[2] + ")" : text;
    }

    function setText(el, text) { if (el && el.textContent !== text) el.textContent = text; }

    function setPill(el, kind, text) {
        if (!el) return;
        el.className = "sim-pill " + kind;
        setText(el, text);
    }

    function showToast(ok, message) {
        toast.className = "sim-toast " + (ok ? "sim-ok" : "sim-bad");
        toast.textContent = message || "";
    }

    function since(iso) {
        if (!iso) return "";
        const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
        if (seconds < 60) return seconds + " s";
        const minutes = Math.floor(seconds / 60);
        if (minutes < 60) return minutes + " min";
        return Math.floor(minutes / 60) + " h " + (minutes % 60) + " min";
    }

    async function post(action, data, button) {
        const body = new URLSearchParams(data || {});
        if (tokenInput) body.append("__RequestVerificationToken", tokenInput.value);
        if (button) button.disabled = true;
        try {
            const response = await fetch("/Simulator/" + action, {
                method: "POST",
                headers: { "Content-Type": "application/x-www-form-urlencoded" },
                body: body
            });
            if (!response.ok) throw new Error("HTTP " + response.status);
            const result = await response.json();
            showToast(result.ok, result.message);
        } catch (err) {
            showToast(false, "No se pudo completar la acción (" + err.message + ").");
        } finally {
            if (button) button.disabled = false;
            refresh();
        }
    }

    // ── Botones globales ─────────────────────────────────────────────
    document.getElementById("btn-create-bots").addEventListener("click", e =>
        post("CreateBots", { count: document.getElementById("bots-count").value }, e.currentTarget));

    document.getElementById("btn-start-full").addEventListener("click", e =>
        post("StartFull", {
            channels: document.getElementById("full-channels").value,
            viewers: document.getElementById("full-viewers").value,
            activityPercent: document.getElementById("full-activity").value,
            withStreams: document.getElementById("full-video").checked
        }, e.currentTarget));

    document.getElementById("btn-stop-all").addEventListener("click", e => {
        if (confirm("¿Detener todos los streams y chats simulados?")) post("StopAll", {}, e.currentTarget);
    });

    // ── Botones por canal ────────────────────────────────────────────
    document.querySelectorAll("tr[data-channel]").forEach(row => {
        const channelId = row.dataset.channel;
        row.querySelectorAll("[data-action]").forEach(button => {
            button.addEventListener("click", () => {
                switch (button.dataset.action) {
                    case "start-stream": return post("StartStream", { channelId }, button);
                    case "stop-stream": return post("StopStream", { channelId }, button);
                    case "stop-chat": return post("StopChat", { channelId }, button);
                    case "start-chat": return post("StartChat", {
                        channelId,
                        viewers: row.querySelector('[data-input="viewers"]').value,
                        activityPercent: row.querySelector('[data-input="activity"]').value
                    }, button);
                }
            });
        });
    });

    // ── Estado ───────────────────────────────────────────────────────
    function render(data) {
        const status = data.status;

        setText(document.getElementById("sim-bots"), status.botAccounts + (status.botsInUse ? " (" + status.botsInUse + " en uso)" : ""));

        const ffmpeg = status.ffmpeg;
        const ffmpegEl = document.getElementById("sim-ffmpeg");
        ffmpegEl.className = "value " + (ffmpeg.available ? "sim-ok" : "sim-bad");
        setText(ffmpegEl, ffmpeg.available ? "Disponible" : "No encontrado");
        setText(document.getElementById("sim-ffmpeg-hint"), ffmpeg.available
            ? (ffmpeg.version || "") + (ffmpeg.hasDrawText ? "" : " · sin drawtext (el video no lleva texto)")
            : (ffmpeg.error || "Instálalo con: winget install Gyan.FFmpeg"));

        const job = status.job;
        const jobEl = document.getElementById("sim-job");
        if (!job) {
            setText(jobEl, "Ninguna");
            setText(document.getElementById("sim-job-hint"), "");
        } else {
            jobEl.className = "value " + (job.running ? "sim-warn" : (job.error ? "sim-bad" : "sim-ok"));
            setText(jobEl, job.name);
            setText(document.getElementById("sim-job-hint"), (job.running ? "⏳ " : (job.error ? "❌ " : "✅ ")) + job.message);
        }

        const chats = new Map(status.chats.map(c => [c.channelId, c]));
        const streams = new Map(status.streams.map(s => [s.channelId, s]));
        const channels = new Map(data.channels.map(c => [c.id, c]));

        document.querySelectorAll("tr[data-channel]").forEach(row => {
            const id = Number(row.dataset.channel);
            const field = name => row.querySelector('[data-field="' + name + '"]');

            const channel = channels.get(id);
            if (channel) {
                setPill(field("live"), channel.isLive ? "live" : "off", channel.isLive ? "EN VIVO" : "Offline");
                setText(field("viewers"), String(channel.viewerCount));
            }

            const stream = streams.get(id);
            if (!stream) {
                setPill(field("stream-state"), "off", "Apagado");
                setText(field("stream-info"), "");
                setText(field("stream-error"), "");
            } else {
                const [kind, label] = STREAM_STATES[stream.state] || ["off", stream.state];
                setPill(field("stream-state"), kind, label);
                const parts = [stream.source];
                if (stream.since && stream.state === "transmitiendo") parts.push("hace " + since(stream.since));
                if (stream.restarts) parts.push(stream.restarts + " reinicio" + (stream.restarts === 1 ? "" : "s"));
                setText(field("stream-info"), parts.filter(Boolean).join(" · "));
                setText(field("stream-error"), stream.state === "transmitiendo" ? "" : (stream.lastError || ""));
            }

            const chat = chats.get(id);
            const info = field("chat-info");
            if (!chat || !chat.running) {
                setPill(field("chat-state"), "off", chat ? "Detenido" : "Apagado");
                info.textContent = "";
                setText(field("chat-error"), chat && chat.lastError ? chat.lastError : "");
            } else {
                setPill(field("chat-state"), chat.notEnoughBots ? "wait" : "run",
                    chat.presentBots + " / " + chat.targetViewers + " bots");
                info.replaceChildren();
                const line = (label, value) => {
                    const strong = document.createElement("strong");
                    strong.textContent = value;
                    info.append(label, strong, " ");
                };
                line("Mensajes/min: ", String(chat.messagesPerMinute));
                line("· enviados: ", String(chat.sent));
                line("· rechazados: ", String(chat.rejected));
                info.append(document.createElement("br"));
                line("Follows: ", String(chat.follows));
                line("· donaciones: ", String(chat.donations));
                if (chat.heldByAutoMod) line("· AutoMod: ", String(chat.heldByAutoMod));
                if (chat.aiEnabled) line("· IA en cola: ", String(chat.aiQueue));
                if (chat.wave) { info.append(document.createElement("br")); line("🌊 Ola: ", chat.wave); }

                const notes = [];
                if (chat.notEnoughBots) notes.push("No hay bots libres suficientes: crea más cuentas.");
                if (chat.topRejection) notes.push("Rechazo más común: " + rejectionLabel(chat.topRejection));
                if (chat.lastError) notes.push(chat.lastError);
                setText(field("chat-error"), notes.join(" · "));
            }
        });
    }

    let refreshing = false;
    async function refresh() {
        if (refreshing) return;
        refreshing = true;
        try {
            const response = await fetch("/Simulator/Status", { headers: { "Accept": "application/json" } });
            if (response.ok) render(await response.json());
        } catch (err) {
            /* el próximo intento lo arregla */
        } finally {
            refreshing = false;
        }
    }

    refresh();
    setInterval(refresh, 2000);
})();
