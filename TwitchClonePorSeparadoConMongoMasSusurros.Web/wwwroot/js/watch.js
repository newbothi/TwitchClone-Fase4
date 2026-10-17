// Página Watch: reproductor HLS + chat en vivo (Fases 1 y 2).
//
// Regla de oro: NUNCA se usa innerHTML con datos de usuarios. Todo se arma con
// createElement/textContent a partir de los DTO que manda el servidor.
//
// Secciones: utilidades · reproductor · render del chat · fijado · encuestas · AutoMod ·
// tarjeta de usuario · raids · reglas · entrada de texto · SignalR · viewers.
(function () {
    "use strict";

    const configEl = document.getElementById("watch-config");
    if (!configEl) return;
    const cfg = JSON.parse(configEl.textContent);

    const MAX_MESSAGES = 150;
    const BADGES = {
        broadcaster: { icon: "🎙", title: "Dueño del canal" },
        moderator: { icon: "⚔️", title: "Moderador" },
        vip: { icon: "💎", title: "VIP" },
        admin: { icon: "🛡", title: "Admin de la plataforma" }
    };

    const $ = (id) => document.getElementById(id);
    const list = $("chat-messages");
    const input = $("chat-input");
    const sendBtn = $("chat-send");
    const newMessagesBtn = $("chat-new-messages");

    const state = {
        canModerate: !!cfg.canModerate,
        role: "viewer",
        settings: null,
        points: 0,
        replyTo: null,
        poll: null,
        myVote: null,
        held: new Map(),
        inputLockedUntil: 0,
        cooldownUntil: 0
    };
    let inputLockTimer = null;
    let cooldownTimer = null;

    // ═══════════════════════════════════════════════════════════════════
    //  Utilidades
    // ═══════════════════════════════════════════════════════════════════

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function button(label, className, onClick, title) {
        const b = el("button", className, label);
        b.type = "button";
        if (title) { b.title = title; b.setAttribute("aria-label", title); }
        b.addEventListener("click", onClick);
        return b;
    }

    function formatDuration(seconds) {
        if (seconds < 60) return seconds + " s";
        if (seconds < 3600) return Math.ceil(seconds / 60) + " min";
        if (seconds < 86400) return Math.round(seconds / 360) / 10 + " h";
        return Math.round(seconds / 8640) / 10 + " días";
    }

    function formatDate(iso) {
        try { return new Date(iso).toLocaleDateString("es-CL", { day: "numeric", month: "short", year: "numeric" }); }
        catch { return iso; }
    }

    function plainText(m) {
        return (m.fragments || []).map(f => f.text || "").join("");
    }

    function storageGet(key) { try { return localStorage.getItem(key); } catch { return null; } }
    function storageSet(key, value) { try { localStorage.setItem(key, value); } catch { /* modo privado */ } }

    // ═══════════════════════════════════════════════════════════════════
    //  Reproductor
    // ═══════════════════════════════════════════════════════════════════

    function setupPlayer() {
        const video = $("video-player");
        if (!video || !cfg.isLive) return;

        if (window.Hls && Hls.isSupported()) {
            const hls = new Hls({ lowLatencyMode: true, backBufferLength: 30 });
            hls.loadSource(cfg.hlsUrl);
            hls.attachMedia(video);
            hls.on(Hls.Events.ERROR, function (_event, data) {
                if (!data.fatal) return;
                if (data.type === Hls.ErrorTypes.NETWORK_ERROR) setTimeout(() => hls.startLoad(), 3000);
                else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
                else { hls.destroy(); setTimeout(setupPlayer, 5000); }
            });
        } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
            video.src = cfg.hlsUrl;
        }
    }
    setupPlayer();

    // ═══════════════════════════════════════════════════════════════════
    //  Render del chat (por lotes con requestAnimationFrame)
    // ═══════════════════════════════════════════════════════════════════

    // Con cientos de mensajes por segundo (simulador) insertar uno por uno congela la
    // página: se acumulan y se insertan juntos una vez por cuadro.
    let pending = [];
    let flushScheduled = false;

    function isNearBottom() {
        return list.scrollHeight - list.scrollTop - list.clientHeight < 80;
    }

    function scrollToBottom() {
        list.scrollTop = list.scrollHeight;
        if (newMessagesBtn) newMessagesBtn.hidden = true;
    }

    function append(node) {
        if (!list) return;
        pending.push(node);
        if (!flushScheduled) {
            flushScheduled = true;
            requestAnimationFrame(flush);
        }
    }

    function flush() {
        flushScheduled = false;
        if (pending.length === 0) return;
        const stick = isNearBottom();
        const fragment = document.createDocumentFragment();
        pending.slice(-MAX_MESSAGES).forEach(n => fragment.appendChild(n));
        pending = [];
        list.appendChild(fragment);
        while (list.children.length > MAX_MESSAGES) list.removeChild(list.firstChild);
        if (stick) scrollToBottom();
        else if (newMessagesBtn) newMessagesBtn.hidden = false;
    }

    function isMention(m) {
        if (!cfg.currentUserId || m.userId === cfg.currentUserId) return false;
        if (m.replyTo && m.replyTo.userId === cfg.currentUserId) return true;
        if (!cfg.currentDisplayName) return false;
        return plainText(m).toLowerCase().includes("@" + cfg.currentDisplayName.toLowerCase());
    }

    function renderMessage(m) {
        const row = el("div", "chat-message");
        row.dataset.id = m.id;
        row.dataset.user = m.userId;
        if (m.isAction) row.classList.add("chat-action");
        if (m.isAnnouncement) row.classList.add("chat-announcement");
        if (isMention(m)) row.classList.add("chat-mention");

        if (m.isAnnouncement) row.appendChild(el("div", "chat-announcement-label", "📢 Anuncio"));

        if (m.replyTo) {
            const reply = el("div", "chat-reply-ref");
            reply.textContent = "↪ Respondiendo a @" + m.replyTo.displayName + ": " + m.replyTo.text;
            row.appendChild(reply);
        }

        (m.badges || []).forEach(badge => {
            const info = BADGES[badge];
            if (!info) return;
            const span = el("span", "chat-badge chat-badge-" + badge, info.icon);
            span.title = info.title;
            row.appendChild(span);
        });

        const name = el("strong", "chat-username", m.displayName);
        name.style.color = m.color || "#a970ff";
        name.tabIndex = 0;
        name.title = "Ver tarjeta de " + m.displayName;
        name.addEventListener("click", () => openUserCard(m.userId));
        row.appendChild(name);
        row.appendChild(document.createTextNode(m.isAction ? " " : ": "));

        const content = el("span", "chat-content");
        if (m.isAction) content.style.color = m.color || "#a970ff";
        (m.fragments || []).forEach(f => {
            if (f.type === "emote" && f.emoteUrl) {
                const img = el("img", "chat-emote");
                img.src = f.emoteUrl;
                img.alt = f.text;
                img.title = f.text;
                content.appendChild(img);
            } else {
                content.appendChild(document.createTextNode(f.text || ""));
            }
        });
        row.appendChild(content);
        row.appendChild(buildActions(m));
        return row;
    }

    function buildActions(m) {
        const box = el("span", "chat-mod-actions");
        if (cfg.isAuthenticated) {
            box.appendChild(button("↩", "chat-mod-btn", () => startReply(m), "Responder"));
        }
        if (state.canModerate) {
            box.appendChild(button("📌", "chat-mod-btn", () => invoke("PinMessage", cfg.channelId, m.id), "Fijar mensaje"));
            if (m.userId !== cfg.currentUserId) {
                box.appendChild(button("🗑", "chat-mod-btn", () => invoke("DeleteMessage", cfg.channelId, m.id), "Borrar mensaje"));
                box.appendChild(button("⏱", "chat-mod-btn", () => invoke("TimeoutUser", cfg.channelId, m.userId, 600), "Silenciar 10 minutos"));
                box.appendChild(button("⛔", "chat-mod-btn", () => {
                    if (confirm("¿Banear a " + m.displayName + " de este chat?")) invoke("BanUser", cfg.channelId, m.userId, null);
                }, "Banear del chat"));
            }
        }
        return box;
    }

    function systemMessage(text, kind) {
        append(el("div", "chat-message chat-system" + (kind ? " chat-system-" + kind : ""), text));
    }

    function markDeleted(row) {
        row.classList.add("chat-deleted");
        const content = row.querySelector(".chat-content");
        if (content) content.textContent = "<mensaje eliminado>";
        const actions = row.querySelector(".chat-mod-actions");
        if (actions) actions.remove();
    }

    function rows(predicate) {
        return Array.prototype.filter.call(list.querySelectorAll(".chat-message[data-id]"), predicate);
    }

    function rebuildActions() {
        list.querySelectorAll(".chat-message[data-id]").forEach(row => {
            if (row.classList.contains("chat-deleted")) return;
            const old = row.querySelector(".chat-mod-actions");
            const name = row.querySelector(".chat-username");
            const m = { id: row.dataset.id, userId: row.dataset.user, displayName: name ? name.textContent : "" };
            const fresh = buildActions(m);
            if (old) old.replaceWith(fresh); else row.appendChild(fresh);
        });
    }

    function applySettings(settings, announce) {
        if (!settings) return;
        const previous = state.settings;
        state.settings = settings;
        const modes = [];
        if (settings.slowModeSeconds > 0) modes.push("Modo lento (" + settings.slowModeSeconds + " s)");
        if (settings.followersOnly) modes.push("Solo seguidores");
        if (settings.emoteOnly) modes.push("Solo emotes");
        const modesEl = $("chat-modes");
        if (modesEl) {
            modesEl.textContent = modes.join(" · ");
            modesEl.hidden = modes.length === 0;
        }
        // Solo se avisa si cambió algo visible (no cuando el dueño toca AutoMod o las palabras bloqueadas).
        const changed = !previous || previous.slowModeSeconds !== settings.slowModeSeconds
            || previous.followersOnly !== settings.followersOnly || previous.emoteOnly !== settings.emoteOnly;
        if (announce && changed) systemMessage(modes.length ? "Modos del chat: " + modes.join(", ") + "." : "El chat ya no tiene modos especiales.");
    }

    // Historial inicial
    if (cfg.chatUnavailable) systemMessage("No se pudo cargar el historial del chat. Los mensajes nuevos deberían funcionar con normalidad.");
    (cfg.history || []).forEach(m => list && list.appendChild(renderMessage(m)));
    if (list) {
        scrollToBottom();
        list.addEventListener("scroll", () => { if (isNearBottom() && newMessagesBtn) newMessagesBtn.hidden = true; });
    }
    if (newMessagesBtn) newMessagesBtn.addEventListener("click", scrollToBottom);

    // ═══════════════════════════════════════════════════════════════════
    //  Mensaje fijado
    // ═══════════════════════════════════════════════════════════════════

    function renderPinned(m) {
        const box = $("chat-pinned");
        if (!box) return;
        box.replaceChildren();
        if (!m) { box.hidden = true; return; }

        box.appendChild(el("span", "chat-pinned-icon", "📌"));
        const body = el("div", "chat-pinned-body");
        const name = el("strong", null, m.displayName);
        name.style.color = m.color || "#a970ff";
        body.appendChild(name);
        body.appendChild(document.createTextNode(": " + plainText(m)));
        box.appendChild(body);
        if (state.canModerate) box.appendChild(button("✕", "chat-pinned-close", () => invoke("UnpinMessage", cfg.channelId), "Desfijar"));
        box.hidden = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Encuestas
    // ═══════════════════════════════════════════════════════════════════

    let pollTimer = null;

    function renderPoll(poll) {
        const box = $("chat-poll");
        if (!box) return;
        if (!state.poll || !poll || state.poll.id !== poll.id) state.myVote = null;
        state.poll = poll;
        box.replaceChildren();
        clearInterval(pollTimer);
        if (!poll) { box.hidden = true; return; }

        const header = el("div", "poll-header");
        header.appendChild(el("span", "poll-title", "📊 " + poll.question));
        const timer = el("span", "poll-timer");
        header.appendChild(timer);
        box.appendChild(header);

        const total = poll.totalVotes || 0;
        const max = Math.max(0, ...poll.options.map(o => o.votes));
        poll.options.forEach((option, index) => {
            const pct = total > 0 ? Math.round(option.votes * 100 / total) : 0;
            const row = el("button", "poll-option");
            row.type = "button";
            row.disabled = poll.ended || !cfg.isAuthenticated;
            if (state.myVote === index) row.classList.add("poll-voted");
            if (poll.ended && option.votes === max && max > 0) row.classList.add("poll-winner");
            const bar = el("span", "poll-bar");
            bar.style.width = pct + "%";
            row.appendChild(bar);
            row.appendChild(el("span", "poll-option-text", option.text));
            row.appendChild(el("span", "poll-option-pct", pct + "% (" + option.votes + ")"));
            row.addEventListener("click", () => vote(index));
            box.appendChild(row);
        });

        const footer = el("div", "poll-footer", total + " voto" + (total === 1 ? "" : "s") + (poll.ended ? " · Terminada" : ""));
        if (state.canModerate && !poll.ended) footer.appendChild(button("Terminar", "poll-end", () => sendRaw("/endpoll")));
        if (poll.ended) footer.appendChild(button("Cerrar", "poll-end", () => { box.hidden = true; }));
        box.appendChild(footer);
        box.hidden = false;

        const tick = () => {
            const left = Math.max(0, Math.round((new Date(poll.endsAt) - Date.now()) / 1000));
            timer.textContent = poll.ended ? "" : left + " s";
        };
        tick();
        if (!poll.ended) pollTimer = setInterval(tick, 1000);
    }

    async function vote(index) {
        if (!state.poll || state.poll.ended) return;
        const result = await invoke("VotePoll", cfg.channelId, state.poll.id, index);
        if (result && result.ok) {
            state.myVote = index;
            renderPoll(state.poll);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  AutoMod (solo moderadores)
    // ═══════════════════════════════════════════════════════════════════

    function renderHeld() {
        const box = $("chat-automod");
        if (!box) return;
        box.replaceChildren();
        if (!state.canModerate || state.held.size === 0) { box.hidden = true; return; }

        box.appendChild(el("div", "automod-title", "🛡 AutoMod: " + state.held.size + " mensaje" + (state.held.size === 1 ? "" : "s") + " en revisión"));
        state.held.forEach(h => {
            const item = el("div", "automod-item");
            const text = el("div", "automod-text");
            text.appendChild(el("strong", null, h.displayName));
            text.appendChild(document.createTextNode(": " + h.text));
            item.appendChild(text);
            const meta = el("div", "automod-meta", (h.category || "") + (h.reason ? " · " + h.reason : ""));
            item.appendChild(meta);
            const actions = el("div", "automod-actions");
            actions.appendChild(button("Aprobar", "automod-approve", () => invoke("ResolveAutoMod", cfg.channelId, h.id, true)));
            actions.appendChild(button("Rechazar", "automod-deny", () => invoke("ResolveAutoMod", cfg.channelId, h.id, false)));
            item.appendChild(actions);
            box.appendChild(item);
        });
        box.hidden = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Tarjeta de usuario
    // ═══════════════════════════════════════════════════════════════════

    const cardModal = $("user-card-modal");

    async function openUserCard(userId) {
        if (!cardModal) return;
        const body = $("user-card-body");
        body.replaceChildren(el("p", "viewers-empty", "Cargando..."));
        cardModal.style.display = "flex";

        let card;
        try { card = await connection.invoke("GetUserCard", cfg.channelId, userId); }
        catch (err) { console.error(err); }
        body.replaceChildren();
        if (!card) { body.appendChild(el("p", "viewers-empty", "No se encontró al usuario.")); return; }

        const header = el("div", "card-header");
        const name = el("h3", "card-name", card.displayName);
        name.style.color = card.color;
        header.appendChild(name);
        (card.badges || []).forEach(b => { if (BADGES[b]) header.appendChild(el("span", "chat-badge", BADGES[b].icon)); });
        body.appendChild(header);

        const roleNames = { broadcaster: "Dueño del canal", moderator: "Moderador", vip: "VIP", viewer: "Espectador" };
        body.appendChild(el("p", "card-line", roleNames[card.role] || card.role));
        body.appendChild(el("p", "card-line", "Cuenta creada: " + formatDate(card.accountCreatedAt)));
        if (card.role !== "broadcaster") {
            body.appendChild(el("p", "card-line", card.followingSince ? "Sigue el canal desde: " + formatDate(card.followingSince) : "No sigue el canal"));
        }
        if (card.isBanned) body.appendChild(el("p", "card-line card-warning", "⛔ Baneado de este chat"));
        if (card.timedOutUntil) body.appendChild(el("p", "card-line card-warning", "⏱ Silenciado hasta " + new Date(card.timedOutUntil).toLocaleTimeString("es-CL")));

        const actions = el("div", "card-actions");
        if (cfg.isAuthenticated && card.userId !== cfg.currentUserId) {
            const whisper = el("a", "card-btn", "💬 Susurrar");
            whisper.href = "/Whisper/Conversation?otherUserId=" + encodeURIComponent(card.userId);
            actions.appendChild(whisper);
        }
        if (cfg.isAuthenticated && card.userId !== cfg.currentUserId) {
            actions.appendChild(button("@ Mencionar", "card-btn", () => {
                if (input) { input.value = (input.value ? input.value + " " : "") + "@" + card.displayName + " "; input.focus(); }
                closeCard();
            }));
        }
        if (card.canTimeout) {
            actions.appendChild(button("⏱ 10 min", "card-btn", () => cardAction("TimeoutUser", card.userId, 600)));
            actions.appendChild(button("⏱ 1 h", "card-btn", () => cardAction("TimeoutUser", card.userId, 3600)));
        }
        if (card.canBan) {
            actions.appendChild(button("⛔ Banear", "card-btn card-danger", () => {
                if (confirm("¿Banear a " + card.displayName + "?")) cardAction("BanUser", card.userId, null);
            }));
            if (card.isBanned || card.timedOutUntil) actions.appendChild(button("✅ Quitar sanción", "card-btn", () => cardAction("UnbanUser", card.userId)));
        }
        if (card.canManageRoles) {
            actions.appendChild(card.role === "moderator"
                ? button("Quitar mod", "card-btn", () => cardAction("SetUserRole", card.userId, "none"))
                : button("⚔️ Hacer mod", "card-btn", () => cardAction("SetUserRole", card.userId, "moderator")));
            actions.appendChild(card.role === "vip"
                ? button("Quitar VIP", "card-btn", () => cardAction("SetUserRole", card.userId, "none"))
                : button("💎 Hacer VIP", "card-btn", () => cardAction("SetUserRole", card.userId, "vip")));
        }
        body.appendChild(actions);
    }

    async function cardAction(method, ...args) {
        await invoke(method, cfg.channelId, ...args);
        closeCard();
    }

    function closeCard() { if (cardModal) cardModal.style.display = "none"; }
    $("user-card-close")?.addEventListener("click", closeCard);
    cardModal?.addEventListener("click", e => { if (e.target === cardModal) closeCard(); });

    // ═══════════════════════════════════════════════════════════════════
    //  Raids
    // ═══════════════════════════════════════════════════════════════════

    let raidTimer = null;

    function startRaidCountdown(targetChannelId, targetName, viewers, seconds) {
        const box = $("chat-raid");
        if (!box) return;
        let left = seconds;
        box.replaceChildren();
        clearInterval(raidTimer);

        // El streamer que lanzó el raid se queda en su página (tiene que cerrar el stream en OBS).
        if (state.role === "broadcaster") {
            box.appendChild(el("span", null, "🚀 Raid enviado a " + targetName + " con " + viewers + " espectadores. Tu audiencia se está mudando."));
            const go = el("a", "raid-cancel", "Ir al canal");
            go.href = "/Channel/Watch/" + targetChannelId;
            box.appendChild(go);
            box.hidden = false;
            return;
        }

        const text = el("span", null);
        box.appendChild(text);
        box.appendChild(button("Quedarme aquí", "raid-cancel", () => { clearInterval(raidTimer); box.hidden = true; }));
        box.hidden = false;
        const tick = () => {
            text.textContent = "🚀 Raid a " + targetName + " con " + viewers + " espectadores en " + left + " s…";
            if (left-- <= 0) {
                clearInterval(raidTimer);
                location.href = "/Channel/Watch/" + targetChannelId;
            }
        };
        tick();
        raidTimer = setInterval(tick, 1000);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Reglas del chat
    // ═══════════════════════════════════════════════════════════════════

    function rulesKey() {
        const rules = (state.settings && state.settings.rules) || "";
        let hash = 0;
        for (let i = 0; i < rules.length; i++) hash = (hash * 31 + rules.charCodeAt(i)) | 0;
        return "chat-rules-" + cfg.channelId + "-" + hash;
    }

    // El dueño y los moderadores no necesitan aceptar las reglas de su propio canal.
    function rulesPending() {
        return cfg.isAuthenticated && !state.canModerate && state.settings && state.settings.rules && !storageGet(rulesKey());
    }

    function showRules() {
        const modal = $("rules-modal");
        if (!modal) return;
        $("rules-text").textContent = state.settings.rules;
        modal.style.display = "flex";
    }

    $("rules-accept")?.addEventListener("click", () => {
        storageSet(rulesKey(), "1");
        $("rules-modal").style.display = "none";
        input?.focus();
    });

    // ═══════════════════════════════════════════════════════════════════
    //  Entrada de texto
    // ═══════════════════════════════════════════════════════════════════

    function setPoints(points) {
        state.points = points;
        const elPoints = $("chat-points");
        if (elPoints) {
            elPoints.textContent = "🪙 " + points.toLocaleString("es-CL");
            elPoints.hidden = !cfg.isAuthenticated;
        }
    }

    function startReply(m) {
        state.replyTo = { id: m.id, displayName: m.displayName };
        const bar = $("chat-reply");
        if (!bar) return;
        $("chat-reply-text").textContent = "Respondiendo a @" + m.displayName;
        bar.hidden = false;
        input?.focus();
    }

    function cancelReply() {
        state.replyTo = null;
        const bar = $("chat-reply");
        if (bar) bar.hidden = true;
    }
    $("chat-reply-cancel")?.addEventListener("click", cancelReply);

    function setInputEnabled(enabled, placeholder) {
        if (!input || !sendBtn) return;
        input.disabled = !enabled;
        sendBtn.disabled = !enabled;
        input.placeholder = placeholder || "Envía un mensaje o escribe /help";
    }

    function canSendNow() {
        const now = Date.now();
        return now >= state.inputLockedUntil && now >= state.cooldownUntil;
    }

    function lockInput(seconds, reason) {
        state.inputLockedUntil = Date.now() + seconds * 1000;
        clearInterval(inputLockTimer);
        const tick = () => {
            const left = Math.ceil((state.inputLockedUntil - Date.now()) / 1000);
            if (left <= 0) {
                clearInterval(inputLockTimer);
                setInputEnabled(true);
                systemMessage("Ya puedes volver a escribir.");
                return;
            }
            setInputEnabled(false, reason + " (" + formatDuration(left) + ")");
        };
        tick();
        inputLockTimer = setInterval(tick, 1000);
    }

    function startCooldown(seconds) {
        if (!sendBtn) return;
        state.cooldownUntil = Date.now() + seconds * 1000;
        clearInterval(cooldownTimer);
        const tick = () => {
            const left = Math.ceil((state.cooldownUntil - Date.now()) / 1000);
            if (left <= 0) {
                clearInterval(cooldownTimer);
                sendBtn.textContent = "Enviar";
                if (canSendNow()) sendBtn.disabled = false;
                return;
            }
            sendBtn.disabled = true;
            sendBtn.textContent = left + " s";
        };
        tick();
        cooldownTimer = setInterval(tick, 1000);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SignalR
    // ═══════════════════════════════════════════════════════════════════

    if (!window.signalR) {
        systemMessage("No se pudo cargar el cliente de chat (signalr.min.js).", "error");
        return;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/streamHub")
        .withAutomaticReconnect()
        .build();

    async function invoke(method, ...args) {
        try {
            const result = await connection.invoke(method, ...args);
            if (result && result.ok === false) systemMessage(result.error || result.errorMessage || "No se pudo completar la acción.", "error");
            // El resto de las acciones ya se anuncia a todo el canal; quitar una sanción no.
            else if (result && result.notice && method === "UnbanUser") systemMessage(result.notice, "notice");
            return result;
        } catch (err) {
            console.error(err);
            systemMessage("No se pudo completar la acción.", "error");
            return null;
        }
    }

    // ── Alertas sobre el video (una a la vez, 4 s cada una) ─────────
    const alertBox = document.getElementById("stream-alert");
    const alertQueue = [];
    let alertShowing = false;

    function queueAlert(kind, title, message) {
        if (!alertBox) return;
        if (alertQueue.length >= 10) alertQueue.shift(); // si llueven alertas, se descartan las más viejas
        alertQueue.push({ kind, title, message });
        if (!alertShowing) showNextAlert();
    }

    function showNextAlert() {
        const next = alertQueue.shift();
        if (!next) { alertShowing = false; alertBox.hidden = true; return; }
        alertShowing = true;
        alertBox.className = "stream-alert " + next.kind;
        alertBox.replaceChildren(el("div", "alert-title", next.title));
        if (next.message) alertBox.append(el("div", "alert-message", next.message));
        alertBox.hidden = false;
        // Reinicia la animación de entrada.
        alertBox.style.animation = "none";
        void alertBox.offsetWidth;
        alertBox.style.animation = "";
        setTimeout(showNextAlert, 4000);
    }

    connection.on("ChatMessage", m => { if (m.channelId === cfg.channelId) append(renderMessage(m)); });

    connection.on("MessageDeleted", id => rows(r => r.dataset.id === id).forEach(markDeleted));

    connection.on("UserTimedOut", (userId, name, seconds) => {
        rows(r => r.dataset.user === userId).forEach(markDeleted);
        systemMessage((name || "Un usuario") + " fue silenciado por " + formatDuration(seconds) + ".");
    });

    connection.on("UserBanned", (userId, name) => {
        rows(r => r.dataset.user === userId).forEach(markDeleted);
        systemMessage((name || "Un usuario") + " fue baneado del chat.");
    });

    connection.on("TimedOut", (channelId, seconds) => {
        if (channelId !== cfg.channelId) return;
        systemMessage("Fuiste silenciado por " + formatDuration(seconds) + ".", "error");
        lockInput(seconds, "Estás silenciado");
    });

    connection.on("Banned", channelId => {
        if (channelId !== cfg.channelId) return;
        clearInterval(inputLockTimer);
        state.inputLockedUntil = Number.MAX_SAFE_INTEGER;
        systemMessage("Fuiste baneado de este chat.", "error");
        setInputEnabled(false, "Estás baneado de este chat");
    });

    connection.on("Unbanned", channelId => {
        if (channelId !== cfg.channelId) return;
        clearInterval(inputLockTimer);
        state.inputLockedUntil = 0;
        setInputEnabled(true);
        systemMessage("Un moderador levantó tu sanción. Ya puedes escribir.");
    });

    connection.on("ChatCleared", () => {
        list.replaceChildren();
        pending = [];
        systemMessage("Un moderador limpió el chat.");
    });

    connection.on("ChatSettingsChanged", settings => applySettings(settings, true));

    connection.on("UserRoleChanged", (userId, name, role) => {
        const text = role === "moderator" ? name + " ahora es moderador."
            : role === "vip" ? name + " ahora es VIP."
            : name + " ya no tiene un rol especial.";
        systemMessage(text);
    });

    // Mi rol cambió: me vuelvo a unir para recibir los permisos nuevos.
    connection.on("RoleChanged", channelId => { if (channelId === cfg.channelId) join().then(rebuildActions); });

    connection.on("PinChanged", m => renderPinned(m));

    connection.on("PollUpdated", poll => { if (poll.channelId === cfg.channelId) renderPoll(poll); });

    connection.on("AutoModHeld", held => {
        if (held.channelId !== cfg.channelId) return;
        state.held.set(held.id, held);
        renderHeld();
    });

    connection.on("AutoModResolved", (heldId, approved, moderatorName) => {
        const held = state.held.get(heldId);
        state.held.delete(heldId);
        renderHeld();
        if (held) systemMessage("AutoMod: " + moderatorName + (approved ? " aprobó" : " rechazó") + " el mensaje de " + held.displayName + ".");
    });

    connection.on("ChatNotice", (channelId, text) => { if (channelId === cfg.channelId) systemMessage(text, "notice"); });

    connection.on("WhisperReceived", (conversationId, senderName, content) => {
        systemMessage("💬 Susurro de " + senderName + ": " + content, "whisper");
    });

    connection.on("RaidStarted", (targetChannelId, targetName, viewers, seconds) => startRaidCountdown(targetChannelId, targetName, viewers, seconds));

    connection.on("RaidIncoming", (fromName, viewers) => {
        systemMessage("🚀 ¡" + fromName + " está raideando el canal con " + viewers + " espectador" + (viewers === 1 ? "" : "es") + "!", "raid");
    });

    connection.on("PointsEarned", amount => {
        setPoints(state.points + amount);
    });

    connection.on("ViewerCountUpdated", setViewerCount);

    connection.on("DonationAlert", (user, points, message) => {
        const div = el("div", "chat-message chat-donation");
        div.append("🎉 ", el("strong", null, user), " donó " + points + " puntos" + (message ? ": " : ""));
        if (message) div.append(document.createTextNode(message));
        append(div);
        queueAlert("donation", "🎉 " + user + " donó " + points + " puntos", message);
    });

    // En Twitch los follows no aparecen en el chat: salen como alerta encima del video.
    connection.on("FollowAlert", user => queueAlert("follow", "💜 " + user + " ahora sigue el canal", null));

    connection.on("StreamStarted", () => location.reload());
    connection.on("StreamEnded", () => location.reload());

    connection.onreconnecting(() => systemMessage("Conexión perdida. Reconectando al chat…"));
    connection.onreconnected(() => { systemMessage("Reconectado."); join().catch(() => { }); });

    async function join() {
        const result = await connection.invoke("JoinChannel", cfg.channelId);
        const couldModerate = state.canModerate;
        state.canModerate = result.canModerate;
        state.role = result.role;
        setViewerCount(result.viewerCount);
        applySettings(result.settings, false);
        renderPinned(result.pinned);
        renderPoll(result.poll);
        setPoints(result.points || 0);
        state.held.clear();
        (result.held || []).forEach(h => state.held.set(h.id, h));
        renderHeld();
        if (couldModerate !== state.canModerate) rebuildActions();
    }

    connection.start()
        .then(join)
        .catch(err => {
            console.error(err);
            systemMessage("No se pudo conectar al chat. Recarga la página.", "error");
        });

    async function sendRaw(text, replyToId) {
        try {
            return await connection.invoke("SendMessage", cfg.channelId, text, replyToId || null);
        } catch (err) {
            console.error(err);
            systemMessage("No se pudo enviar el mensaje. ¿Sigues conectado?", "error");
            return null;
        }
    }

    async function sendMessage() {
        if (!input) return;
        const text = input.value.trim();
        if (!text || !canSendNow()) return;
        if (rulesPending()) { showRules(); return; }

        sendBtn.disabled = true;
        try {
            const result = await sendRaw(text, state.replyTo && state.replyTo.id);
            if (!result) return;
            if (result.ok) {
                input.value = "";
                cancelReply();
                if (result.notice) systemMessage(result.notice, result.heldForReview ? "warning" : "notice");
            } else {
                systemMessage(result.errorMessage || "No se pudo enviar el mensaje.", "error");
                if (result.errorCode === "timed_out" && result.retryAfterSeconds) lockInput(result.retryAfterSeconds, "Estás silenciado");
                else if (result.errorCode === "banned") { state.inputLockedUntil = Number.MAX_SAFE_INTEGER; setInputEnabled(false, "Estás baneado de este chat"); }
                else if (result.retryAfterSeconds) startCooldown(result.retryAfterSeconds);
            }
        } finally {
            if (canSendNow()) sendBtn.disabled = false;
        }
    }

    if (sendBtn && input) {
        sendBtn.addEventListener("click", sendMessage);
        input.addEventListener("keydown", e => {
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
            if (e.key === "Escape") cancelReply();
        });
        input.addEventListener("focus", () => { if (rulesPending()) showRules(); });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Viewers
    // ═══════════════════════════════════════════════════════════════════

    function setViewerCount(count) {
        const counter = $("viewer-count");
        if (counter) counter.textContent = count;
    }

    const viewersModal = $("viewers-modal");

    async function openViewers() {
        if (!viewersModal) return;
        viewersModal.style.display = "flex";
        const listEl = $("viewers-modal-list");
        try {
            const data = await connection.invoke("GetViewers", cfg.channelId);
            $("viewers-modal-count").textContent = data.count;
            listEl.replaceChildren();
            if (data.count === 0) { listEl.appendChild(el("p", "viewers-empty", "Nadie está viendo este canal todavía.")); return; }
            data.names.forEach(name => listEl.appendChild(el("div", "viewer-row", "👤 " + name)));
            if (data.anonymousCount > 0) {
                listEl.appendChild(el("div", "viewer-row viewer-row-anon",
                    "+ " + data.anonymousCount + " espectador" + (data.anonymousCount === 1 ? "" : "es") + " sin sesión"));
            }
        } catch (err) { console.error(err); }
    }

    $("viewer-pill")?.addEventListener("click", openViewers);
    $("viewers-modal-close")?.addEventListener("click", () => { viewersModal.style.display = "none"; });
    viewersModal?.addEventListener("click", e => { if (e.target === viewersModal) viewersModal.style.display = "none"; });
})();
