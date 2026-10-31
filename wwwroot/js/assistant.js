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
    var token = widget.getAttribute('data-token');
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

    function ask(question) {
        if (busy || !question) return;
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
        ask(input.value.trim());
    });
})();
