// Floating budget assistant. Posts questions to /Chat/AskStream and renders the reply as it streams in.
(function () {
    'use strict';

    var widget = document.getElementById('assistantWidget');
    if (!widget) return;

    var panel = document.getElementById('assistantPanel');
    var log = document.getElementById('assistantLog');
    var form = document.getElementById('assistantForm');
    var input = document.getElementById('assistantInput');
    var sendBtn = document.getElementById('assistantSend');
    var streamUrl = widget.getAttribute('data-stream-url');
    var resetUrl = widget.getAttribute('data-reset-url');
    var outcomeUrl = widget.getAttribute('data-outcome-url');
    var token = widget.getAttribute('data-token');
    var transcribeUrl = widget.getAttribute('data-transcribe-url');
    var speakUrl = widget.getAttribute('data-speak-url');
    var micBtn = document.getElementById('assistantMic');
    var speakerBtn = document.getElementById('assistantSpeaker');
    var busy = false;

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // Minimal markdown: pipe tables, bold, inline code, bullets and line breaks.
    function render(text) {
        var lines = escapeHtml(text).split('\n');
        var html = '';
        var i = 0;

        while (i < lines.length) {
            var line = lines[i];
            var isTableRow = /^\s*\|.*\|\s*$/.test(line);
            var isSeparator = i + 1 < lines.length && /^\s*\|[\s:|-]+\|\s*$/.test(lines[i + 1]);

            if (isTableRow && isSeparator) {
                var cells = function (row) {
                    return row.trim().replace(/^\||\|$/g, '').split('|').map(function (c) { return c.trim(); });
                };
                var header = cells(line);
                html += '<table><thead><tr>' + header.map(function (c) {
                    return '<th>' + c + '</th>';
                }).join('') + '</tr></thead><tbody>';
                i += 2;
                while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) {
                    // Keep every row the width of the header so short rows cannot shift columns.
                    var row = cells(lines[i]).slice(0, header.length);
                    while (row.length < header.length) row.push('');
                    html += '<tr>' + row.map(function (c) { return '<td>' + c + '</td>'; }).join('') + '</tr>';
                    i++;
                }
                html += '</tbody></table>';
                continue;
            }

            html += line + '\n';
            i++;
        }

        return html
            .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
            .replace(/`([^`]+)`/g, '<code>$1</code>')
            .replace(/\n/g, '<br>');
    }

    function append(text, cssClass, asHtml) {
        var div = document.createElement('div');
        div.className = 'assistant-msg ' + cssClass;
        if (asHtml) {
            div.innerHTML = render(text);
        } else {
            div.textContent = text;
        }
        log.appendChild(div);
        log.scrollTop = log.scrollHeight;
        return div;
    }

    function setBusy(state) {
        busy = state;
        sendBtn.disabled = state;
        input.disabled = state;
        if (micBtn && !recorder) micBtn.disabled = state;
    }

    // ---- Voice: read answers aloud ----

    var readAloud = false;
    var player = null;
    try { readAloud = localStorage.getItem('assistantReadAloud') === '1'; } catch (e) { /* storage blocked */ }

    // Bumped whenever audio should stop, so a reply still being generated is dropped on arrival.
    var speechId = 0;

    function stopSpeaking() {
        speechId++;
        if (player) {
            player.pause();
            URL.revokeObjectURL(player.src);
            player = null;
        }
    }

    function showSpeakerState() {
        if (!speakerBtn) return;
        speakerBtn.setAttribute('aria-pressed', readAloud ? 'true' : 'false');
        speakerBtn.title = readAloud ? 'Stop reading answers aloud' : 'Read answers aloud';
        speakerBtn.innerHTML = '<i class="bi ' + (readAloud ? 'bi-volume-up' : 'bi-volume-mute') + '"></i>';
        speakerBtn.classList.toggle('active', readAloud);
    }

    // Tables and markdown symbols sound wrong when read out, so speak plain text only.
    function speechText(text) {
        return text
            .split('\n')
            .filter(function (line) { return !/^\s*\|[\s:|-]+\|\s*$/.test(line); })
            .map(function (line) { return line.replace(/^\s*\|/, '').replace(/\|\s*$/, '').replace(/\|/g, ', '); })
            .join('\n')
            .replace(/[*`#_]/g, '')
            .trim();
    }

    // A question asked by voice is always answered aloud; typed ones only when the toggle is on.
    function speak(text, spokenQuestion) {
        if ((!readAloud && !spokenQuestion) || !speakUrl) return;
        var plain = speechText(text);
        if (!plain) return;
        stopSpeaking();
        var id = speechId;

        fetch(speakUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify({ text: plain })
        }).then(function (response) {
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.blob();
        }).then(function (blob) {
            if (id !== speechId) return;
            player = new Audio(URL.createObjectURL(blob));
            player.addEventListener('ended', stopSpeaking);
            return player.play();
        }).catch(function () { /* the answer is already on screen; audio is a bonus */ });
    }

    if (speakerBtn) {
        showSpeakerState();
        speakerBtn.addEventListener('click', function () {
            readAloud = !readAloud;
            try { localStorage.setItem('assistantReadAloud', readAloud ? '1' : '0'); } catch (e) { /* storage blocked */ }
            if (!readAloud) stopSpeaking();
            showSpeakerState();
        });
    }

    // ---- Voice: speak a question ----

    var recorder = null;
    var recordTimer = null;
    var MAX_RECORD_MS = 60000;

    function showMicState(recording) {
        micBtn.classList.toggle('assistant-recording', recording);
        micBtn.setAttribute('aria-pressed', recording ? 'true' : 'false');
        micBtn.title = recording ? 'Stop and send' : 'Speak your question';
        micBtn.innerHTML = '<i class="bi ' + (recording ? 'bi-stop-fill' : 'bi-mic') + '"></i>';
    }

    // 16 kHz mono 16-bit PCM: what speech models use internally, and about 2 MB a minute.
    var WAV_RATE = 16000;

    function encodeWav(samples) {
        var buffer = new ArrayBuffer(44 + samples.length * 2);
        var view = new DataView(buffer);
        function text(offset, s) { for (var i = 0; i < s.length; i++) view.setUint8(offset + i, s.charCodeAt(i)); }

        text(0, 'RIFF');
        view.setUint32(4, 36 + samples.length * 2, true);
        text(8, 'WAVE');
        text(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);            // PCM
        view.setUint16(22, 1, true);            // mono
        view.setUint32(24, WAV_RATE, true);
        view.setUint32(28, WAV_RATE * 2, true); // bytes per second
        view.setUint16(32, 2, true);            // bytes per frame
        view.setUint16(34, 16, true);           // bits per sample
        text(36, 'data');
        view.setUint32(40, samples.length * 2, true);

        for (var i = 0; i < samples.length; i++) {
            var s = Math.max(-1, Math.min(1, samples[i]));
            view.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
        }
        return new Blob([view], { type: 'audio/wav' });
    }

    // OpenAI rejects some browser recordings (Firefox's Ogg/Opus answers "corrupted or
    // unsupported"), so every recording is re-encoded as WAV, which it always accepts.
    function toWav(blob) {
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx || !window.OfflineAudioContext) return Promise.reject(new Error('no Web Audio'));

        return blob.arrayBuffer().then(function (data) {
            var ctx = new Ctx();
            return new Promise(function (resolve, reject) {
                ctx.decodeAudioData(data, resolve, reject);
            }).finally(function () { ctx.close(); });
        }).then(function (decoded) {
            var offline = new OfflineAudioContext(1, Math.ceil(decoded.duration * WAV_RATE), WAV_RATE);
            var source = offline.createBufferSource();
            source.buffer = decoded;
            source.connect(offline.destination);
            source.start();
            return offline.startRendering();
        }).then(function (rendered) {
            return encodeWav(rendered.getChannelData(0));
        });
    }

    function sendRecording(blob, type) {
        var pending = append('Listening…', 'assistant-msg-bot assistant-typing', false);
        setBusy(true);

        var extension = type.indexOf('mp4') >= 0 ? 'mp4' : type.indexOf('ogg') >= 0 ? 'ogg' : 'webm';

        toWav(blob).then(function (wav) {
            return { blob: wav, name: 'question.wav' };
        }, function () {
            return { blob: blob, name: 'question.' + extension };
        }).then(function (upload) {
            var body = new FormData();
            body.append('audio', upload.blob, upload.name);
            return fetch(transcribeUrl, {
                method: 'POST',
                headers: { 'RequestVerificationToken': token },
                body: body
            });
        }).then(function (response) {
            if (response.status === 429) return { ok: false, error: 'Too many voice requests. Please wait a minute.' };
            if (!response.ok) throw new Error('HTTP ' + response.status);
            return response.json();
        }).then(function (data) {
            pending.remove();
            setBusy(false);
            if (data.ok) {
                ask(data.text, true);
            } else {
                append(data.error || 'Voice input failed.', 'assistant-msg-error', false);
            }
        }).catch(function () {
            pending.remove();
            setBusy(false);
            append('Voice input failed. Please type your question instead.', 'assistant-msg-error', false);
        });
    }

    function startRecording() {
        stopSpeaking();
        navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
            var chunks = [];
            recorder = new MediaRecorder(stream);
            recorder.addEventListener('dataavailable', function (e) {
                if (e.data && e.data.size) chunks.push(e.data);
            });
            recorder.addEventListener('stop', function () {
                var type = recorder.mimeType || 'audio/webm';
                stream.getTracks().forEach(function (t) { t.stop(); });
                clearTimeout(recordTimer);
                recorder = null;
                showMicState(false);
                if (chunks.length) sendRecording(new Blob(chunks, { type: type }), type);
            });
            recorder.start();
            showMicState(true);
            recordTimer = setTimeout(stopRecording, MAX_RECORD_MS);
        }).catch(function () {
            append('Microphone access was blocked. Allow the microphone for this site in your browser, then try again.', 'assistant-msg-error', false);
        });
    }

    function stopRecording() {
        if (recorder && recorder.state !== 'inactive') recorder.stop();
    }

    // Live conversation (OpenAI Realtime over WebRTC) when the server offers it and the browser
    // can do it; otherwise the microphone records one question at a time.
    var liveAvailable = widget.getAttribute('data-realtime') === '1' && !!window.RTCPeerConnection
        && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);

    if (micBtn) {
        if (liveAvailable) {
            micBtn.addEventListener('click', function () {
                if (live || liveStarting) endLive();
                else startLive();
            });
        } else if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.MediaRecorder) {
            micBtn.remove();
            micBtn = null;
        } else {
            micBtn.addEventListener('click', function () {
                if (recorder) stopRecording();
                else if (!busy) startRecording();
            });
        }
    }

    var toolLabels = {
        get_user_scope: 'Checking your access',
        get_budget_summary: 'Summarising the budget',
        search_budget_lines: 'Searching budget lines',
        get_budget_vs_actual: 'Comparing budget with actuals',
        get_kpis: 'Looking up KPIs',
        get_activity_unit_cost: 'Calculating activity unit costs',
        get_pbb_maturity: 'Checking PBB maturity',
        list_master_data: 'Reading master data',
        search_pbb_reference: 'Searching the PBB reference',
        oecd_find_dataflow: 'Searching OECD datasets',
        oecd_data_query: 'Querying OECD data',
        oecd_read_page: 'Reading an OECD page'
    };

    function showStatus(div, text) {
        div.className = 'assistant-msg assistant-msg-bot assistant-typing';
        div.textContent = text;
        log.scrollTop = log.scrollHeight;
    }

    function showReply(div, text, cssClass) {
        div.className = 'assistant-msg ' + cssClass;
        div.innerHTML = render(text);
        log.scrollTop = log.scrollHeight;
    }

    // Reads a text/event-stream body and calls onEvent(name, data) for each event.
    function readEvents(response, onEvent) {
        var reader = response.body.getReader();
        var decoder = new TextDecoder();
        var buffer = '';

        function dispatch(block) {
            var name = 'message';
            var data = '';
            block.split('\n').forEach(function (line) {
                if (line.indexOf('event:') === 0) name = line.slice(6).trim();
                else if (line.indexOf('data:') === 0) data += line.slice(5).trim();
            });
            if (data) onEvent(name, JSON.parse(data));
        }

        function pump() {
            return reader.read().then(function (chunk) {
                if (chunk.done) {
                    if (buffer.trim()) dispatch(buffer);
                    return;
                }
                buffer += decoder.decode(chunk.value, { stream: true }).replace(/\r/g, '');
                var end;
                while ((end = buffer.indexOf('\n\n')) >= 0) {
                    dispatch(buffer.slice(0, end));
                    buffer = buffer.slice(end + 2);
                }
                return pump();
            });
        }

        return pump();
    }

    function el(tag, cssClass, text) {
        var node = document.createElement(tag);
        if (cssClass) node.className = cssClass;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function reportOutcome(proposal, ok, message) {
        // In a live conversation the model hears what happened, so it can tell the user.
        tellLive('The user ' + (message === 'Cancelled by the user.' ? 'cancelled' : 'confirmed') + ' the card "' +
            (proposal.summary || proposal.title) + '". Result: ' + (ok ? 'saved' : 'not saved') + ' - ' + message);
        return fetch(outcomeUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify({ title: proposal.summary || proposal.title, ok: ok, message: message })
        }).catch(function () { /* the change itself already happened or failed; history is best effort */ });
    }

    // Posts the confirmed form to the screen's own action. The server answers flagged requests
    // with { ok, message } instead of a redirect or a re-rendered page.
    function execute(proposal) {
        var body = new URLSearchParams();
        proposal.form.forEach(function (pair) { body.append(pair.key, pair.value); });
        body.append('__RequestVerificationToken', token);

        return fetch(proposal.url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
                'RequestVerificationToken': token,
                'X-Assistant-Action': '1',
                'X-Requested-With': 'XMLHttpRequest',
                'Accept': 'application/json'
            },
            body: body
        }).then(function (response) {
            var isJson = (response.headers.get('Content-Type') || '').indexOf('json') >= 0;
            return (isJson ? response.json() : Promise.resolve(null)).then(function (data) {
                var ok = response.ok && !(data && (data.ok === false || data.success === false || data.error));
                var message = (data && (data.message || data.error)) || (ok ? 'Saved.' : 'The screen rejected the change (HTTP ' + response.status + ').');
                return { ok: ok, message: message };
            });
        }).catch(function () {
            return { ok: false, message: 'The change could not be sent. Check your connection and try again.' };
        });
    }

    function proposalCard(proposal) {
        var card = el('div', 'assistant-proposal assistant-proposal-' + proposal.kind);
        card.appendChild(el('div', 'assistant-proposal-title', proposal.title));
        card.appendChild(el('div', 'assistant-proposal-summary', proposal.summary));
        if (proposal.record) card.appendChild(el('div', 'assistant-proposal-record', proposal.record));

        if (proposal.changes.length) {
            var hasPrevious = proposal.changes.some(function (c) { return c.previous !== null && c.previous !== undefined; });
            var table = el('table');
            var head = el('tr');
            head.appendChild(el('th', null, 'Field'));
            head.appendChild(el('th', null, 'New value'));
            if (hasPrevious) head.appendChild(el('th', null, 'Was'));
            table.appendChild(head);
            proposal.changes.forEach(function (c) {
                var row = el('tr');
                row.appendChild(el('td', null, c.name));
                row.appendChild(el('td', null, c.value));
                if (hasPrevious) row.appendChild(el('td', null, c.previous === null ? '' : c.previous));
                table.appendChild(row);
            });
            card.appendChild(table);
        }

        var actions = el('div', 'assistant-proposal-actions');
        var confirm = el('button', 'btn btn-sm ' + (proposal.kind === 'delete' ? 'btn-danger' : 'btn-primary'), 'Confirm');
        var cancel = el('button', 'btn btn-sm btn-outline-secondary', 'Cancel');
        confirm.type = cancel.type = 'button';
        actions.appendChild(confirm);
        actions.appendChild(cancel);
        card.appendChild(actions);
        var result = el('div', 'assistant-proposal-result');
        card.appendChild(result);

        function finish(outcome) {
            actions.remove();
            result.textContent = (outcome.ok ? '✓ ' : '✗ ') + outcome.message;
            result.className = 'assistant-proposal-result ' + (outcome.ok ? 'text-success' : 'text-danger');
            if (outcome.ok) {
                var refresh = el('button', 'btn btn-link btn-sm p-0 ms-2', 'Refresh page');
                refresh.type = 'button';
                refresh.addEventListener('click', function () { window.location.reload(); });
                result.appendChild(refresh);
            }
            card.dataset.done = '1';
        }

        card.run = function () {
            if (card.dataset.done) return Promise.resolve();
            confirm.disabled = cancel.disabled = true;
            result.textContent = 'Saving…';
            return execute(proposal).then(function (outcome) {
                finish(outcome);
                return reportOutcome(proposal, outcome.ok, outcome.message);
            });
        };

        confirm.addEventListener('click', function () { card.run(); });
        cancel.addEventListener('click', function () {
            finish({ ok: false, message: 'Cancelled. Nothing was saved.' });
            reportOutcome(proposal, false, 'Cancelled by the user.');
        });

        return card;
    }

    function showProposals(proposals) {
        if (!proposals.length) return;
        var wrap = el('div', 'assistant-proposals');
        var cards = proposals.map(proposalCard);

        if (cards.length > 1) {
            var all = el('button', 'btn btn-sm btn-primary assistant-confirm-all', 'Confirm all ' + cards.length);
            all.type = 'button';
            all.addEventListener('click', function () {
                all.remove();
                // One at a time, in order, so dependent changes (e.g. switch context, then add) apply correctly.
                cards.reduce(function (chain, card) {
                    return chain.then(function () { return card.run(); });
                }, Promise.resolve());
            });
            wrap.appendChild(all);
        }

        cards.forEach(function (card) { wrap.appendChild(card); });
        log.appendChild(wrap);
        log.scrollTop = log.scrollHeight;
    }

    function ask(question, spoken) {
        if (busy || !question) return;
        stopSpeaking();
        append(question, 'assistant-msg-user', false);
        input.value = '';
        setBusy(true);

        var pending = append('Thinking', 'assistant-msg-bot assistant-typing', false);
        var text = '';
        var finished = false;

        fetch(streamUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify({ message: question })
        }).then(function (response) {
            if (!response.ok || !response.body) throw new Error('HTTP ' + response.status);
            return readEvents(response, function (name, data) {
                if (name === 'tool') {
                    showStatus(pending, toolLabels[data.name] || 'Looking up data');
                } else if (name === 'delta') {
                    text += data.text;
                    showReply(pending, text, 'assistant-msg-bot');
                } else if (name === 'reset') {
                    text = '';
                    showStatus(pending, 'Thinking');
                } else if (name === 'done') {
                    finished = true;
                    showReply(pending, data.reply, data.ok ? 'assistant-msg-bot' : 'assistant-msg-error');
                    showProposals(data.proposals || []);
                    if (data.ok) speak(data.reply, spoken);
                }
            });
        }).then(function () {
            if (!finished) throw new Error('Stream ended early');
        }).catch(function () {
            showReply(pending, 'The assistant is unavailable right now. Please try again.', 'assistant-msg-error');
        }).finally(function () {
            setBusy(false);
            input.focus();
        });
    }

    // ---- Live voice conversation ----
    //
    // The server mints a short-lived key (/Chat/RealtimeSession); the browser then connects to
    // OpenAI over WebRTC, so speech goes both ways in real time and the user can interrupt.
    // Every tool the model calls comes back to /Chat/RealtimeTool, which runs it with the user's
    // own scope. Changes still arrive as cards that only a click on Confirm saves.

    var statusLine = document.getElementById('assistantStatus');
    var statusDefault = statusLine ? statusLine.textContent : '';
    var live = null;
    var liveStarting = false;
    var liveAttempt = 0; // bumped by endLive, so a connection still being set up is dropped
    var liveBubble = null;
    var liveText = '';

    function setLiveStatus(text) {
        if (statusLine) statusLine.textContent = text || statusDefault;
    }

    function showLiveState(state) {
        if (!micBtn) return;
        var on = state !== 'off';
        micBtn.classList.toggle('assistant-recording', on);
        micBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        micBtn.title = on ? 'End the conversation' : 'Talk with the assistant';
        micBtn.innerHTML = '<i class="bi ' + (on ? 'bi-mic-fill' : 'bi-mic') + '"></i>';
    }

    function postJson(url, data) {
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify(data || {})
        }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (body) {
                if (!response.ok) throw new Error(body.error || ('HTTP ' + response.status));
                return body;
            });
        });
    }

    function logLive(role, text) {
        if (text && text.trim()) {
            postJson(widget.getAttribute('data-realtime-log-url'), { role: role, text: text.trim() }).catch(function () { /* best effort */ });
        }
    }

    function sendLive(event) {
        if (live && live.dc.readyState === 'open') live.dc.send(JSON.stringify(event));
    }

    function tellLive(text) {
        if (!live) return;
        sendLive({ type: 'conversation.item.create', item: { type: 'message', role: 'system', content: [{ type: 'input_text', text: text }] } });
        sendLive({ type: 'response.create' });
    }

    function sendLiveText(text) {
        append(text, 'assistant-msg-user', false);
        input.value = '';
        sendLive({ type: 'conversation.item.create', item: { type: 'message', role: 'user', content: [{ type: 'input_text', text: text }] } });
        sendLive({ type: 'response.create' });
        logLive('user', text);
    }

    function startLive() {
        if (live || liveStarting || busy) return;
        liveStarting = true;
        var attempt = ++liveAttempt;
        stopSpeaking();
        showLiveState('connecting');
        setLiveStatus('Connecting…');
        var mic = null;
        var pc = null;

        navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
            mic = stream;
            if (attempt !== liveAttempt) throw new Error('cancelled');
            return postJson(widget.getAttribute('data-realtime-session-url'), { screen: window.location.pathname });
        }).then(function (info) {
            pc = new RTCPeerConnection();
            var audio = document.createElement('audio');
            audio.autoplay = true;
            pc.ontrack = function (event) { audio.srcObject = event.streams[0]; };
            mic.getTracks().forEach(function (track) { pc.addTrack(track, mic); });

            var dc = pc.createDataChannel('oai-events');
            var s = { pc: pc, dc: dc, mic: mic, audio: audio, timer: null };

            dc.addEventListener('message', function (event) {
                var data;
                try { data = JSON.parse(event.data); } catch (e) { return; }
                if (live === s) handleLiveEvent(data);
            });
            pc.addEventListener('connectionstatechange', function () {
                if ((pc.connectionState === 'failed' || pc.connectionState === 'disconnected') && live === s) {
                    append('The live conversation dropped. Tap the microphone to start again.', 'assistant-msg-error', false);
                    endLive();
                }
            });

            return pc.createOffer().then(function (offer) {
                return pc.setLocalDescription(offer).then(function () { return offer; });
            }).then(function (offer) {
                return fetch(widget.getAttribute('data-realtime-calls-url'), {
                    method: 'POST',
                    body: offer.sdp,
                    headers: { 'Authorization': 'Bearer ' + info.clientSecret, 'Content-Type': 'application/sdp' }
                });
            }).then(function (response) {
                if (!response.ok) throw new Error('OpenAI did not accept the connection (' + response.status + ').');
                return response.text();
            }).then(function (sdp) {
                return pc.setRemoteDescription({ type: 'answer', sdp: sdp });
            }).then(function () {
                return new Promise(function (resolve, reject) {
                    if (dc.readyState === 'open') { resolve(); return; }
                    dc.addEventListener('open', resolve, { once: true });
                    setTimeout(function () { reject(new Error('The assistant did not answer in time.')); }, 15000);
                });
            }).then(function () {
                if (attempt !== liveAttempt) throw new Error('cancelled');
                liveStarting = false;
                live = s;
                s.timer = setTimeout(function () {
                    append('Live conversations last ' + info.maxMinutes + ' minutes. Tap the microphone to start a new one.', 'assistant-msg-bot', false);
                    endLive();
                }, info.maxMinutes * 60000);
                showLiveState('on');
                setLiveStatus('Listening… speak naturally. Tap the microphone to end.');
            });
        }).catch(function (error) {
            if (pc) { try { pc.close(); } catch (e) { /* already closed */ } }
            if (mic) mic.getTracks().forEach(function (t) { t.stop(); });
            if (attempt !== liveAttempt) return; // the user ended it while it was connecting
            liveStarting = false;
            showLiveState('off');
            setLiveStatus('');
            var message = error && error.name === 'NotAllowedError'
                ? 'Microphone access was blocked. Allow the microphone for this site in your browser, then try again. You can still type.'
                : ((error && error.message) || 'The live conversation could not start.') + ' You can still type your questions.';
            append(message, 'assistant-msg-error', false);
        });
    }

    function endLive() {
        var s = live;
        live = null;
        liveAttempt++;
        liveStarting = false;
        liveBubble = null;
        showLiveState('off');
        setLiveStatus('');
        if (!s) return;
        clearTimeout(s.timer);
        try { s.dc.close(); } catch (e) { /* already closed */ }
        try { s.pc.close(); } catch (e) { /* already closed */ }
        s.mic.getTracks().forEach(function (t) { t.stop(); });
        s.audio.srcObject = null;
    }

    function handleLiveEvent(event) {
        switch (event.type) {
            case 'input_audio_buffer.speech_started':
                setLiveStatus('Listening…');
                break;
            case 'conversation.item.input_audio_transcription.completed':
                if (event.transcript && event.transcript.trim()) {
                    // The transcript can arrive after the answer has started; keep it above the answer.
                    var said = el('div', 'assistant-msg assistant-msg-user', event.transcript.trim());
                    if (liveBubble && liveBubble.parentNode === log) log.insertBefore(said, liveBubble);
                    else log.appendChild(said);
                    log.scrollTop = log.scrollHeight;
                    logLive('user', event.transcript);
                }
                break;
            case 'response.created':
                setLiveStatus('Thinking…');
                liveBubble = null;
                liveText = '';
                break;
            case 'response.output_audio_transcript.delta':
            case 'response.output_text.delta':
                if (!liveBubble) liveBubble = append('', 'assistant-msg-bot', false);
                liveText += event.delta || '';
                liveBubble.textContent = liveText;
                log.scrollTop = log.scrollHeight;
                setLiveStatus('Speaking… you can interrupt at any time.');
                break;
            case 'response.output_audio_transcript.done':
            case 'response.output_text.done':
                if (liveBubble && liveText) {
                    liveBubble.innerHTML = render(liveText);
                    logLive('assistant', liveText);
                }
                liveBubble = null;
                liveText = '';
                break;
            case 'response.done':
                onLiveResponseDone(event.response || {});
                break;
            case 'error':
                // Interrupting while nothing is playing is harmless and not worth showing.
                if (event.error && event.error.code !== 'response_cancel_not_active') {
                    append(event.error.message || 'The assistant reported an error.', 'assistant-msg-error', false);
                }
                break;
        }
    }

    function onLiveResponseDone(response) {
        var calls = (response.output || []).filter(function (item) { return item.type === 'function_call'; });
        if (!calls.length) {
            if (live) setLiveStatus('Listening… speak naturally. Tap the microphone to end.');
            return;
        }

        var s = live;
        setLiveStatus(toolLabels[calls[0].name] || 'Looking that up…');
        Promise.all(calls.map(function (call) {
            return postJson(widget.getAttribute('data-realtime-tool-url'), { name: call.name, arguments: call.arguments })
                .then(function (result) {
                    if (result.proposals && result.proposals.length) showProposals(result.proposals);
                    return { call: call, output: result.output || '{}' };
                })
                .catch(function (error) {
                    return { call: call, output: JSON.stringify({ error: error.message }) };
                });
        })).then(function (results) {
            if (live !== s) return;
            results.forEach(function (r) {
                sendLive({ type: 'conversation.item.create', item: { type: 'function_call_output', call_id: r.call.call_id, output: r.output } });
            });
            sendLive({ type: 'response.create' });
        });
    }

    window.addEventListener('pagehide', endLive);

    document.getElementById('assistantLauncher').addEventListener('click', function () {
        var open = panel.hasAttribute('hidden');
        if (open) {
            panel.removeAttribute('hidden');
            input.focus();
        } else {
            panel.setAttribute('hidden', '');
        }
        this.setAttribute('aria-expanded', open ? 'true' : 'false');
    });

    document.getElementById('assistantClose').addEventListener('click', function () {
        panel.setAttribute('hidden', '');
        document.getElementById('assistantLauncher').setAttribute('aria-expanded', 'false');
        stopRecording();
        stopSpeaking();
        endLive();
    });

    document.getElementById('assistantReset').addEventListener('click', function () {
        fetch(resetUrl, { method: 'POST', headers: { 'RequestVerificationToken': token } })
            .finally(function () {
                log.innerHTML = '';
                append('Conversation cleared. Ask me anything about your budget or OECD performance budgeting.', 'assistant-msg-bot', false);
            });
    });

    log.addEventListener('click', function (event) {
        var suggestion = event.target.closest('.assistant-suggestion');
        if (suggestion) ask(suggestion.textContent.trim());
    });

    form.addEventListener('submit', function (event) {
        event.preventDefault();
        var text = input.value.trim();
        if (!text) return;
        // During a live conversation, typing joins the same conversation.
        if (live) sendLiveText(text);
        else ask(text);
    });
})();
